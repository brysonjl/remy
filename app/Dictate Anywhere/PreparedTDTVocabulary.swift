import FluidAudio
@preconcurrency import CoreML

/// Prepared tokenizer/scorer and resident weights, scoped to one model and term list.
nonisolated enum PreparedTDTVocabulary: Sendable {
    case shared(SharedCTCVocabularySession)

    static func prepare(models: AsrModels, terms: [String],
                        cachedHead: MLModel? = nil, progressHandler: ProgressHandler? = nil) async throws -> Self {
        // FluidAudio 0.17.5's VocabularyBoostingSession always reads the SDK's
        // shared CTC cache, including when explicit CtcModels are supplied.
        // Fail before creating a session or downloading a second model.
        guard models.version == .tdtCtc110m else {
            throw ASRError.processingFailed(
                "Acoustic vocabulary for this model is unavailable in Remy with FluidAudio 0.17.5. Use Compact English for acoustic vocabulary, or choose another cleanup mode and keep this speech model.")
        }
        let session = try await SharedCTCVocabularySession.prepare(models: models, terms: terms,
            config: VocabularyRescoringPolicy.config, cachedHead: cachedHead, progressHandler: progressHandler)
        try await session.warm()
        return .shared(session)
    }

    func rescore(_ original: ASRResult, samples: [Float]) async throws -> String {
        switch self {
        case .shared(let session): return try await session.rescore(original, samples: samples)
        }
    }
}
