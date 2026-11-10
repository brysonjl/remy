import Foundation
import FoundationModels

/// Ephemeral immutable values for one dictation. Never encode or log this
/// configuration: prompts, vocabulary and endpoint configuration are private.
/// Credential values live only in the separately owned runtime operation.
nonisolated struct RemyDictationConfiguration {
    let settingsVersion: Int
    let instructionVersion: Int
    let credentialReference: UUID
    let capture: Capture
    let cleanup: Cleanup

    init(settingsVersion: Int = 1, instructionVersion: Int = 1,
         credentialReference: UUID = UUID(), capture: Capture, cleanup: Cleanup) {
        self.settingsVersion = settingsVersion
        self.instructionVersion = instructionVersion
        self.credentialReference = credentialReference
        self.capture = capture
        self.cleanup = cleanup
    }

    struct Capture {
        let engine: TranscriptionEngineChoice
        let model: ParakeetModelChoice
        let language: SupportedLanguage
        let microphoneUID: String?
        let vocabulary: [String]
        let fillerRules: RemyFillerRules
        let textRules: DictationTextRules
        let muteSystemAudio: Bool
        let boostMicrophoneVolume: Bool
        let preserveCancelledSessions: Bool
        let autoStopAfterSpeechEnds: Bool
        let historyEnabled: Bool
        let destination: RemyDictationDestination
        /// Read names from the focused window for this session. Effective
        /// only with context awareness and a consumer (see ScreenNamePolicy).
        let spellNamesFromActiveWindow: Bool

        init(engine: TranscriptionEngineChoice = .parakeet, model: ParakeetModelChoice = .englishOnly,
             language: SupportedLanguage = .english, microphoneUID: String? = nil,
             vocabulary: [String] = [], fillerRules: RemyFillerRules = .init(isEnabled: false, words: []),
             textRules: DictationTextRules = .init(), muteSystemAudio: Bool = false, boostMicrophoneVolume: Bool = false,
             preserveCancelledSessions: Bool = false, autoStopAfterSpeechEnds: Bool = false,
             historyEnabled: Bool = true, destination: RemyDictationDestination = .originalField,
             spellNamesFromActiveWindow: Bool = false) {
            self.engine = engine
            self.model = model
            self.language = language
            self.microphoneUID = microphoneUID
            self.vocabulary = vocabulary
            self.fillerRules = fillerRules
            self.textRules = textRules
            self.muteSystemAudio = muteSystemAudio
            self.boostMicrophoneVolume = boostMicrophoneVolume
            self.preserveCancelledSessions = preserveCancelledSessions
            self.autoStopAfterSpeechEnds = autoStopAfterSpeechEnds
            self.historyEnabled = historyEnabled
            self.destination = destination
            self.spellNamesFromActiveWindow = spellNamesFromActiveWindow
        }
    }

    struct Cleanup {
        let mode: TranscriptPostProcessingMode
        let prompt: String
        let baseURL: String
        let model: String
        let reasoningEnabled: Bool
        let s1Styling: S1MiniStyling
        let s1Structure: S1MiniStructure
        let s1ContextSetting: S1MiniContextSetting
        /// Expected URL captured synchronously. The runtime must validate this
        /// same asset rather than following a later model-manager selection.
        let s1ModelURL: URL?
        let contextPolicy: RemyDictationContextPolicy
        let profile: CleanupProfile?

        init(mode: TranscriptPostProcessingMode = .none, prompt: String = "", baseURL: String = "",
             model: String = "", reasoningEnabled: Bool = true,
             s1Styling: S1MiniStyling = .semiFormal, s1Structure: S1MiniStructure = .prose,
             s1ContextSetting: S1MiniContextSetting = .general, s1ModelURL: URL? = nil, profile: CleanupProfile? = nil,
             contextPolicy: RemyDictationContextPolicy = .init()) {
            self.profile = profile
            self.mode = mode
            self.prompt = prompt
            self.baseURL = baseURL
            self.model = model
            self.reasoningEnabled = reasoningEnabled
            self.s1Styling = s1Styling
            self.s1Structure = s1Structure
            self.s1ContextSetting = s1ContextSetting
            self.s1ModelURL = s1ModelURL
            self.contextPolicy = contextPolicy
        }

        /// How captured text may reach this session's cleanup route. A
        /// loopback Ollama/compatible/profile endpoint counts as local.
        var contextSupport: DictationContextSupport {
            mode.dictationContextSupport(isConfiguredServerLocal: RemyDictationContextPolicy.isLocalEndpoint(
                baseURL: profile?.baseURL ?? baseURL, model: profile?.model ?? model))
        }
    }
}

