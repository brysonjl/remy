import Foundation
import XCTest
@testable import Dictate_Anywhere

@MainActor final class RemyCleanupAttemptTests: XCTestCase {
    private func attempt(_ clock: CleanupAttemptClock, gate: CleanupAttemptWorker,
                         token: Int = 1, raw: String = "corrected raw") -> RemyCleanupAttempt<Int> {
        RemyCleanupAttempt(token: token, correctedRaw: raw, now: { clock.now },
            sleep: { try await clock.sleep($0) }, operation: { await gate.run() })
    }

    func testEightSecondDeadlineReturnsFallbackBeforeNoncooperativeWorkerJoins() async {
        let clock = CleanupAttemptClock()
        let gate = CleanupAttemptWorker()
        let attempt = attempt(clock, gate: gate, raw: " \t原文 👩🏽‍💻\r\n")
        await gate.waitUntilStarted()
        await clock.waitUntilSleeping()
        clock.advance(by: .milliseconds(7_999))
        XCTAssertNil(attempt.outcome)
        clock.advance(by: .milliseconds(1))
        let outcome = await attempt.result()
        XCTAssertEqual(outcome, .fallback(reason: .timeout, correctedRaw: " \t原文 👩🏽‍💻\r\n"))
        XCTAssertTrue(attempt.isDraining)
        XCTAssertFalse(attempt.isWorkerFinished)
        XCTAssertEqual(attempt.decisionSequence, 1)
        XCTAssertEqual(attempt.failureCategory, .timeout)
        XCTAssertEqual(attempt.durationMilliseconds, 8_000)
        gate.finish("late output")
        clock.now += .seconds(20)
        await attempt.joinWorker()
        XCTAssertTrue(attempt.isWorkerFinished)
        XCTAssertFalse(attempt.isDraining)
        XCTAssertEqual(attempt.outcome, outcome)
        XCTAssertEqual(attempt.failureCategory, .timeout)
        XCTAssertEqual(attempt.durationMilliseconds, 8_000)
    }

    func testSuccessBeforeDeadlineWinsAndDuplicateActionsCannotReplaceIt() async {
        let clock = CleanupAttemptClock()
        let gate = CleanupAttemptWorker()
        let attempt = attempt(clock, gate: gate)
        await gate.waitUntilStarted()
        await clock.waitUntilSleeping()
        clock.advance(by: .milliseconds(7_999))
        gate.finish("cleaned")
        let result = await attempt.result()
        XCTAssertEqual(result, .cleaned("cleaned"))
        XCTAssertNil(attempt.failureCategory)
        XCTAssertEqual(attempt.durationMilliseconds, 7_999)
        XCTAssertFalse(attempt.useRawNow())
        XCTAssertFalse(attempt.cancel())
        clock.advance(by: .seconds(1))
        await attempt.joinWorker()
        XCTAssertEqual(attempt.outcome, .cleaned("cleaned"))
        XCTAssertEqual(attempt.decisionSequence, 1)
        XCTAssertNil(attempt.failureCategory)
        XCTAssertEqual(attempt.durationMilliseconds, 7_999)
    }

    func testSuccessAtDeadlineCannotBeatDelayedTimerCallback() async {
        let clock = CleanupAttemptClock()
        let gate = CleanupAttemptWorker()
        let attempt = attempt(clock, gate: gate)
        await gate.waitUntilStarted()
        await clock.waitUntilSleeping()
        // Advance the clock without waking its timer: provider callback itself
        // must enforce the absolute deadline, independent of task scheduling.
        clock.now += .seconds(8)
        gate.finish("late at boundary")
        let result = await attempt.result()
        XCTAssertEqual(result, .fallback(reason: .timeout, correctedRaw: "corrected raw"))
        await attempt.joinWorker()
    }

    func testUseRawIsImmediateOneShotAndLateOldAttemptCannotChangeNewAttempt() async {
        let oldClock = CleanupAttemptClock()
        let oldGate = CleanupAttemptWorker()
        let old = attempt(oldClock, gate: oldGate, token: 1, raw: "old raw")
        await oldGate.waitUntilStarted()
        oldClock.now += .milliseconds(250)
        XCTAssertTrue(old.useRawNow())
        XCTAssertFalse(old.useRawNow())
        XCTAssertFalse(old.cancel())
        let oldResult = await old.result()
        XCTAssertEqual(oldResult, .fallback(reason: .useRaw, correctedRaw: "old raw"))
        let newClock = CleanupAttemptClock()
        let newGate = CleanupAttemptWorker()
        let current = attempt(newClock, gate: newGate, token: 2, raw: "new raw")
        await newGate.waitUntilStarted()
        oldGate.finish("stale result")
        await old.joinWorker()
        XCTAssertNil(current.outcome)
        XCTAssertEqual(current.token, 2)
        newGate.finish("new clean")
        let result = await current.result()
        XCTAssertEqual(result, .cleaned("new clean"))
        await current.joinWorker()
        XCTAssertEqual(old.outcome, oldResult)
        XCTAssertEqual(old.failureCategory, .useRaw)
        XCTAssertEqual(old.durationMilliseconds, 250)
    }

