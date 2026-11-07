import Foundation

nonisolated struct OllamaModelDetails: Decodable, Sendable {
    enum ThinkingValue: Decodable, Equatable, Sendable {
        case toggle(Bool)
        case level(String)
        init(from decoder: Decoder) throws {
            let container = try decoder.singleValueContainer()
            if let value = try? container.decode(Bool.self) { self = .toggle(value) }
            else { self = .level(try container.decode(String.self)) }
        }
        var jsonValue: Any {
            switch self { case .toggle(let value): return value; case .level(let value): return value }
        }
    }
    struct Thinking: Decodable, Sendable { let values: [ThinkingValue] }
    struct Details: Decodable, Sendable {
        let family: String?
        let families: [String]?
    }
    struct ModelInfo: Decodable, Sendable {
        let contextLength: Int?
        struct Key: CodingKey {
            let stringValue: String
            var intValue: Int? { nil }
            init?(stringValue: String) { self.stringValue = stringValue }
            init?(intValue: Int) { return nil }
        }
        init(from decoder: Decoder) throws {
            let values = try decoder.container(keyedBy: Key.self)
            contextLength = values.allKeys.filter { $0.stringValue.hasSuffix(".context_length") }
                .compactMap { try? values.decode(Int.self, forKey: $0) }.filter { $0 > 0 }.min()
        }
    }
    let thinking: Thinking?
    let capabilities: [String]?
    let modelInfo: ModelInfo?
    let details: Details?
    enum CodingKeys: String, CodingKey { case thinking, capabilities, details; case modelInfo = "model_info" }

    var isKnownNonThinking: Bool {
        if let values = thinking?.values { return values == [.toggle(false)] }
        guard let capabilities, capabilities.contains("completion") else { return false }
        return !capabilities.contains("thinking")
    }

    /// A bounded dictation working set; don't allocate a model's entire training
    /// context (potentially hundreds of thousands of tokens) to clean a note.
    var cleanupContextLength: Int { min(8_192, modelInfo?.contextLength ?? 8_192) }

    func reasoningCapability(for model: String) -> OllamaReasoningCapability {
        guard let values = thinking?.values else {
            // Older /api/show responses advertise thinking without its values.
            // Preserve their boolean control, except GPT-OSS's effort-only API.
            guard capabilities?.contains(where: { $0.lowercased() == "thinking" }) == true else { return .unsupported }
            let identifiers = [model, details?.family ?? ""] + (details?.families ?? [])
            if identifiers.contains(where: {
                let identifier = $0.lowercased()
                return identifier.contains("gpt-oss") || identifier.contains("gptoss")
            }) { return .level }
            return .toggle
        }
        let hasReasoning = values.contains(.toggle(true)) || values.contains {
            if case .level = $0 { return true }
            return false
        }
        if hasReasoning && values.contains(.toggle(false)) { return .toggle }
        if values.contains(.toggle(true)) { return .required }
        if ["low", "medium", "high"].contains(where: { values.contains(.level($0)) }) { return .level }
        if values.contains(where: { if case .level = $0 { return true }; return false }) { return .required }
        return .unsupported
    }

    func reasoningOffOverride(enabled: Bool, model: String) -> ThinkingValue? {
        guard !enabled else { return nil }
        if let values = thinking?.values {
            return values.contains(.toggle(false)) ? .toggle(false) : nil
        }
        return reasoningCapability(for: model) == .toggle ? .toggle(false) : nil
    }
}

/// Adapt only the parameter the server explicitly identifies as unsupported.
/// Never retry authentication, quota, context, refusal or generic 400 errors.
nonisolated enum CleanupRequestAdaptation {
    enum Parameter: Hashable { case structuredOutput, temperature }
    static func unsupportedParameter(in message: String) -> Parameter? {
        let text = message.lowercased()
        guard ["unsupported", "not supported", "not support", "not allowed", "only supports", "unknown parameter", "unrecognized"].contains(where: text.contains) else { return nil }
        if text.contains("temperature") { return .temperature }
        if text.contains("response_format") || text.contains("json_schema") || text.contains("structured output") {
            return .structuredOutput
        }
        // Ollama names its schema parameter `format`. A generic file, model
        // or audio format error does not reject that request parameter.
        let formatParameterErrors = [
            "format is not supported",
            "format is not supported for cloud models",
            "unsupported parameter: format",
            "unknown parameter: format",
            "unrecognized parameter: format"
        ]
        if formatParameterErrors.contains(text.trimmingCharacters(in: .whitespacesAndNewlines)) {
            return .structuredOutput
        }
        return nil
    }
}