/// Frozen at recording start, before Accessibility capture can complete. A
/// settings change during dictation cannot broaden this session's disclosure.
nonisolated struct RemyDictationContextPolicy {
    let isEnabled: Bool
    let shareCapturedTextRemotely: Bool
    /// Resolved name and instruction text, so later style edits cannot
    /// change an in-flight dictation.
    let writingStyles: [DictationContextCategory: DictationResolvedWritingStyle]
    let s1Styles: [DictationContextCategory: S1MiniStyling]
    let appRules: [DictationAppRule]

    init(isEnabled: Bool = false, shareCapturedTextRemotely: Bool = false,
         writingStyles: [DictationContextCategory: DictationResolvedWritingStyle] = [:],
         s1Styles: [DictationContextCategory: S1MiniStyling] = [:],
         appRules: [DictationAppRule] = []) {
        self.isEnabled = isEnabled
        self.shareCapturedTextRemotely = shareCapturedTextRemotely
        self.writingStyles = writingStyles
        self.s1Styles = s1Styles
        self.appRules = appRules
    }

    func writingStyle(for category: DictationContextCategory) -> DictationResolvedWritingStyle {
        (writingStyles[category] ?? .builtIn(.original)).sanitized(for: category)
    }

    func s1Styling(for category: DictationContextCategory?, fallback: S1MiniStyling) -> S1MiniStyling {
        guard isEnabled, let category else { return fallback }
        return s1Styles[category] ?? fallback
    }

    /// Only an explicitly loopback endpoint qualifies for automatic full
    /// context. Wildcard/LAN addresses and declared cloud models need opt-in.
    static func isLocalEndpoint(baseURL: String, model: String) -> Bool {
        let tag = model.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !tag.hasSuffix("-cloud"), !tag.hasSuffix(":cloud"),
              let endpoint = try? CleanupEndpointIdentity(baseURL: baseURL),
              let host = URLComponents(string: endpoint.canonicalURL)?.host?.lowercased() else { return false }
        return ["localhost", "127.0.0.1", "::1", "[::1]"].contains(host)
    }
}

/// Same boundary-aware filler algorithm as the inherited settings route, with
/// explicit immutable rules rather than reads from Settings during finalization.
nonisolated struct RemyFillerRules {
    let isEnabled: Bool
    let words: [String]
    let bareMatchCJKFillers: Set<String>

    init(isEnabled: Bool, words: [String], bareMatchCJKFillers: Set<String> = ["嗯", "呃"]) {
        self.isEnabled = isEnabled
        self.words = words
        self.bareMatchCJKFillers = bareMatchCJKFillers
    }

    func removingFillers(from text: String) -> String {
        guard isEnabled, !words.isEmpty else { return text }
        let alternatives = words.compactMap { word -> String? in
            let trimmed = word.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty else { return nil }
            let escaped = NSRegularExpression.escapedPattern(for: trimmed)
            if trimmed.unicodeScalars.contains(where: { CJKText.isCJK($0) }) {
                let repeated = "(?:\(escaped))+"
                guard bareMatchCJKFillers.contains(trimmed) else {
                    return "(?<![^\\s\\p{P}])\(repeated)(?![^\\s\\p{P}])"
                }
                return repeated
            }
            return "\\b\(escaped)\\b"
        }
        guard !alternatives.isEmpty,
              let regex = try? NSRegularExpression(pattern: "(?:" + alternatives.joined(separator: "|") + ")",
                                                   options: .caseInsensitive) else { return text }
        var result = regex.stringByReplacingMatches(in: text, options: [], range: NSRange(text.startIndex..., in: text), withTemplate: "")
        while result.contains("  ") { result = result.replacingOccurrences(of: "  ", with: " ") }
        for mark in [",", ".", "!", "?", "，", "。", "！", "？"] {
            result = result.replacingOccurrences(of: " " + mark, with: mark)
        }
        while result.contains("，，") { result = result.replacingOccurrences(of: "，，", with: "，") }
        return result.trimmingCharacters(in: .whitespaces)
    }
}

