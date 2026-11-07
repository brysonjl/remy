/// Supported Remy routes. Runtime and settings normalization must enforce the
/// same policy as UI selection; hiding a control alone is not a capability gate.
nonisolated enum RemyFeaturePolicy {
    static let supportedEngineChoices: [TranscriptionEngineChoice] = [.parakeet, .appleSpeech]
    static let allowsCloudSpeechRecognition = false
    static let allowsDictationContextGathering = true
    static let allowsRemoteDictationContextSharing = true

    /// Preserve supported local selections and recover old cloud selections to
    /// the default local engine. Platform availability is resolved separately.
    static func effectiveEngineChoice(_ choice: TranscriptionEngineChoice) -> TranscriptionEngineChoice {
        switch choice {
        case .parakeet, .appleSpeech: choice
        case .assemblyAI: .parakeet
        }
    }
}