nonisolated struct CleanupChatOptions: Sendable {
    var structuredOutput = true

    mutating func adapt(to message: String) -> Bool {
        switch CleanupRequestAdaptation.unsupportedParameter(in: message) {
        case .structuredOutput where structuredOutput: structuredOutput = false; return true
        default: return false
        }
    }
}

/// Remember explicit protocol rejections, never transcript text or credentials.
/// A short lifetime lets a server upgrade regain structured output automatically.
actor CleanupSchemaSupport<Key: Hashable & Sendable> {
    private let lifetime: Duration
    private let capacity: Int
    private let now: @Sendable () -> ContinuousClock.Instant
    private var rejected: [Key: ContinuousClock.Instant] = [:]

    init(lifetime: Duration = .seconds(300), capacity: Int = 8,
         now: @escaping @Sendable () -> ContinuousClock.Instant = { .now }) {
        self.lifetime = lifetime
        self.capacity = max(1, capacity)
        self.now = now
    }

    func usesStructuredOutput(for key: Key) -> Bool {
        guard let time = rejected[key] else { return true }
        if time.duration(to: now()) < lifetime { return false }
        rejected[key] = nil
        return true
    }

    func recordUnsupportedSchema(for key: Key) {
        rejected[key] = now()
        if rejected.count > capacity, let oldest = rejected.min(by: { $0.value < $1.value })?.key {
            rejected[oldest] = nil
        }
    }

    func invalidate(_ key: Key) { rejected[key] = nil }
}

/// Runtime chunking never enables the legacy adapters' automatic retries.
/// Every piece belongs to one all-or-nothing foreground cleanup attempt.
nonisolated enum RemoteCleanupInputPolicy: Equatable, Sendable {
    case legacyChunking
    case singleRequestOnly(conservativeContextBudget: Int)
    case boundedChunking(conservativeContextBudget: Int)
}
nonisolated enum RemoteCleanupInputPolicyError: Error, Equatable {
    case singleRequestBudgetExceeded
}
nonisolated struct RemoteCleanupInputAssessment: Equatable, Sendable {
    enum Validation: Equatable, Sendable { case conservativeByteEstimate }
    let validation: Validation
    let sourceBytes: Int
    let estimatedRequestAndOutputBudget: Int
    let contextBudget: Int
}

enum RemoteCleanupProcessing {
    @TaskLocal static var inputPolicy: RemoteCleanupInputPolicy = .legacyChunking

    /// This is a conservative UTF-8 byte estimate, not tokenizer verification
    /// or evidence of a server's actual context capacity. Unknown capacities use
    /// an explicit 8192 budget ceiling; reported smaller capacities tighten it.
    static func assessSingleRequest(
        text: String, instructions: String, vocabulary: [String], context: DictationPostProcessingContext?,
        conservativeContextBudget: Int = 8_192, contextLength: Int? = nil,
        maximumCompletionTokens: Int? = nil
    ) throws -> RemoteCleanupInputAssessment {
        let budget = min(8_192, conservativeContextBudget, contextLength ?? conservativeContextBudget)
        let sourceBytes = text.utf8.count
        guard budget > 0, sourceBytes <= budget, instructions.utf8.count <= budget else {
            throw RemoteCleanupInputPolicyError.singleRequestBudgetExceeded
        }
        let output = TranscriptCleanupPlan.outputReserve(inputTokens: sourceBytes)
        let request = remotePostProcessingRequestPrompt(text: text, vocabulary: vocabulary, context: context)
        let total = instructions.utf8.count + 512 + request.utf8.count + output
        guard total <= budget, maximumCompletionTokens.map({ output <= $0 }) ?? true else {
            throw RemoteCleanupInputPolicyError.singleRequestBudgetExceeded
        }
        return .init(validation: .conservativeByteEstimate, sourceBytes: sourceBytes,
                     estimatedRequestAndOutputBudget: total, contextBudget: budget)
    }

