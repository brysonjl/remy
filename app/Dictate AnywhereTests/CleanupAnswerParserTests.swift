import Foundation
import XCTest
@testable import Dictate_Anywhere

final class CleanupAnswerParserTests: XCTestCase {
    private func json(_ object: [String: Any]) throws -> Data { try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]) }
    private func event(_ delta: [String: Any], finish: Any = NSNull()) throws -> Data {
        let object = try json(["choices": [["delta": delta, "finish_reason": finish]]])
        return Data("data: ".utf8) + object + Data("\n\n".utf8)
    }
    private func completed(_ content: Any, reason: Any = "stop") throws -> Data {
        try json(["choices": [["message": ["role": "assistant", "content": content], "finish_reason": reason]]])
    }

    func testSSEPreservesSplitUTF8AndRequiresStopPlusDoneAcrossEverySplit() throws {
        let answer = "Café 👩🏽‍💻 中文"
        let wire = try event(["role": "assistant", "content": answer]) + event([:], finish: "stop") + Data("data: [DONE]\n\n".utf8)
        for cut in 0...wire.count {
            var parser = try CleanupAnswerParser(format: .compatibleSSE)
            var events = try parser.append(Data(wire.prefix(cut)))
            events += try parser.append(Data(wire.dropFirst(cut)))
            XCTAssertEqual(events, [.answerDelta(answer)])
            XCTAssertEqual(try parser.finish(), .completed(answer))
        }
        var bytewise = try CleanupAnswerParser(format: .compatibleSSE)
        for byte in wire { _ = try bytewise.append(Data([byte])) }
        XCTAssertEqual(try bytewise.finish(), .completed(answer))
    }

    func testSSESupportsCRLFAndCRDelimitersIncludingChunkBoundaries() throws {
        let wire = try event(["content": "Answer"], finish: "stop") + Data("data: [DONE]\n\n".utf8)
        for newline in ["\r\n", "\r"] {
            let text = String(decoding: wire, as: UTF8.self).replacingOccurrences(of: "\n", with: newline)
            var parser = try CleanupAnswerParser(format: .compatibleSSE)
            for byte in Data(text.utf8) { _ = try parser.append(Data([byte])) }
            XCTAssertEqual(try parser.finish(), .completed("Answer"))
        }
    }

    func testSSEMultiDataLinesCommentsAndUsageDoNotBecomeText() throws {
        var parser = try CleanupAnswerParser(format: .compatibleSSE)
        let wire = ": keepalive\nevent: message\ndata: {\ndata: \"choices\":[{\"delta\":{\"content\":\"Answer\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: {\"choices\":[],\"usage\":{\"total_tokens\":10}}\n\ndata: [DONE]\n\n"
        XCTAssertEqual(try parser.append(Data(wire.utf8)), [.answerDelta("Answer")])
        XCTAssertEqual(try parser.finish(), .completed("Answer"))
    }

    func testSSETruncatedEOFAndMissingFinishAreNotSuccess() throws {
        let cases: [Data] = [
            try event(["content": "Answer"]),
            try event(["content": "Answer"], finish: "stop"),
            try event(["content": "Answer"], finish: "stop") + Data("data: [DONE]".utf8)
        ]
        for wire in cases {
            var parser = try CleanupAnswerParser(format: .compatibleSSE)
            _ = try parser.append(wire)
            XCTAssertThrowsError(try parser.finish())
            XCTAssertThrowsError(try parser.append(Data()))
        }
        var noStop = try CleanupAnswerParser(format: .compatibleSSE)
        XCTAssertThrowsError(try noStop.append(try event(["content": "Answer"]) + Data("data: [DONE]\n\n".utf8)))
    }

    func testNDJSONBytewiseOnlyAnswerNotThinkingAndExplicitDone() throws {
        let first = try json(["response": "Café ", "thinking": "private reasoning", "done": false])
        let last = try json(["response": "中文", "done": true, "done_reason": "stop"])
        let wire = first + Data("\n".utf8) + last // Final NDJSON line need not end in LF.
        var parser = try CleanupAnswerParser(format: .ollamaNDJSON)
        for byte in wire { _ = try parser.append(Data([byte])) }
        XCTAssertEqual(try parser.finish(), .completed("Café 中文"))
        for data in [try json(["response": "Answer", "done": false]), try json(["response": "Answer"]) ] {
            var incomplete = try CleanupAnswerParser(format: .ollamaNDJSON)
            _ = try incomplete.append(data)
            XCTAssertThrowsError(try incomplete.finish())
        }
    }

    func testCompleteJSONRequiresExplicitCompletionOnBothAdapters() throws {
        var compatible = try CleanupAnswerParser(format: .compatibleJSON)
        XCTAssertEqual(try compatible.append(try completed("Answer")), [])
        XCTAssertEqual(try compatible.finish(), .completed("Answer"))
        var missing = try CleanupAnswerParser(format: .compatibleJSON)
        _ = try missing.append(try completed("Answer", reason: NSNull()))
        XCTAssertThrowsError(try missing.finish())
        var ollama = try CleanupAnswerParser(format: .ollamaJSON)
        _ = try ollama.append(try json(["response": "Answer", "done": true]))
        XCTAssertEqual(try ollama.finish(), .completed("Answer"))
        var numericDone = try CleanupAnswerParser(format: .ollamaJSON)
        _ = try numericDone.append(try json(["response": "Answer", "done": 1]))
        XCTAssertThrowsError(try numericDone.finish(), "Numeric one is not a Boolean completion flag")
    }

    func testTypedPartsExcludeReasoningAndUnknownPartsCannotLeak() throws {
        var parser = try CleanupAnswerParser(format: .compatibleJSON)
        _ = try parser.append(try completed([["type": "reasoning", "text": "private reasoning"], ["type": "text", "text": "Answer"]]))
        XCTAssertEqual(try parser.finish(), .completed("Answer"))
        for content in [[["text": "unknown text"]], [["type": "image", "text": "not answer"]]] {
            var unknown = try CleanupAnswerParser(format: .compatibleJSON)
            _ = try unknown.append(try completed(content))
            XCTAssertThrowsError(try unknown.finish())
        }
    }

    func testCompleteMultipartBlocksPreserveNewlinesWhileStreamDeltasConcatenate() throws {
        let parts = [["type": "text", "text": "First block"],
                     ["type": "reasoning", "text": "not an answer"],
                     ["type": "text", "text": "Second block"]]
        var complete = try CleanupAnswerParser(format: .compatibleJSON)
        _ = try complete.append(try completed(parts))
        XCTAssertEqual(try complete.finish(), .completed("First block\nSecond block"))
        var stream = try CleanupAnswerParser(format: .compatibleSSE)
        var events = try stream.append(try event(["content": [["type": "text", "text": "Hel"], ["type": "text", "text": "lo"]]]))
        events += try stream.append(try event(["content": " world"], finish: "stop") + Data("data: [DONE]\n\n".utf8))
        XCTAssertEqual(events, [.answerDelta("Hello"), .answerDelta(" world")])
        XCTAssertEqual(try stream.finish(), .completed("Hello world"))
    }

    func testRefusalToolsLengthAndMalformedResponsesFailWithoutFinalOutput() throws {
        let messages: [[String: Any]] = [
            ["content": "Answer", "refusal": "cannot comply"],
            ["content": "Answer", "tool_calls": [["function": ["name": "run"]]]],
            ["content": "Answer", "function_call": ["name": "run"]]
        ]
        for message in messages {
            var parser = try CleanupAnswerParser(format: .compatibleJSON)
            _ = try parser.append(try json(["choices": [["message": message, "finish_reason": "stop"]]]))
            XCTAssertThrowsError(try parser.finish())
        }
        var length = try CleanupAnswerParser(format: .compatibleJSON)
        _ = try length.append(try completed("Truncated", reason: "length"))
        XCTAssertThrowsError(try length.finish())
        var reasoningEvent = try CleanupAnswerParser(format: .compatibleSSE)
        XCTAssertThrowsError(try reasoningEvent.append(Data("event: reasoning\n".utf8) + event(["content": "not an answer"])))
        var malformed = try CleanupAnswerParser(format: .compatibleSSE)
        XCTAssertThrowsError(try malformed.append(Data("data: not-json\n\n".utf8)))
        XCTAssertThrowsError(try malformed.append(Data()))
    }

    func testResponseRecordAndAnswerBoundsAndInvalidUTF8() throws {
        var total = try CleanupAnswerParser(format: .compatibleJSON, limits: .init(responseBytes: 8, recordBytes: 8, answerBytes: 8))
        XCTAssertThrowsError(try total.append(Data(repeating: 32, count: 9)))
        var line = try CleanupAnswerParser(format: .compatibleSSE, limits: .init(responseBytes: 128, recordBytes: 8, answerBytes: 8))
        XCTAssertThrowsError(try line.append(Data("data: too long\n\n".utf8)))
        var answer = try CleanupAnswerParser(format: .compatibleJSON, limits: .init(answerBytes: 3))
        _ = try answer.append(try completed("中文"))
        XCTAssertThrowsError(try answer.finish())
        var invalid = try CleanupAnswerParser(format: .compatibleSSE)
        XCTAssertThrowsError(try invalid.append(Data("data: ".utf8) + Data([0xff, 10, 10])))
        XCTAssertThrowsError(try CleanupAnswerParser(format: .compatibleJSON, limits: .init(responseBytes: 0)))
    }

    func testCancellationDiscardsPartialAnswerAndRejectsLateCompletion() throws {
        var parser = try CleanupAnswerParser(format: .compatibleSSE)
        _ = try parser.append(try event(["content": "Provisional answer"]))
        parser.cancel()
        XCTAssertThrowsError(try parser.append(try event([:], finish: "stop"))) {
            XCTAssertEqual($0 as? CleanupAnswerParseError, .cancelled)
        }
        XCTAssertThrowsError(try parser.finish()) {
            XCTAssertEqual($0 as? CleanupAnswerParseError, .cancelled)
        }
    }

    func testNothingAfterCompletionCanAlterFinalAnswer() throws {
        var parser = try CleanupAnswerParser(format: .compatibleSSE)
        _ = try parser.append(try event(["content": "Answer"], finish: "stop") + Data("data: [DONE]\n\n".utf8))
        XCTAssertEqual(try parser.finish(), .completed("Answer"))
        XCTAssertThrowsError(try parser.append(try event(["content": "late"])))
        XCTAssertThrowsError(try parser.finish())
    }
}