/// The closure privately owns a selected credential value for this session.
/// No registry, persistence, Codable conformance, or diagnostic interpolation.
@MainActor
final class RemyCleanupRuntime {
    enum RuntimeError: Error, Equatable { case missingCapturedCredential, missingValidatedS1Model, s1ModelChanged, invalidProfile, unsupportedProfileTransport, unverifiedProfileRoute, profileCredentialUnavailable, profileRequestFailed }
    let credentialReference: UUID
    let remoteInputPolicy: RemoteCleanupInputPolicy
    private let remotePrompt: String?
    private let cleanup: RemyDictationConfiguration.Cleanup
    private let remoteVocabulary: [String]
    private let operation: @MainActor (String, DictationPostProcessingContext?) async throws -> String

    convenience init(configuration: RemyDictationConfiguration,
                     operation: @escaping @MainActor (String) async throws -> String) {
        self.init(configuration: configuration, contextualOperation: { text, _ in try await operation(text) })
    }

    init(configuration: RemyDictationConfiguration,
         contextualOperation: @escaping @MainActor (String, DictationPostProcessingContext?) async throws -> String) {
        cleanup = configuration.cleanup
        credentialReference = configuration.credentialReference
        switch configuration.cleanup.mode {
        case .ollama, .openRouter, .openAICompatible, .profile:
            remoteInputPolicy = .boundedChunking(conservativeContextBudget: 8_192)
            remoteVocabulary = configuration.capture.vocabulary
            remotePrompt = configuration.cleanup.profile?.prompt ?? configuration.cleanup.prompt
        default:
            remoteInputPolicy = .legacyChunking
            remoteVocabulary = []
            remotePrompt = nil
        }
        self.operation = contextualOperation
    }

    static func isLocalContextEndpoint(baseURL: String, model: String) -> Bool {
        RemyDictationContextPolicy.isLocalEndpoint(baseURL: baseURL, model: model)
    }

    /// Screen names follow the captured-text rule: local routes receive them;
    /// a remote route only with this session's surrounding-text opt-in.
    func postProcessingContext(from context: DictationContext?,
                               screenNames: [String] = []) -> DictationPostProcessingContext? {
        guard cleanup.contextPolicy.isEnabled, cleanup.mode.usesDictationContext,
              let context else { return nil }
        let support = cleanup.contextSupport
        guard support != .none else { return nil }
        return context.postProcessingContext(style: cleanup.contextPolicy.writingStyle(for: context.category),
            includeCapturedText: support.includesCapturedText(
                remoteSharingEnabled: cleanup.contextPolicy.shareCapturedTextRemotely),
            screenNames: screenNames)
    }

