import Foundation

/// A session-owned, revocable delivery lease shared with queued OS workers.
/// Claims linearize authorization immediately before a synchronous side effect.
/// A claimed paste is conservatively irreversible/possibly dispatched even when
/// the OS later returns an error; it must never authorize a second fallback.
nonisolated final class RemyDeliveryAuthorization: @unchecked Sendable {
    struct Snapshot: Equatable, Sendable {
        let copied: Bool
        let pasteClaimed: Bool
    }

    private let lock = NSLock()
    private var revoked = false
    private var clipboardClaimed = false
    private var copied = false
    private var pasteClaimed = false

    var isRevoked: Bool { lock.withLock { revoked } }
    var isEligible: Bool { lock.withLock { !revoked } }
    var snapshot: Snapshot { lock.withLock { snapshotLocked() } }

    /// Revocation and returned effect classification share one linearization.
    @discardableResult
    func revoke() -> Snapshot {
        lock.withLock {
            revoked = true
            return snapshotLocked()
        }
    }

    func consumeClipboard() -> Bool {
        lock.withLock {
            guard !revoked, !clipboardClaimed else { return false }
            clipboardClaimed = true
            return true
        }
    }

    /// Called only after a real clipboard write/read-back succeeds. A clipboard
    /// authorization claim alone does not establish that text was copied.
    func recordCopied() {
        lock.withLock { copied = true }
    }

    /// Bounded synchronous clipboard mutation plus success recording. Revocation
    /// cannot classify the session between writing and recording copied=true.
    /// Initial ownership must already be consumed; retries/owned list rewrites
    /// use the same ownership and recheck revocation on each mutation.
    func performClipboardMutation(_ mutation: () -> Bool) -> Bool {
        lock.withLock {
            guard !revoked, clipboardClaimed else { return false }
            let succeeded = mutation()
            if succeeded { copied = true }
            return succeeded
        }
    }

    /// Serializes a bounded synchronous focus effect with revocation. This does
    /// not claim clipboard/paste authority. Never await or reenter this lease
    /// inside the closure; cancellation may wait for the in-flight OS request.
    func performFocusMutation(_ mutation: () -> Bool) -> Bool {
        lock.withLock {
            guard !revoked, !pasteClaimed else { return false }
            return mutation()
        }
    }

    func consumePaste() -> Bool {
        lock.withLock {
            guard !revoked, copied, !pasteClaimed else { return false }
            pasteClaimed = true
            return true
        }
    }

    private func snapshotLocked() -> Snapshot {
        Snapshot(copied: copied, pasteClaimed: pasteClaimed)
    }
}
