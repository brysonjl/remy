import Foundation
import CoreFoundation

nonisolated enum CleanupAnswerFormat: Sendable {
    case compatibleSSE, ollamaNDJSON, compatibleJSON, ollamaJSON
}
nonisolated enum CleanupAnswerEvent: Equatable, Sendable {
    /// Provisional answer only. Not eligible for history/output until completed.
    case answerDelta(String)
    case completed(String)
}
nonisolated enum CleanupAnswerParseError: Error, Equatable {
    case invalidLimits, responseTooLarge, recordTooLarge, answerTooLarge
    case invalidUTF8, malformed, refused, toolCall, unsupportedContent
    case incomplete, alreadyFinished, cancelled
}

/// Incremental bytes, no network or credentials. SSE requires an explicit stop
/// finish reason AND [DONE]; Ollama requires done:true. EOF never implies success.
/// Reasoning fields are ignored; tool/refusal completion fails without emitting
/// their content. Errors permanently invalidate this parser instance.
nonisolated struct CleanupAnswerParser {
    struct Limits: Sendable {
        let responseBytes: Int
        let recordBytes: Int
        let answerBytes: Int
        init(responseBytes: Int = 2_097_152, recordBytes: Int = 65_536, answerBytes: Int = 262_144) {
            self.responseBytes = responseBytes; self.recordBytes = recordBytes; self.answerBytes = answerBytes
        }
    }
    private let format: CleanupAnswerFormat
    private let limits: Limits
    private var totalBytes = 0
    private var pending = Data()
    private var eventData: [String] = []
    private var eventBytes = 0
    private var eventType = ""
    private var answer = ""
    private var answerBytes = 0
    private var stopped = false
    private var transportDone = false
    private var terminal = false
    private var cancelled = false

    init(format: CleanupAnswerFormat, limits: Limits = .init()) throws {
        guard limits.responseBytes > 0, limits.recordBytes > 0, limits.answerBytes > 0,
              limits.recordBytes <= limits.responseBytes else { throw CleanupAnswerParseError.invalidLimits }
        self.format = format; self.limits = limits
    }

    /// The transport owner must also cancel its network task. Parser cancellation
    /// revokes local finalization and discards buffered provisional content.
    mutating func cancel() {
        cancelled = true; terminal = true
        pending.removeAll(keepingCapacity: false)
        eventData.removeAll(keepingCapacity: false)
        answer = ""
    }

    mutating func append(_ bytes: Data) throws -> [CleanupAnswerEvent] {
        guard !terminal else { throw cancelled ? CleanupAnswerParseError.cancelled : CleanupAnswerParseError.alreadyFinished }
        do {
            guard bytes.count <= limits.responseBytes - totalBytes else { throw CleanupAnswerParseError.responseTooLarge }
            totalBytes += bytes.count
            pending.append(bytes)
            if format == .compatibleJSON || format == .ollamaJSON { return [] }
            var events: [CleanupAnswerEvent] = []
            while let newline = pending.firstIndex(where: { $0 == 10 || $0 == 13 }) {
                // A CR at a chunk boundary may be the first half of CRLF.
                if pending[newline] == 13, pending.index(after: newline) == pending.endIndex { break }
                let line = Data(pending[..<newline])
                var end = pending.index(after: newline)
                if pending[newline] == 13, end < pending.endIndex, pending[end] == 10 { end = pending.index(after: end) }
                pending.removeSubrange(..<end)
                guard line.count <= limits.recordBytes else { throw CleanupAnswerParseError.recordTooLarge }
                guard let text = String(data: line, encoding: .utf8) else { throw CleanupAnswerParseError.invalidUTF8 }
                events += try consumeLine(text)
            }
            guard pending.count - (pending.last == 13 ? 1 : 0) <= limits.recordBytes else { throw CleanupAnswerParseError.recordTooLarge }
            return events
        } catch {
            terminal = true
            throw error
        }
    }

    mutating func finish() throws -> CleanupAnswerEvent {
        guard !terminal else { throw cancelled ? CleanupAnswerParseError.cancelled : CleanupAnswerParseError.alreadyFinished }
        defer { terminal = true }
        if (format == .compatibleSSE || format == .ollamaNDJSON), pending.last == 13 {
            let line = Data(pending.dropLast())
            guard line.count <= limits.recordBytes else { throw CleanupAnswerParseError.recordTooLarge }
            guard let text = String(data: line, encoding: .utf8) else { throw CleanupAnswerParseError.invalidUTF8 }
            pending.removeAll(keepingCapacity: false)
            _ = try consumeLine(text)
        }
        switch format {
        case .compatibleJSON:
            _ = try compatibleObject(object(pending), streaming: false)
        case .ollamaJSON:
            _ = try ollamaObject(object(pending))
        case .ollamaNDJSON:
            if !pending.isEmpty {
                guard pending.count <= limits.recordBytes else { throw CleanupAnswerParseError.recordTooLarge }
                guard let text = String(data: pending, encoding: .utf8) else { throw CleanupAnswerParseError.invalidUTF8 }
                if !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    _ = try ollamaObject(object(pending))
                }
            }
        case .compatibleSSE:
            // A final unterminated event is not an SSE completion marker.
            guard pending.isEmpty, eventData.isEmpty, transportDone else { throw CleanupAnswerParseError.incomplete }
        }
        guard stopped, !answer.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw CleanupAnswerParseError.incomplete }
        return .completed(answer)
    }

    private mutating func consumeLine(_ line: String) throws -> [CleanupAnswerEvent] {
        if format == .ollamaNDJSON {
            if line.trimmingCharacters(in: .whitespaces).isEmpty { return [] }
            return try ollamaObject(object(Data(line.utf8)))
        }
        if line.isEmpty {
            defer { eventType = "" }
            guard !eventData.isEmpty else { return [] }
            guard eventType.isEmpty || eventType == "message" else { throw CleanupAnswerParseError.unsupportedContent }
            let value = eventData.joined(separator: "\n")
            eventData = []; eventBytes = 0
            if value == "[DONE]" {
                guard stopped, !transportDone else { throw CleanupAnswerParseError.incomplete }
                transportDone = true
                return []
            }
            guard !transportDone else { throw CleanupAnswerParseError.malformed }
            return try compatibleObject(object(Data(value.utf8)), streaming: true)
        }
        if line.hasPrefix(":") { return [] }
        if line.hasPrefix("event:") {
            eventType = String(line.dropFirst(6)).trimmingCharacters(in: .whitespaces)
            return []
        }
        if line == "data" || line.hasPrefix("data:") {
            var value = line == "data" ? "" : String(line.dropFirst(5))
            if value.hasPrefix(" ") { value.removeFirst() }
            eventBytes += value.utf8.count + 1
            guard eventBytes <= limits.recordBytes else { throw CleanupAnswerParseError.recordTooLarge }
            eventData.append(value)
        }
        // Other SSE fields (id/retry) carry transport metadata, never answer content.
        return []
    }

    private func object(_ data: Data) throws -> [String: Any] {
        guard String(data: data, encoding: .utf8) != nil else { throw CleanupAnswerParseError.invalidUTF8 }
        do {
            guard let value = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw CleanupAnswerParseError.malformed }
            return value
        } catch { throw CleanupAnswerParseError.malformed }
    }

    private mutating func compatibleObject(_ value: [String: Any], streaming: Bool) throws -> [CleanupAnswerEvent] {
        guard value["error"] == nil, let choices = value["choices"] as? [[String: Any]], choices.count <= 1 else {
            throw CleanupAnswerParseError.malformed
        }
        // Usage-only events are allowed after stop but are not completion proof.
        if choices.isEmpty { return [] }
        guard !stopped, let choice = choices.first,
              let message = choice[streaming ? "delta" : "message"] as? [String: Any] else { throw CleanupAnswerParseError.malformed }
        if let role = message["role"] as? String, role != "assistant" { throw CleanupAnswerParseError.unsupportedContent }
        if let refusal = message["refusal"], !(refusal is NSNull) {
            if let text = refusal as? String, text.isEmpty { /* Empty refusal is harmless. */ }
            else { throw CleanupAnswerParseError.refused }
        }
        if let tools = message["tool_calls"], !(tools is NSNull) {
            if let calls = tools as? [Any], calls.isEmpty { /* No tool call. */ }
            else { throw CleanupAnswerParseError.toolCall }
        }
        if let function = message["function_call"], !(function is NSNull) { throw CleanupAnswerParseError.toolCall }
        let delta = try answerText(message["content"], separateTextBlocks: !streaming)
        let events = try appendAnswer(delta)
        if let reason = choice["finish_reason"], !(reason is NSNull) {
            guard reason as? String == "stop" else { throw CleanupAnswerParseError.incomplete }
            stopped = true
        } else if !streaming { throw CleanupAnswerParseError.incomplete }
        return events
    }

    private func answerText(_ content: Any?, separateTextBlocks: Bool) throws -> String {
        guard let content, !(content is NSNull) else { return "" }
        if let text = content as? String { return text }
        guard let parts = content as? [[String: Any]] else { throw CleanupAnswerParseError.unsupportedContent }
        var blocks: [String] = []
        for part in parts {
            guard let type = part["type"] as? String else { throw CleanupAnswerParseError.unsupportedContent }
            switch type {
            case "text":
                guard let text = part["text"] as? String else { throw CleanupAnswerParseError.malformed }
                blocks.append(text)
            case "reasoning", "reasoning_text", "thinking":
                continue
            case "tool_call", "function_call": throw CleanupAnswerParseError.toolCall
            default: throw CleanupAnswerParseError.unsupportedContent
            }
        }
        // Complete JSON historically treated distinct content blocks as lines.
        // Streaming parts/deltas remain exact adjacent fragments.
        return blocks.joined(separator: separateTextBlocks ? "\n" : "")
    }

    private mutating func ollamaObject(_ value: [String: Any]) throws -> [CleanupAnswerEvent] {
        guard !stopped, value["error"] == nil,
              let done = value["done"] as? NSNumber,
              CFGetTypeID(done) == CFBooleanGetTypeID() else { throw CleanupAnswerParseError.malformed }
        if value["tool_calls"] != nil || value["message"] != nil { throw CleanupAnswerParseError.unsupportedContent }
        if let reason = value["done_reason"], !(reason is NSNull), reason as? String != "stop" { throw CleanupAnswerParseError.incomplete }
        guard value["response"] == nil || value["response"] is String else { throw CleanupAnswerParseError.malformed }
        let events = try appendAnswer(value["response"] as? String ?? "")
        if done.boolValue { stopped = true }
        return events
    }

    private mutating func appendAnswer(_ text: String) throws -> [CleanupAnswerEvent] {
        guard text.utf8.count <= limits.answerBytes - answerBytes else { throw CleanupAnswerParseError.answerTooLarge }
        guard !text.isEmpty else { return [] }
        answerBytes += text.utf8.count
        answer += text
        return [.answerDelta(text)]
    }
}
