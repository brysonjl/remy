import Foundation

/// The only failure detail allowed in history, diagnostics, and the overlay.
/// Never retain or interpolate a provider's error body, URL, or credentials.
nonisolated enum RemyCleanupFailure: String, Error, Sendable, CaseIterable {
    case timeout, useRaw, providerFailure, cancelled, transport, inputTooLong, requestBudgetExceeded
    case incompleteResponse, invalidResponse, authentication, rateLimited
    case serverUnavailable, invalidConfiguration, modelUnavailable, unsupportedLanguage
    case responseTooLarge, requestRejected

    var message: String {
        switch self {
        case .timeout: return "Cleanup did not finish before its time limit."
        case .useRaw: return "You chose to use the original transcript before cleanup finished."
        case .providerFailure: return "The cleanup provider failed without a recognized error."
        case .cancelled: return "Cleanup was cancelled before it finished."
        case .transport: return "Remy could not connect to the cleanup provider or the connection was interrupted."
        case .inputTooLong: return "The transcript and cleanup instructions exceed the model's request limit."
        case .requestBudgetExceeded: return "The transcript, instructions, and enabled context exceed Remy's cleanup request budget."
        case .incompleteResponse: return "The cleanup provider returned an incomplete response."
        case .invalidResponse: return "The cleanup provider returned an empty or invalid response."
        case .authentication: return "The cleanup credential is missing, unavailable, or was rejected."
        case .rateLimited: return "The cleanup provider is limiting requests. Try again later."
        case .serverUnavailable: return "The cleanup provider is temporarily unavailable."
        case .invalidConfiguration: return "The cleanup configuration or endpoint is invalid or unsupported."
        case .modelUnavailable: return "The selected cleanup model is unavailable or could not be loaded."
        case .unsupportedLanguage: return "The selected cleanup model does not support this dictation language."
        case .responseTooLarge: return "The cleanup provider's response exceeded the allowed size."
        case .requestRejected: return "The cleanup provider rejected the request. Check the model and cleanup settings."
        }
    }

    var overlayTitle: String {
        switch self {
        case .timeout: return "Cleanup timed out"
        case .useRaw: return "Raw transcript selected"
        case .cancelled: return "Cleanup cancelled"
        case .modelUnavailable, .unsupportedLanguage: return "Cleanup unavailable"
        case .inputTooLong, .requestBudgetExceeded: return "Cleanup input too long"
        default: return "Cleanup failed"
        }
    }

    /// Legacy and unknown values are deliberately never rendered verbatim.
    static func message(for category: String?) -> String {
        category.flatMap(Self.init(rawValue:))?.message
            ?? "Cleanup did not complete. No detailed reason was recorded."
    }

    /// Provider-independent failures shared by the deadline primitive and runtime.
    static func classifyCommon(_ error: Error) -> Self? {
        if let failure = error as? Self { return failure }
        if error is CancellationError { return .cancelled }
        if let error = error as? URLError {
            switch error.code {
            case .timedOut: return .timeout
            case .cancelled: return .cancelled
            case .userAuthenticationRequired, .userCancelledAuthentication: return .authentication
            case .badURL, .unsupportedURL: return .invalidConfiguration
            default: return .transport
            }
        }
        return nil
    }
}
