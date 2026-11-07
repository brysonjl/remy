import Foundation

/// Frozen per dictation. Changing this preference affects the next session.
nonisolated enum RemyDictationDestination: String, CaseIterable, Codable, Sendable {
    case originalField
    case currentField

    var displayName: String {
        switch self {
        case .originalField: "Where dictation started"
        case .currentField: "Where I’m focused when it finishes"
        }
    }
    var caption: String {
        switch self {
        case .originalField:
            "Returns to the app and field where dictation started. If that field cannot be restored, copy the result and paste it manually."
        case .currentField:
            "Chooses your focused app and field after cleanup finishes. If that field is unavailable, copy the result and paste it manually."
        }
    }
    static func decodeStored(_ value: String?) -> Self {
        value.flatMap(Self.init(rawValue:)) ?? .originalField
    }
}

/// Chooses destination authority without touching native focus, AX or clipboard.
/// Native callbacks must independently gate any focus effect on the same lease.
@MainActor enum RemyDestinationRouter {
    struct Destination: Sendable {
        let processIdentifier: pid_t?
        let target: RemyInsertionTarget
        let allowsPaste: Bool
        static let unavailable = Destination(processIdentifier: nil, target: .unavailable, allowsPaste: false)
    }

    static func resolve(
        policy: RemyDictationDestination,
        original: Destination,
        authorization: RemyDeliveryAuthorization,
        captureCurrent: @MainActor () async -> Destination,
        restoreOriginal: @MainActor () async -> Bool
    ) async -> Destination {
        func eligible() -> Bool {
            !Task.isCancelled && authorization.isEligible && !authorization.snapshot.pasteClaimed
        }
        guard eligible() else { return .unavailable }
        switch policy {
        case .originalField:
            guard original.allowsPaste else {
                return Destination(processIdentifier: original.processIdentifier, target: original.target, allowsPaste: false)
            }
            let restored = await restoreOriginal()
            guard eligible() else { return .unavailable }
            return Destination(processIdentifier: original.processIdentifier, target: original.target,
                               allowsPaste: restored)
        case .currentField:
            // Capture exactly once at the caller's post-cleanup boundary. Never
            // substitute the original proof if the selected field is unavailable.
            let current = await captureCurrent()
            guard eligible() else { return .unavailable }
            return current
        }
    }
}
