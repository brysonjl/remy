//
//  TransformProvider.swift
//  Dictate Anywhere
//
//  Routes a transform to the user's configured cleanup provider as one
//  freeform request: system = transform rules + prompt, user = selection.
//

import Foundation
import FoundationModels

enum TransformProviderRoute {
    typealias Completion = @MainActor (TransformRequest) async throws -> String

    case ready(providerName: String, Completion)
    case unavailable(TransformNotice)

    /// Values frozen from Settings when the shortcut fires. Credentials are
    /// resolved here and live only in the returned completion closure.
    struct Snapshot {
        var mode: TranscriptPostProcessingMode
        var appleIntelligenceAvailable = false
        var ollamaBaseURL = ""
        var ollamaModel = ""
        var ollamaReasoningEnabled = false
        var openRouterModel = ""
        var openRouterKey: String?
        var openRouterReasoningEnabled = false
        var compatibleBaseURL = ""
        var compatibleModel = ""
        var compatibleKey = ""
        var profile: CleanupProfile?
    }

    static func capture(settings: Settings, profile: CleanupProfile?,
                        credentials: (any CleanupProfileCredentialStore)?) -> Self {
        let mode = settings.transcriptPostProcessingMode
        var snapshot = Snapshot(mode: mode)
        switch mode {
        case .appleIntelligence:
            if #available(macOS 26, *), case .available = AIPostProcessingService.availability {
                snapshot.appleIntelligenceAvailable = true
            }
        case .ollama:
            snapshot.ollamaBaseURL = settings.ollamaBaseURL
            snapshot.ollamaModel = settings.ollamaModel
            snapshot.ollamaReasoningEnabled = settings.ollamaReasoningEnabled
        case .openRouter:
            snapshot.openRouterModel = settings.openRouterModel
            snapshot.openRouterReasoningEnabled = settings.openRouterReasoningEnabled
            snapshot.openRouterKey = RemyCleanupRuntime.resolvedOpenRouterCredential(
                apiKey: settings.openRouterAPIKey, environmentReference: settings.openRouterAPIKeyEnvironmentVariable,
                environment: ProcessInfo.processInfo.environment)
        case .openAICompatible:
            snapshot.compatibleBaseURL = settings.openAICompatibleBaseURL
            snapshot.compatibleModel = settings.openAICompatibleModel
            snapshot.compatibleKey = settings.openAICompatibleAPIKey
        case .profile:
            snapshot.profile = profile
        case .none, .fluidAudioVocabulary, .s1Mini:
            break
        }
        return resolve(snapshot, credentials: credentials)
    }

    /// Modes that can never run a transform, independent of their settings.
    static func modeNotice(_ mode: TranscriptPostProcessingMode) -> TransformNotice? {
        switch mode {
        case .none, .fluidAudioVocabulary: .cleanupOff
        // A small model trained for fixed cleanup controls; it does not
        // follow arbitrary rewrite instructions reliably.
        case .s1Mini: .providerUnsupported("S1-mini")
        case .appleIntelligence, .ollama, .openRouter, .openAICompatible, .profile: nil
        }
    }

    static func resolve(_ snapshot: Snapshot, credentials: (any CleanupProfileCredentialStore)?) -> Self {
        if let notice = modeNotice(snapshot.mode) { return .unavailable(notice) }
        switch snapshot.mode {
        case .none, .fluidAudioVocabulary, .s1Mini:
            return .unavailable(.cleanupOff)
        case .appleIntelligence:
            guard snapshot.appleIntelligenceAvailable else { return .unavailable(.providerUnavailable) }
            return .ready(providerName: "Apple Intelligence") { request in
                guard #available(macOS 26, *) else { throw RemyCleanupFailure.modelUnavailable }
                return try await AppleIntelligenceTransform.complete(request)
            }
        case .ollama:
            guard !snapshot.ollamaModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return .unavailable(.providerUnavailable)
            }
            if let notice = endpointNotice(snapshot.ollamaBaseURL) { return .unavailable(notice) }
            let baseURL = snapshot.ollamaBaseURL, model = snapshot.ollamaModel
            let reasoning = snapshot.ollamaReasoningEnabled
            return .ready(providerName: "Ollama") { request in
                try await OllamaPostProcessingService.transformCompletion(system: request.system, user: request.user,
                    baseURL: baseURL, model: model, reasoningEnabled: reasoning)
            }
        case .openRouter:
            guard let key = snapshot.openRouterKey, !key.isEmpty else { return .unavailable(.missingCredential) }
            guard !snapshot.openRouterModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return .unavailable(.providerUnavailable)
            }
            let model = snapshot.openRouterModel, reasoning = snapshot.openRouterReasoningEnabled
            return .ready(providerName: "OpenRouter") { request in
                try await OpenRouterPostProcessingService.transformCompletion(system: request.system, user: request.user,
                    model: model, apiKey: key, reasoningEnabled: reasoning)
            }
        case .openAICompatible:
            guard !snapshot.compatibleModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return .unavailable(.providerUnavailable)
            }
            if let notice = endpointNotice(snapshot.compatibleBaseURL) { return .unavailable(notice) }
            let baseURL = snapshot.compatibleBaseURL, model = snapshot.compatibleModel, key = snapshot.compatibleKey
            return .ready(providerName: "OpenAI Compatible") { request in
                try await OpenAICompatiblePostProcessingService.transformCompletion(system: request.system,
                    user: request.user, baseURL: baseURL, model: model, apiKey: key)
            }
        case .profile:
            return profileRoute(snapshot.profile, credentials: credentials)
        }
    }

    /// Remote endpoints must use HTTPS; loopback HTTP is allowed. The legacy
    /// provider fields accept a bare host, which their services treat as http.
    static func endpointNotice(_ baseURL: String) -> TransformNotice? {
        var normalized = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty else { return .providerUnavailable }
        if !normalized.contains("://") { normalized = "http://" + normalized }
        do {
            _ = try CleanupEndpointIdentity(baseURL: normalized)
            return nil
        } catch CleanupEndpointIdentity.ValidationError.insecureRemoteEndpoint {
            return .insecureEndpoint
        } catch {
            return .providerUnavailable
        }
    }

    /// Mirrors RemyCleanupRuntime.makeProfile: the profile's own endpoint
    /// scope, transport and local-only route policy, and its scoped credential.
    private static func profileRoute(_ profile: CleanupProfile?,
                                     credentials: (any CleanupProfileCredentialStore)?) -> Self {
        guard let profile, let endpoint = try? profile.validatedEndpoint(),
              profile.capabilities.transport == .completeJSON,
              (try? CleanupLocalRouteValidation.validate(baseURL: profile.baseURL, model: profile.model,
                  policy: profile.routePolicy, evidence: .unknown)) != nil else {
            return .unavailable(.invalidProfile)
        }
        var key = ""
        if let reference = profile.credential {
            guard let credentials,
                  let value = try? credentials.resolve(reference, for: endpoint),
                  !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                return .unavailable(.missingCredential)
            }
            key = value
        }
        return .ready(providerName: profile.name) { request in
            switch profile.adapter {
            case .openAICompatible:
                try await OpenAICompatiblePostProcessingService.transformCompletion(system: request.system,
                    user: request.user, baseURL: profile.baseURL, model: profile.model, apiKey: key,
                    timeoutSeconds: max(profile.timeoutSeconds, 60))
            case .ollama:
                try await OllamaPostProcessingService.transformCompletion(system: request.system, user: request.user,
                    baseURL: profile.baseURL, model: profile.model, reasoningEnabled: false,
                    thinkingControl: profile.capabilities.thinkingControl, apiKey: key,
                    timeoutSeconds: max(profile.timeoutSeconds, 60), discovery: profile.capabilities.discovery)
            }
        }
    }
}

@available(macOS 26, *)
enum AppleIntelligenceTransform {
    /// A fresh session per transform; no earlier text can leak into it.
    static func complete(_ request: TransformRequest) async throws -> String {
        let session = LanguageModelSession(model: .default, instructions: request.system)
        let response = try await session.respond(to: request.user, options: GenerationOptions(samplingMode: .greedy))
        return response.content
    }
}
