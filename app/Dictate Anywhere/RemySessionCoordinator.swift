import Foundation

/// Foreground-session authority around retained ASR/provider/history adapters.
/// Configuration is an immutable value snapshot supplied by the caller. Keep
/// reference-backed mutable settings and credential values outside this type.
/// History reprocessing must not call `begin`: it has no foreground authority.
@MainActor
final class RemySessionCoordinator<Configuration> {
    struct Token: Hashable {
        let id: UUID
        let generation: UInt64
    }

    enum State: Equatable {
        case idle, preparing, recording, finalizing, cleaning, ready, delivering
        case completed, cancelled, failed
    }

    struct DeliveryPermit: Hashable {
        let id: UUID
        let token: Token
        let authorization: RemyDeliveryAuthorization

        nonisolated static func == (lhs: Self, rhs: Self) -> Bool {
            lhs.id == rhs.id && lhs.token == rhs.token && lhs.authorization === rhs.authorization
        }

        nonisolated func hash(into hasher: inout Hasher) {
            hasher.combine(id)
            hasher.combine(token)
            hasher.combine(ObjectIdentifier(authorization))
        }
    }

    enum CancellationOutcome: Equatable {
        case cancelled
        case clipboardAlreadyWritten
        case alreadyDispatched
        case ignored
    }

    enum CancellationReason: Equatable {
        case user, superseded, shutdown, inputUnavailable
    }

    struct Failure: Equatable {
        enum Stage: Equatable { case session, capture, recognition, cleanup, history, delivery }
        enum Category: Equatable { case timeout, unavailable, invalidInput, persistence, provider, unknown }
        let stage: Stage
        let category: Category
        let retryable: Bool
    }

    private(set) var failure: Failure?
    private(set) var cancellationReason: CancellationReason?
    private(set) var state: State = .idle
    /// Per-session sequence of accepted state transitions and the write-once raw
    /// event. Duplicate events and side-effect permit consumption do not advance it.
    private(set) var eventSequence: UInt64 = 0
    private var generation: UInt64 = 0
    private var token: Token?
    private var snapshot: Configuration?
    private var raw: String?
    private var permit: DeliveryPermit?

    func begin(configuration: Configuration, id: UUID = UUID()) -> Token? {
        guard !hasActiveSession, generation < UInt64.max else { return nil }
        generation += 1
        let next = Token(id: id, generation: generation)
        token = next
        snapshot = configuration
        raw = nil
        failure = nil
        cancellationReason = nil
        permit = nil
        eventSequence = 0
        transition(to: .preparing)
        return next
    }

    func isCurrent(_ candidate: Token) -> Bool {
        token == candidate && hasActiveSession
    }

    func configuration(for candidate: Token) -> Configuration? {
        isCurrent(candidate) ? snapshot : nil
    }

    func finalRaw(for candidate: Token) -> String? {
        isCurrent(candidate) ? raw : nil
    }

    @discardableResult
    func markRecording(_ candidate: Token) -> Bool {
        guard isCurrent(candidate), state == .preparing else { return false }
        transition(to: .recording)
        return true
    }

    @discardableResult
    func requestStop(_ candidate: Token) -> Bool {
        guard isCurrent(candidate), state == .recording else { return false }
        transition(to: .finalizing)
        return true
    }

    /// Raw is write-once, even if an adapter repeats the identical final result.
    @discardableResult
    func recordFinalRaw(_ candidate: Token, text: String) -> Bool {
        guard isCurrent(candidate), state == .finalizing, raw == nil else { return false }
        raw = text
        eventSequence += 1
        return true
    }

    @discardableResult
    func beginCleanup(_ candidate: Token) -> Bool {
        guard isCurrent(candidate), state == .finalizing, raw != nil else { return false }
        transition(to: .cleaning)
        return true
    }

    @discardableResult
    func markReady(_ candidate: Token) -> Bool {
        guard isCurrent(candidate), raw != nil,
              state == .finalizing || state == .cleaning else { return false }
        transition(to: .ready)
        return true
    }

    /// Authorizing reserves one delivery attempt but performs no OS effect.
    /// A cancellation can still revoke the permit before or between effects.
    func authorizeDelivery(_ candidate: Token) -> DeliveryPermit? {
        guard isCurrent(candidate), state == .ready, permit == nil else { return nil }
        let next = DeliveryPermit(id: UUID(), token: candidate, authorization: RemyDeliveryAuthorization())
        permit = next
        transition(to: .delivering)
        return next
    }

    /// Convenience for synchronous main-actor adapters. Queued output adapters
    /// pass `permit.authorization` to the worker and consume at the actual effect.
    /// The worker must recordCopied() after a real successful clipboard write.
    func consumeClipboardPermit(_ candidate: DeliveryPermit) -> Bool {
        guard valid(candidate) else { return false }
        return candidate.authorization.consumeClipboard()
    }

    /// External focus validation remains an output-adapter responsibility (A07).
    func consumePastePermit(_ candidate: DeliveryPermit) -> Bool {
        guard valid(candidate) else { return false }
        return candidate.authorization.consumePaste()
    }

    @discardableResult
    func cancel(_ candidate: Token, reason: CancellationReason = .user) -> CancellationOutcome {
        guard isCurrent(candidate) else { return .ignored }
        // Revocation and effect claims share one lock in the worker-safe lease.
        // Its returned snapshot is the cancellation linearization point.
        let delivery = permit?.authorization.revoke()
        guard delivery?.pasteClaimed != true else { return .alreadyDispatched }
        let outcome: CancellationOutcome = delivery?.copied == true ? .clipboardAlreadyWritten : .cancelled
        permit = nil
        cancellationReason = reason
        transition(to: .cancelled)
        return outcome
    }

    @discardableResult
    func revoke(_ candidate: Token, reason: CancellationReason = .user) -> CancellationOutcome { cancel(candidate, reason: reason) }

    /// Ready can complete without delivery for empty/silent input; callers must
    /// avoid authorizing an output for that path. Copy-only completes delivering.
    @discardableResult
    func complete(_ candidate: Token) -> Bool {
        guard isCurrent(candidate), state == .ready || state == .delivering else { return false }
        permit?.authorization.revoke()
        permit = nil
        transition(to: .completed)
        return true
    }

    @discardableResult
    func fail(_ candidate: Token, failure: Failure = Failure(stage: .session, category: .unknown, retryable: false)) -> Bool {
        guard isCurrent(candidate) else { return false }
        permit?.authorization.revoke()
        permit = nil
        self.failure = failure
        transition(to: .failed)
        return true
    }

    private var hasActiveSession: Bool {
        switch state {
        case .preparing, .recording, .finalizing, .cleaning, .ready, .delivering: true
        case .idle, .completed, .cancelled, .failed: false
        }
    }

    private func valid(_ candidate: DeliveryPermit) -> Bool {
        isCurrent(candidate.token) && state == .delivering && permit == candidate
    }

    private func transition(to next: State) {
        state = next
        eventSequence += 1
    }
}