    func process(text: String, context capturedContext: DictationContext? = nil,
                 screenNames: [String] = []) async throws -> String {
        try Task.checkCancellation()
        let context = postProcessingContext(from: capturedContext, screenNames: screenNames)
        if case .boundedChunking(let budget) = remoteInputPolicy, let remotePrompt {
            let remoteInstructions = remotePostProcessingInstructions(prompt: remotePrompt,
                vocabulary: remoteVocabulary, context: context)
            // Validate a complete lossless plan before provider operations. The
            // adapter replans against any smaller reported context/output limit.
            _ = try await RemoteCleanupProcessing.boundedChunks(text: text, instructions: remoteInstructions,
                vocabulary: remoteVocabulary, context: context, conservativeContextBudget: budget)
        }
        let result = try await RemoteCleanupProcessing.$inputPolicy.withValue(remoteInputPolicy) {
            try await operation(text, context)
        }
        try Task.checkCancellation()
        guard !result.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw RemyCleanupFailure.invalidResponse
        }
        return result
    }

    /// Capture once after resolving the session's selected speech configuration.
    /// No await occurs here. Validation is bound to the captured expected URL
    /// and checked before S1 inference, without rereading Settings.
    static func capture(settings: Settings, destination: RemyDictationDestination? = nil, s1ModelURL: URL? = nil,
                        validateS1Model: (@MainActor () async throws -> URL)? = nil,
                        profile: CleanupProfile? = nil,
                        profileCredentials: (any CleanupProfileCredentialStore)? = nil)
        -> (configuration: RemyDictationConfiguration, runtime: RemyCleanupRuntime) {
        let engine = RemyFeaturePolicy.effectiveEngineChoice(settings.engineChoice)
        let language = engine == .appleSpeech ? settings.appleSpeechLanguage : settings.selectedLanguage
        let mode = settings.transcriptPostProcessingMode
        let prompt: String
        let baseURL: String
        let model: String
        let reasoning: Bool
        switch mode {
        case .profile:
            prompt = profile?.prompt ?? ""; baseURL = profile?.baseURL ?? ""
            model = profile?.model ?? ""; reasoning = false
        case .appleIntelligence:
            prompt = settings.aiPostProcessingPrompt; baseURL = ""; model = ""; reasoning = false
        case .ollama:
            prompt = settings.ollamaPostProcessingPrompt; baseURL = settings.ollamaBaseURL
            model = settings.ollamaModel; reasoning = settings.ollamaReasoningEnabled
        case .openRouter:
            prompt = settings.openRouterPostProcessingPrompt; baseURL = "https://openrouter.ai/api/v1"
            model = settings.openRouterModel; reasoning = settings.openRouterReasoningEnabled
        case .openAICompatible:
            prompt = settings.openAICompatiblePostProcessingPrompt; baseURL = settings.openAICompatibleBaseURL
            model = settings.openAICompatibleModel; reasoning = false
        default:
            prompt = ""; baseURL = ""; model = ""; reasoning = false
        }
        let configuration = RemyDictationConfiguration(
            capture: .init(engine: engine, model: settings.parakeetModelChoice, language: language,
                microphoneUID: settings.selectedMicrophoneUID, vocabulary: settings.customVocabulary,
                fillerRules: .init(isEnabled: settings.isFillerWordRemovalEnabled, words: settings.fillerWordsToRemove,
                                   bareMatchCJKFillers: Settings.bareMatchCJKFillers),
                textRules: .init(corrections: settings.vocabularyCorrections, snippets: settings.dictationSnippets),
                muteSystemAudio: settings.muteSystemAudioDuringRecordingEnabled,
                boostMicrophoneVolume: settings.boostMicrophoneVolumeEnabled,
                preserveCancelledSessions: settings.preserveCancelledSessions,
                autoStopAfterSpeechEnds: settings.autoStopAfterSpeechEndsEnabled,
                historyEnabled: settings.historyEnabled, destination: destination ?? settings.dictationDestination,
                spellNamesFromActiveWindow: settings.spellNamesFromActiveWindowEnabled),
            cleanup: .init(mode: mode, prompt: prompt, baseURL: baseURL, model: model, reasoningEnabled: reasoning,
                s1Styling: settings.s1MiniStyling, s1Structure: settings.s1MiniStructure,
                s1ContextSetting: settings.s1MiniContextSetting, s1ModelURL: s1ModelURL,
                profile: mode == .profile ? profile : nil,
                contextPolicy: .init(
                    isEnabled: RemyFeaturePolicy.allowsDictationContextGathering && settings.dictationContextAwarenessEnabled,
                    shareCapturedTextRemotely: RemyFeaturePolicy.allowsRemoteDictationContextSharing
                        && settings.shareDictationContextWithRemoteProviders,
                    writingStyles: Dictionary(uniqueKeysWithValues: DictationContextCategory.allCases.map {
                        ($0, settings.resolvedDictationWritingStyle(for: $0))
                    }),
                    s1Styles: Dictionary(uniqueKeysWithValues: DictationContextCategory.allCases.map {
                        ($0, settings.s1MiniStyling(for: $0))
                    }), appRules: settings.dictationAppRules)))
        if mode == .profile {
            return (configuration, makeProfile(configuration: configuration, credentials: profileCredentials))
        }
        // Resolve only the selected route's credential. No Settings/environment
        // reference survives into the cleanup operation.
        let routerKey = mode == .openRouter ? resolvedOpenRouterCredential(
            apiKey: settings.openRouterAPIKey, environmentReference: settings.openRouterAPIKeyEnvironmentVariable,
            environment: ProcessInfo.processInfo.environment) : nil
        let compatibleKey = mode == .openAICompatible ? settings.openAICompatibleAPIKey : ""
        return (configuration, make(configuration: configuration, openRouterCredential: routerKey,
                                    compatibleCredential: compatibleKey, validateS1Model: validateS1Model))
    }

    /// Resolve the exact endpoint scope once, before any suspension. Never fall
    /// back to legacy/environment credentials for a selected profile.
    static func makeProfile(configuration: RemyDictationConfiguration,
                            credentials: (any CleanupProfileCredentialStore)?,
                            session: URLSession = .shared) -> RemyCleanupRuntime {
        do {
            guard let profile = configuration.cleanup.profile else { throw RuntimeError.invalidProfile }
            let endpoint = try profile.validatedEndpoint()
            guard profile.capabilities.transport == .completeJSON else { throw RuntimeError.unsupportedProfileTransport }
            try CleanupLocalRouteValidation.validate(baseURL: profile.baseURL, model: profile.model,
                policy: profile.routePolicy, evidence: .unknown)
            var key = ""
            if let reference = profile.credential {
                guard let credentials else { throw RuntimeError.profileCredentialUnavailable }
                do {
                    guard let value = try credentials.resolve(reference, for: endpoint),
                          !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                        throw RuntimeError.profileCredentialUnavailable
                    }
                    key = value
                } catch { throw RuntimeError.profileCredentialUnavailable }
            }
            return make(configuration: configuration, profileCredential: key, profileSession: session)
        } catch {
            // Do not retain a backend/provider error that could carry secrets.
            let failure: RuntimeError
            if let typed = error as? RuntimeError { failure = typed }
            else if error is CleanupLocalRouteValidation.RouteError { failure = .unverifiedProfileRoute }
            else if error is CleanupProfileCredentialError { failure = .profileCredentialUnavailable }
            else { failure = .invalidProfile }
            return RemyCleanupRuntime(configuration: configuration) { _ in throw failure }
        }
    }

    /// Explicit captured-key arguments also permit pure tests without Settings,
    /// Keychain or environment mutation. Missing keys never defer to environment.
    static func make(configuration: RemyDictationConfiguration, openRouterCredential: String? = nil,
                     compatibleCredential: String = "",
                     profileCredential: String? = nil, profileSession: URLSession = .shared,
                     validateS1Model: (@MainActor () async throws -> URL)? = nil) -> RemyCleanupRuntime {
        let cleanup = configuration.cleanup
        let vocabulary = configuration.capture.vocabulary
        let language = configuration.capture.language
        let frozenRouterKey = openRouterCredential?.trimmingCharacters(in: .whitespacesAndNewlines)
        let frozenCompatibleKey = compatibleCredential
        return RemyCleanupRuntime(configuration: configuration, contextualOperation: { text, context in
            switch cleanup.mode {
            case .profile:
                guard let profile = cleanup.profile else { throw RuntimeError.invalidProfile }
                _ = try profile.validatedEndpoint()
                guard profile.capabilities.transport == .completeJSON else { throw RuntimeError.unsupportedProfileTransport }
                try CleanupLocalRouteValidation.validate(baseURL: profile.baseURL, model: profile.model,
                    policy: profile.routePolicy, evidence: .unknown)
                guard profile.credential == nil || profileCredential != nil else { throw RuntimeError.profileCredentialUnavailable }
                do {
                    switch profile.adapter {
                case .openAICompatible:
                    return try await OpenAICompatiblePostProcessingService.process(text: text,
                        baseURL: profile.baseURL, model: profile.model, apiKey: profileCredential ?? "",
                        prompt: profile.prompt, vocabulary: vocabulary, context: context, session: profileSession,
                        timeoutSeconds: profile.timeoutSeconds, profileCapabilities: profile.capabilities)
                case .ollama:
                    return try await OllamaPostProcessingService.process(text: text,
                        baseURL: profile.baseURL, model: profile.model, reasoningEnabled: cleanup.reasoningEnabled,
                        prompt: profile.prompt, vocabulary: vocabulary, context: context, session: profileSession,
                        timeoutSeconds: profile.timeoutSeconds, profileCapabilities: profile.capabilities,
                        apiKey: profileCredential ?? "")
                    }
                } catch is CancellationError { throw CancellationError() }
                catch let error as RemoteCleanupInputPolicyError { throw error }
                catch {
                    try Task.checkCancellation()
                    throw RemyCleanupFailure.classify(error)
                }
            case .none, .fluidAudioVocabulary:
                return text
            case .appleIntelligence:
                guard #available(macOS 26, *), case .available = AIPostProcessingService.availability else {
                    throw RemyCleanupFailure.modelUnavailable
                }
                return try await AIPostProcessingService.process(text: text, prompt: cleanup.prompt,
                                                                 vocabulary: vocabulary, context: context)
            case .s1Mini:
                guard language == .english else { throw RemyCleanupFailure.unsupportedLanguage }
                guard let url = cleanup.s1ModelURL, url.isFileURL,
                      let validateS1Model else { throw RuntimeError.missingValidatedS1Model }
                let validated = try await validateS1Model()
                try Task.checkCancellation()
                guard validated.isFileURL, validated.standardizedFileURL == url.standardizedFileURL else {
                    throw RuntimeError.s1ModelChanged
                }
                return try await S1MiniPostProcessingService.process(text: text, modelURL: url,
                    styling: cleanup.contextPolicy.s1Styling(for: context?.category, fallback: cleanup.s1Styling), structure: cleanup.s1Structure,
                    contextSetting: cleanup.s1ContextSetting, context: context)
            case .ollama:
                return try await OllamaPostProcessingService.process(text: text, baseURL: cleanup.baseURL,
                    model: cleanup.model, reasoningEnabled: cleanup.reasoningEnabled,
                    prompt: cleanup.prompt, vocabulary: vocabulary, context: context)
            case .openRouter:
                guard let key = frozenRouterKey, !key.isEmpty else { throw RuntimeError.missingCapturedCredential }
                return try await OpenRouterPostProcessingService.process(text: text, model: cleanup.model,
                    prompt: cleanup.prompt, vocabulary: vocabulary, apiKey: key,
                    apiKeyEnvironmentVariable: "", context: context, reasoningEnabled: cleanup.reasoningEnabled)
            case .openAICompatible:
                return try await OpenAICompatiblePostProcessingService.process(text: text, baseURL: cleanup.baseURL,
                    model: cleanup.model, apiKey: frozenCompatibleKey, prompt: cleanup.prompt,
                    vocabulary: vocabulary, context: context)
            }
        })
    }

    /// Pure resolution of the legacy route. Freeze this result at capture, not
    /// the environment-variable name. Empty results become a static failure.
    static func resolvedOpenRouterCredential(apiKey: String, environmentReference: String,
                                             environment: [String: String]) -> String? {
        let direct = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if !direct.isEmpty { return direct }
        let reference = environmentReference.trimmingCharacters(in: .whitespacesAndNewlines)
        if reference.hasPrefix("sk-or-") { return reference }
        let name = reference.isEmpty ? OpenRouterPostProcessingService.defaultAPIKeyEnvironmentVariable : reference
        let resolved = environment[name]?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return resolved.isEmpty ? nil : resolved
    }
}
