import Foundation

/// Transport-independent policy. No HTTP client is activated by these contracts.
/// A future URLSession delegate must cancel, rather than follow, redirects using
/// this decision; validating only the final response URL would be too late.
nonisolated enum CleanupRedirectDecision: Equatable, Sendable {
    case rejectCrossOrigin
    case rejectRedirect
}
nonisolated enum CleanupHTTPFailure: Equatable, Sendable {
    case authentication, rateLimited, server, rejectedStatus, invalidResponse
}
nonisolated enum CleanupTransportPolicy {
    static func redirect(from source: URL, to destination: URL) -> CleanupRedirectDecision {
        guard origin(source) == origin(destination) else { return .rejectCrossOrigin }
        // Same-origin redirects can change provider routing too. A profile must
        // be explicitly edited instead of silently accepting a new endpoint.
        return .rejectRedirect
    }

    static func failure(statusCode: Int) -> CleanupHTTPFailure? {
        switch statusCode {
        case 200...299: nil
        case 401, 403: .authentication
        case 429: .rateLimited
        case 500...599: .server
        case 100...599: .rejectedStatus
        default: .invalidResponse
        }
    }

    private static func origin(_ url: URL) -> String? {
        guard let parts = URLComponents(url: url, resolvingAgainstBaseURL: false),
              let scheme = parts.scheme?.lowercased(), let host = parts.host?.lowercased(),
              ["http", "https"].contains(scheme), parts.user == nil, parts.password == nil else { return nil }
        return "\(scheme)://\(host):\(parts.port ?? (scheme == "https" ? 443 : 80))"
    }
}
