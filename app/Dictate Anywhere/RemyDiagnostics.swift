import Foundation

/// Default diagnostics describe operations, never error descriptions, provider
/// bodies, transcripts, credentials, paths, device identities, or prompts.
nonisolated enum RemyDiagnostics {
    static let fallbackSubsystem = "io.github.brysonjl.remy"

    static var subsystem: String {
        subsystem(for: Bundle.main.bundleIdentifier)
    }

    static func subsystem(for bundleIdentifier: String?) -> String {
        guard let bundleIdentifier, !bundleIdentifier.isEmpty else { return fallbackSubsystem }
        return bundleIdentifier
    }

    /// A closed vocabulary keeps arbitrary LocalizedError/provider payloads
    /// out of logging, including diagnostic capture modes that reveal .private.
    static func errorCategory(_ error: Error) -> String {
        if error is CancellationError { return "cancelled" }
        if let error = error as? URLError {
            switch error.code {
            case .cancelled: return "cancelled"
            case .timedOut: return "timeout"
            default: return "transport"
            }
        }
        return "other"
    }
}
