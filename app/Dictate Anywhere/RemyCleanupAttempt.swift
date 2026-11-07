import Foundation

/// A foreground cleanup decision and its separately owned worker lifetime.
/// The caller still checks its session token before history or output effects.
/// Returning a fallback does not join a noncooperative provider: retain this
/// attempt and await joinWorker before reusing/unloading its shared local runtime.
/// Application/provider errors must be mapped to RemyCleanupFailure at the
/// operation boundary; this primitive recognizes Foundation failures directly.
@MainActor
final class RemyCleanupAttempt<Token: Hashable> {
    enum FallbackReason: Equatable { case timeout, useRaw, providerFailure }
    enum Outcome: Equatable {
        case cleaned(String)
        case fallback(reason: FallbackReason, correctedRaw: String)
        case cancelled
    }
    let token: Token
    let correctedRaw: String
    let deadline: ContinuousClock.Instant
    let startedAt = Date()
    private(set) var durationMilliseconds: Int?
    private(set) var failureCategory: RemyCleanupFailure?
    private(set) var outcome: Outcome?
    private(set) var isWorkerFinished = false
    var isDraining: Bool { outcome != nil && !isWorkerFinished }
    /// Exactly one accepted decision, independent of duplicate callbacks/actions.
    private(set) var decisionSequence: UInt64 = 0
    private let now: @MainActor () -> ContinuousClock.Instant
    private let startedInstant: ContinuousClock.Instant
    private var worker: Task<Void, Never>?
    private var timer: Task<Void, Never>?
    private var waiters: [CheckedContinuation<Outcome, Never>] = []

    init(token: Token, correctedRaw: String, timeout: Duration = .seconds(8),
         now: @escaping @MainActor () -> ContinuousClock.Instant = { .now },
         sleep: @escaping @MainActor (Duration) async throws -> Void = { try await Task.sleep(for: $0) },
         operation: @escaping @MainActor () async throws -> String) {
        self.token = token
        self.correctedRaw = correctedRaw
        self.now = now
        startedInstant = now()
        deadline = startedInstant + max(.zero, timeout)
        worker = Task { @MainActor [weak self] in
            guard self?.mayStartWorker() == true else {
                self?.isWorkerFinished = true
                return
            }
            do {
                let value = try await operation()
                self?.workerCompleted(value)
            } catch {
                // Error bodies and credentials never become decision metadata.
                self?.workerFailed(error)
            }
            self?.isWorkerFinished = true
        }
        timer = Task { @MainActor [weak self] in
            do {
                while let remaining = self.map({ $0.deadline - $0.now() }), remaining > .zero {
                    try await sleep(remaining)
                    guard !Task.isCancelled else { return }
                }
            } catch {
                if Task.isCancelled { return }
                // A broken injected clock must not leave cleanup unbounded.
                self?.workerFailed(error)
                return
            }
            guard !Task.isCancelled else { return }
            self?.timeoutReached()
        }
    }

    deinit { worker?.cancel(); timer?.cancel() }

    /// Cancellation of any foreground waiter cancels this attempt, not delivery
    /// of raw text. Timeout/Use raw are explicit, different decisions.
    func result() async -> Outcome {
        await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                if Task.isCancelled { _ = cancel() }
                if let outcome { continuation.resume(returning: outcome) }
                else { waiters.append(continuation) }
            }
        } onCancel: {
            Task { @MainActor [weak self] in _ = self?.cancel() }
        }
    }

    @discardableResult
    func useRawNow() -> Bool {
        settle(.fallback(reason: .useRaw, correctedRaw: correctedRaw), failure: .useRaw)
    }

    @discardableResult
    func cancel() -> Bool { settle(.cancelled, failure: .cancelled) }

    /// This can take longer than the deadline. Call for resource ownership,
    /// never as a prerequisite to displaying/persisting the fallback decision.
    func joinWorker() async { await worker?.value }

    private func mayStartWorker() -> Bool {
        guard outcome == nil else { return false }
        guard now() < deadline else { timeoutReached(); return false }
        return true
    }
    private func workerCompleted(_ value: String) {
        guard outcome == nil else { return }
        guard now() < deadline else { timeoutReached(); return }
        guard !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            workerFailed(RemyCleanupFailure.invalidResponse)
            return
        }
        _ = settle(.cleaned(value))
    }
    private func workerFailed(_ error: Error) {
        guard outcome == nil else { return }
        if now() >= deadline { timeoutReached() }
        else {
            _ = settle(.fallback(reason: .providerFailure, correctedRaw: correctedRaw),
                failure: RemyCleanupFailure.classifyCommon(error) ?? .providerFailure)
        }
    }
    private func timeoutReached() {
        _ = settle(.fallback(reason: .timeout, correctedRaw: correctedRaw), failure: .timeout)
    }
    @discardableResult
    private func settle(_ decision: Outcome, failure: RemyCleanupFailure? = nil) -> Bool {
        guard outcome == nil else { return false }
        outcome = decision
        failureCategory = failure
        let elapsed = max(.zero, now() - startedInstant).components
        let milliseconds = Int(clamping: elapsed.seconds).multipliedReportingOverflow(by: 1_000)
        let total = milliseconds.partialValue.addingReportingOverflow(Int(elapsed.attoseconds / 1_000_000_000_000_000))
        durationMilliseconds = milliseconds.overflow || total.overflow ? Int.max : total.partialValue
        decisionSequence += 1
        timer?.cancel()
        // Cancellation is cooperative; an already running local call may drain
        // later. Its only callback is the rejected one-shot completion above.
        worker?.cancel()
        let pending = waiters
        waiters.removeAll()
        for continuation in pending { continuation.resume(returning: decision) }
        return true
    }
}