    func testExplicitCancelAndCancelledWaiterNeverSelectFallback() async {
        for cancelWaiter in [false, true] {
            let clock = CleanupAttemptClock()
            let gate = CleanupAttemptWorker()
            let attempt = attempt(clock, gate: gate)
            await gate.waitUntilStarted()
            clock.now += .milliseconds(400)
            let waiter = Task { await attempt.result() }
            if cancelWaiter { waiter.cancel() }
            else { XCTAssertTrue(attempt.cancel()) }
            let result = await waiter.value
            XCTAssertEqual(result, .cancelled)
            XCTAssertFalse(attempt.useRawNow())
            gate.finish("late cancelled output")
            await attempt.joinWorker()
            XCTAssertEqual(attempt.outcome, .cancelled)
            XCTAssertEqual(attempt.decisionSequence, 1)
            XCTAssertEqual(attempt.failureCategory, .cancelled)
            XCTAssertEqual(attempt.durationMilliseconds, 400)
        }
    }

    func testProviderFailureSanitizesErrorAndMultipleWaitersSeeSameDecision() async {
        let clock = CleanupAttemptClock()
        let gate = CleanupAttemptWorker()
        let attempt = RemyCleanupAttempt(token: 1, correctedRaw: "raw", now: { clock.now },
            sleep: { try await clock.sleep($0) }, operation: {
                _ = await gate.run()
                throw NSError(domain: "secret-bearing-provider-error", code: 7)
            })
        await gate.waitUntilStarted()
        let first = Task { await attempt.result() }
        let second = Task { await attempt.result() }
        gate.finish("ignored")
        let firstResult = await first.value
        let secondResult = await second.value
        XCTAssertEqual(firstResult, .fallback(reason: .providerFailure, correctedRaw: "raw"))
        XCTAssertEqual(firstResult, secondResult)
        XCTAssertFalse(String(describing: firstResult).contains("secret-bearing-provider-error"))
        XCTAssertEqual(attempt.failureCategory, .providerFailure)
        XCTAssertFalse(attempt.failureCategory!.message.contains("secret-bearing-provider-error"))
        await attempt.joinWorker()
        XCTAssertEqual(attempt.decisionSequence, 1)
    }

    func testExpiredConstructionSkipsProviderEntirely() async {
        let clock = CleanupAttemptClock()
        var calls = 0
        let attempt = RemyCleanupAttempt(token: 1, correctedRaw: "raw", timeout: .zero,
            now: { clock.now }, sleep: { try await clock.sleep($0) }, operation: {
                calls += 1
                return "never eligible"
            })
        let result = await attempt.result()
        XCTAssertEqual(result, .fallback(reason: .timeout, correctedRaw: "raw"))
        await attempt.joinWorker()
        XCTAssertEqual(calls, 0)
        XCTAssertEqual(attempt.failureCategory, .timeout)
        XCTAssertEqual(attempt.durationMilliseconds, 0)
    }



    func testEmptyResponseFailsButUnchangedTextRemainsSuccessful() async {
        for output in ["", " \n\t", "raw"] {
            let attempt = RemyCleanupAttempt(token: 1, correctedRaw: "raw", operation: { output })
            let result = await attempt.result()
            if output == "raw" {
                XCTAssertEqual(result, .cleaned("raw"))
                XCTAssertNil(attempt.failureCategory)
            } else {
                XCTAssertEqual(result, .fallback(reason: .providerFailure, correctedRaw: "raw"))
                XCTAssertEqual(attempt.failureCategory, .invalidResponse)
            }
            await attempt.joinWorker()
        }
    }


}

@MainActor final class CleanupAttemptWorker {
    private var started = false
    private var startWaiters: [CheckedContinuation<Void, Never>] = []
    private var continuation: CheckedContinuation<String, Never>?
    func run() async -> String {
        started = true
        let pending = startWaiters; startWaiters.removeAll()
        pending.forEach { $0.resume() }
        // Intentionally noncooperative: cancellation alone cannot resume it.
        return await withCheckedContinuation { continuation = $0 }
    }
    func waitUntilStarted() async {
        if started { return }
        await withCheckedContinuation { startWaiters.append($0) }
    }
    func finish(_ value: String) { continuation?.resume(returning: value); continuation = nil }
}

@MainActor final class CleanupAttemptClock {
    var now = ContinuousClock.now
    private var sleepers: [UUID: (ContinuousClock.Instant, CheckedContinuation<Void, Error>)] = [:]
    private var observers: [CheckedContinuation<Void, Never>] = []
    func sleep(_ duration: Duration) async throws {
        let id = UUID()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                if Task.isCancelled { continuation.resume(throwing: CancellationError()); return }
                sleepers[id] = (now + duration, continuation)
                let pending = observers; observers.removeAll()
                pending.forEach { $0.resume() }
            }
        } onCancel: {
            Task { @MainActor in self.sleepers.removeValue(forKey: id)?.1.resume(throwing: CancellationError()) }
        }
    }
    func waitUntilSleeping() async {
        if !sleepers.isEmpty { return }
        await withCheckedContinuation { observers.append($0) }
    }
    func advance(by duration: Duration) {
        now += duration
        let due = sleepers.filter { $0.value.0 <= now }
        for (id, value) in due { sleepers.removeValue(forKey: id); value.1.resume() }
    }
}
