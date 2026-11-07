import Foundation
import FoundationModels

/// Full application/provider mapping stays outside the dependency-free primitive.
extension RemyCleanupFailure {
    @MainActor
    static func classify(_ error: Error) -> Self {
        if let common = classifyCommon(error) { return common }
        if error is RemoteCleanupInputPolicyError { return .requestBudgetExceeded }
        if let error = error as? CleanupResponseError {
            switch error {
            case .inputCannotFit: return .inputTooLong
            case .incompleteResponse: return .incompleteResponse
            }
        }
        if let error = error as? CleanupAnswerParseError {
            switch error {
            case .incomplete: return .incompleteResponse
            case .cancelled: return .cancelled
            case .responseTooLarge, .recordTooLarge, .answerTooLarge: return .responseTooLarge
            case .invalidLimits: return .invalidConfiguration
            case .refused: return .requestRejected
            case .invalidUTF8, .malformed, .toolCall, .unsupportedContent, .alreadyFinished: return .invalidResponse
            }
        }
        if let error = error as? CleanupBoundedHTTPTransport.TransportError {
            switch error {
            case .invalidRequest, .redirectRejected, .crossOriginRedirect: return .invalidConfiguration
            case .invalidResponse: return .invalidResponse
            case .responseTooLarge: return .responseTooLarge
            }
        }
        if let error = error as? RemyCleanupRuntime.RuntimeError {
            switch error {
            case .missingCapturedCredential, .profileCredentialUnavailable: return .authentication
            case .missingValidatedS1Model, .s1ModelChanged: return .modelUnavailable
            case .invalidProfile, .unsupportedProfileTransport, .unverifiedProfileRoute: return .invalidConfiguration
            case .profileRequestFailed: return .providerFailure
            }
        }
        if error is CleanupProfileCredentialError { return .authentication }
        if error is CleanupEndpointIdentity.ValidationError || error is CleanupProfile.ValidationError
            || error is CleanupLocalRouteValidation.RouteError { return .invalidConfiguration }
        if let error = error as? OpenAICompatiblePostProcessingService.ServiceError {
            switch error {
            case .missingModel, .invalidBaseURL: return .invalidConfiguration
            case .invalidResponse, .emptyResponse: return .invalidResponse
            case .unexpectedStatus(let status): return classifyHTTPStatus(status)
            case .serverMessage: return .providerFailure
            }
        }
        if let error = error as? OpenRouterPostProcessingService.ServiceError {
            switch error {
            case .missingModel: return .invalidConfiguration
            case .missingAPIKey: return .authentication
            case .invalidResponse, .emptyResponse: return .invalidResponse
            case .unexpectedStatus(let status): return classifyHTTPStatus(status)
            case .serverMessage: return .providerFailure
            }
        }
        if let error = error as? OllamaPostProcessingService.ServiceError {
            switch error {
            case .missingModel, .invalidBaseURL, .missingCLI: return .invalidConfiguration
            case .invalidResponse, .emptyResponse: return .invalidResponse
            case .unexpectedStatus(let status): return classifyHTTPStatus(status)
            case .serverMessage: return .providerFailure
            }
        }
        if let error = error as? S1MiniServiceError {
            switch error {
            case .modelNotDownloaded, .modelLoadFailed, .contextCreationFailed: return .modelUnavailable
            case .transcriptTooLong: return .inputTooLong
            case .outputLimitReached: return .incompleteResponse
            case .invalidOutput: return .invalidResponse
            case .tokenizationFailed, .promptEvaluationFailed, .tokenEvaluationFailed: return .providerFailure
            }
        }
        if #available(macOS 26, *), let error = error as? LanguageModelSession.GenerationError {
            switch error {
            case .exceededContextWindowSize: return .inputTooLong
            case .assetsUnavailable: return .modelUnavailable
            case .unsupportedLanguageOrLocale: return .unsupportedLanguage
            case .rateLimited: return .rateLimited
            case .decodingFailure: return .invalidResponse
            case .guardrailViolation, .refusal: return .requestRejected
            case .unsupportedGuide: return .invalidConfiguration
            case .concurrentRequests: return .serverUnavailable
            @unknown default: return .providerFailure
            }
        }
        if error is DecodingError { return .invalidResponse }
        return .providerFailure
    }

    private static func classifyHTTPStatus(_ status: Int) -> Self {
        switch status {
        case 401, 403: return .authentication
        case 408, 504: return .timeout
        case 413: return .inputTooLong
        case 429: return .rateLimited
        case 500...599: return .serverUnavailable
        case 400...499: return .requestRejected
        default: return .invalidResponse
        }
    }
}