    /// Validate the whole lossless partition before contacting a provider. The
    /// total dictation may exceed the working set; each request still must fit.
    static func boundedChunks(
        text: String, instructions: String, vocabulary: [String], context: DictationPostProcessingContext?,
        conservativeContextBudget: Int = 8_192, contextLength: Int? = nil,
        maximumCompletionTokens: Int? = nil
    ) async throws -> [TranscriptCleanupChunk] {
        try Task.checkCancellation()
        // Splitting cannot repair oversized fixed instructions/context.
        _ = try assessSingleRequest(text: "", instructions: instructions, vocabulary: vocabulary,
            context: context, conservativeContextBudget: conservativeContextBudget,
            contextLength: contextLength, maximumCompletionTokens: maximumCompletionTokens)
        return try await TranscriptCleanupPlan.chunks(text) { candidate in
            do {
                _ = try assessSingleRequest(text: candidate, instructions: instructions, vocabulary: vocabulary,
                    context: context, conservativeContextBudget: conservativeContextBudget,
                    contextLength: contextLength, maximumCompletionTokens: maximumCompletionTokens)
                return true
            } catch RemoteCleanupInputPolicyError.singleRequestBudgetExceeded {
                return false
            }
        }
    }

    static func process(
        text: String, instructions: String, vocabulary: [String], context: DictationPostProcessingContext?,
        contextLength: Int = 8_192, maximumCompletionTokens: Int? = nil,
        maximumConcurrentRequests: Int = 1,
        generate: @escaping @Sendable (String) async throws -> String
    ) async throws -> String {
        if case .singleRequestOnly(let budget) = inputPolicy {
            _ = try assessSingleRequest(text: text, instructions: instructions, vocabulary: vocabulary,
                context: context, conservativeContextBudget: budget, contextLength: contextLength,
                maximumCompletionTokens: maximumCompletionTokens)
            try Task.checkCancellation()
            // Never split/reassemble a runtime-selected transcript. A rejected
            // request leaves corrected raw intact for the caller's fallback.
            return try await processChunk(TranscriptCleanupChunk(text), generate: generate)
        }
        let chunks: [TranscriptCleanupChunk]
        if case .boundedChunking(let budget) = inputPolicy {
            chunks = try await boundedChunks(text: text, instructions: instructions, vocabulary: vocabulary,
                context: context, conservativeContextBudget: budget, contextLength: contextLength,
                maximumCompletionTokens: maximumCompletionTokens)
        } else {
            let fixedTokens = TranscriptCleanupPlan.estimatedTokens(instructions) + 512
            chunks = try await TranscriptCleanupPlan.chunks(text) { candidate in
                let output = TranscriptCleanupPlan.outputReserve(inputTokens: TranscriptCleanupPlan.estimatedTokens(candidate))
                let request = remotePostProcessingRequestPrompt(text: candidate, vocabulary: vocabulary, context: context)
                return fixedTokens + TranscriptCleanupPlan.estimatedTokens(request) + output <= contextLength
                    && (maximumCompletionTokens.map { output <= $0 } ?? true)
            }
        }
        // Local model servers keep serial generation. Independent cloud
        // passages can overlap without changing partitions or their order.
        let limit = min(2, max(1, maximumConcurrentRequests))
        if limit == 1 || chunks.count <= 1 {
            var outputs: [String] = []
            for chunk in chunks {
                outputs.append(try await processChunk(chunk, generate: generate))
            }
            return outputs.joined()
        }
        return try await withThrowingTaskGroup(of: (Int, String).self) { group in
            var nextIndex = 0
            var outputs = Array(repeating: "", count: chunks.count)
            func enqueueNext() {
                let index = nextIndex
                let chunk = chunks[index]
                nextIndex += 1
                group.addTask {
                    (index, try await processChunk(chunk, generate: generate))
                }
            }
            for _ in 0..<min(limit, chunks.count) { enqueueNext() }
            do {
                while let (index, output) = try await group.next() {
                    try Task.checkCancellation()
                    outputs[index] = output
                    if nextIndex < chunks.count { enqueueNext() }
                }
            } catch {
                group.cancelAll()
                throw error // No partial or reordered cleanup can escape.
            }
            return outputs.joined()
        }
    }

    private static func processChunk(_ chunk: TranscriptCleanupChunk,
                                     generate: @Sendable (String) async throws -> String) async throws -> String {
        try Task.checkCancellation()
        guard !chunk.text.isEmpty else { return chunk.original }
        let raw = try await generate(chunk.text)
        try Task.checkCancellation()
        return chunk.replacingText(with: cleanedRemotePostProcessingResponse(from: raw, originalText: chunk.text))
    }
}
