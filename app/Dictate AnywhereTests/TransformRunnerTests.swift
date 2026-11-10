import XCTest
@testable import Dictate_Anywhere

@MainActor
final class TransformRunnerTests: XCTestCase {
    private let pid: pid_t = 4_242
    private let instruction = TransformInstruction(name: "Polish", prompt: "Make it clearer.")

    /// Records every seam the runner touches; each behavior is overridable.
    private final class Harness {
        var route: TransformProviderRoute
        var selection: TransformSelectionResult
        var selectionMatches = true
        var frontmost: pid_t?
        var deliveryResult: TextInsertionResult = .success
        var requests: [TransformRequest] = []
        var reads = 0
        var deliveries: [(text: String, paste: Bool)] = []
        var events: [TransformEvent] = []

        init(pid: pid_t, response: @escaping (TransformRequest) async throws -> String = { _ in "Clearer text." }) {
            frontmost = pid
            selection = .selected(TransformSelection(text: "  rough text\n", processIdentifier: pid,
                                                     target: .unavailable))
            route = .unavailable(.providerUnavailable)
            route = .ready(providerName: "Fake") { [unowned self] request in
                self.requests.append(request)
                return try await response(request)
            }
        }

        func runner() -> TransformRunner {
            let runner = TransformRunner(dependencies: .init(
                route: { [unowned self] in self.route },
                readSelection: { [unowned self] in self.reads += 1; return self.selection },
                selectionStillMatches: { [unowned self] _ in self.selectionMatches },
                frontmostProcessIdentifier: { [unowned self] in self.frontmost },
                deliver: { [unowned self] text, _, paste, authorization in
                    guard authorization.isEligible else { return .failed }
                    self.deliveries.append((text, paste))
                    return paste ? self.deliveryResult : .copiedOnly
                }))
            runner.onEvent = { [unowned self] in self.events.append($0) }
            return runner
        }
    }

    /// A provider that suspends until the test releases it.
    private final class Gate {
        private var continuation: CheckedContinuation<String, Error>?
        private(set) var isWaiting = false
        func wait() async throws -> String {
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { continuation in
                    self.continuation = continuation
                    isWaiting = true
                }
            } onCancel: {
                Task { @MainActor in self.continuation?.resume(throwing: CancellationError()); self.continuation = nil }
            }
        }
        func release(_ value: String) { continuation?.resume(returning: value); continuation = nil }
    }

    private func waitUntil(_ condition: @escaping () -> Bool) async {
        for _ in 0..<500 where !condition() { await Task.yield() }
        XCTAssertTrue(condition())
    }

    func testSuccessInsertsFinalizedOutputWithOneProviderRequest() async {
        let harness = Harness(pid: pid)
        let runner = harness.runner()
        let outcome = await runner.run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome, .inserted)
        XCTAssertEqual(harness.requests.count, 1)
        XCTAssertEqual(harness.requests.first?.user, "<selected_text>rough text</selected_text>")
        XCTAssertEqual(harness.deliveries.map(\.text), ["  Clearer text.\n"])
        XCTAssertEqual(harness.deliveries.map(\.paste), [true])
        XCTAssertEqual(harness.events, [.processing(name: "Polish", processIdentifier: pid), .finished(.inserted)])
        XCTAssertEqual(runner.phase, .idle)
    }

    func testNoSelectionIsANoOp() async {
        let harness = Harness(pid: pid)
        harness.selection = .rejected(.noSelection)
        let outcome = await harness.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome, .notStarted(.noSelection))
        XCTAssertTrue(harness.requests.isEmpty)
        XCTAssertTrue(harness.deliveries.isEmpty)
    }

    func testWhitespaceAndOversizedSelectionsNeverReachTheProvider() async {
        let harness = Harness(pid: pid)
        harness.selection = .selected(.init(text: " \n\t", processIdentifier: pid, target: .unavailable))
        let outcome1 = await harness.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome1, .notStarted(.noSelection))
        let long = Array(repeating: "w", count: 3_001).joined(separator: " ")
        harness.selection = .selected(.init(text: long, processIdentifier: pid, target: .unavailable))
        let outcome2 = await harness.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome2,
                       .notStarted(.selectionTooLong(words: 3_001)))
        XCTAssertTrue(harness.requests.isEmpty)
    }

    func testProtectedFieldAndUnavailableProviderStopBeforeReading() async {
        let harness = Harness(pid: pid)
        harness.selection = .rejected(.protectedField)
        let outcome3 = await harness.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome3, .notStarted(.protectedField))
        harness.route = .unavailable(.cleanupOff)
        let outcome4 = await harness.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome4, .notStarted(.cleanupOff))
        XCTAssertEqual(harness.reads, 1, "An unusable provider is reported before any selection is read")
        let outcome5 = await harness.runner().run(.init(name: "Empty", prompt: " \n"), dictationBusy: false)
        XCTAssertEqual(outcome5,
                       .notStarted(.emptyPrompt))
        XCTAssertTrue(harness.deliveries.isEmpty)
    }

    func testProviderErrorEmptyAndRefusalNeverPaste() async {
        let failing = Harness(pid: pid) { _ in throw URLError(.timedOut) }
        let outcome6 = await failing.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome6, .failed(.providerFailed(.timeout)))
        let empty = Harness(pid: pid) { _ in "  " }
        let outcome7 = await empty.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome7, .failed(.emptyResult))
        let refusing = Harness(pid: pid) { _ in "I'm sorry, but I can't help with that." }
        let outcome8 = await refusing.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome8, .failed(.refusal))
        XCTAssertTrue(failing.deliveries.isEmpty && empty.deliveries.isEmpty && refusing.deliveries.isEmpty)
    }

    func testFocusMovedToAnotherAppCopiesWithoutPasting() async {
        let harness = Harness(pid: pid)
        harness.frontmost = pid + 1
        let outcome = await harness.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome, .copied(.focusChanged))
        XCTAssertEqual(harness.deliveries.map(\.paste), [false])
    }

    func testChangedSelectionInSameAppCopiesWithoutPasting() async {
        let harness = Harness(pid: pid)
        harness.selectionMatches = false
        let outcome9 = await harness.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome9, .copied(.focusChanged))
        XCTAssertEqual(harness.deliveries.map(\.paste), [false])
    }

    func testDestinationRefusingPasteIsReportedAsCopied() async {
        let harness = Harness(pid: pid)
        harness.deliveryResult = .copiedOnly
        let outcome10 = await harness.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome10, .copied(nil))
        harness.deliveryResult = .failed
        let outcome11 = await harness.runner().run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome11, .failed(.deliveryFailed))
    }

    func testCancelDuringProcessingNeverDelivers() async {
        let gate = Gate()
        let harness = Harness(pid: pid) { _ in try await gate.wait() }
        let runner = harness.runner()
        let task = Task { await runner.run(instruction, dictationBusy: false) }
        await waitUntil { gate.isWaiting }
        XCTAssertEqual(runner.phase, .processing)
        XCTAssertTrue(runner.canCancel)
        runner.cancel()
        XCTAssertEqual(runner.phase, .idle)
        gate.release("Too late.")
        let outcome = await task.value
        XCTAssertEqual(outcome, .cancelled)
        XCTAssertTrue(harness.deliveries.isEmpty)
        XCTAssertEqual(harness.events.last, .finished(.cancelled))
    }

    func testLateProviderResultAfterCancelCannotPaste() async {
        final class Flag { var isSet = false }
        let release = Flag()
        let harness = Harness(pid: pid) { _ in
            // Ignores task cancellation, like a provider that finishes anyway.
            while !release.isSet { await Task.yield() }
            return "Late result."
        }
        let runner = harness.runner()
        let task = Task { await runner.run(instruction, dictationBusy: false) }
        await waitUntil { runner.phase == .processing }
        runner.cancel()
        release.isSet = true
        let outcome12 = await task.value
        XCTAssertEqual(outcome12, .cancelled)
        XCTAssertEqual(harness.requests.count, 1)
        XCTAssertTrue(harness.deliveries.isEmpty)
    }

    func testMutualExclusionWithDictationAndConcurrentTransforms() async {
        let harness = Harness(pid: pid)
        let outcome13 = await harness.runner().run(instruction, dictationBusy: true)
        XCTAssertEqual(outcome13, .notStarted(.dictationBusy))
        XCTAssertEqual(harness.reads, 0)

        let gate = Gate()
        let slow = Harness(pid: pid) { _ in try await gate.wait() }
        let runner = slow.runner()
        let first = Task { await runner.run(instruction, dictationBusy: false) }
        await waitUntil { gate.isWaiting }
        let outcome14 = await runner.run(instruction, dictationBusy: false)
        XCTAssertEqual(outcome14, .notStarted(.alreadyRunning))

        // AppState refuses to start dictation while a transform is busy.
        let app = AppState(permissions: Permissions(statusProvider: { (true, true) }), transformRunner: runner)
        XCTAssertTrue(app.transformRunner.isBusy)
        await app.startDictation(mode: .handsFreeToggle)
        XCTAssertEqual(app.status, .idle)
        XCTAssertFalse(app.isDictationBusy)

        gate.release("Done.")
        let outcome15 = await first.value
        XCTAssertEqual(outcome15, .inserted)
        XCTAssertFalse(runner.isBusy)
    }

    func testAppStateRoutesCancelAvailabilityToTheRunner() async {
        let gate = Gate()
        let harness = Harness(pid: pid) { _ in try await gate.wait() }
        let runner = harness.runner()
        let app = AppState(permissions: Permissions(statusProvider: { (true, true) }), transformRunner: runner)
        XCTAssertFalse(app.hotkeyService.isCancellationEnabled)
        let task = Task { await runner.run(instruction, dictationBusy: app.isDictationBusy) }
        await waitUntil { gate.isWaiting }
        XCTAssertTrue(app.hotkeyService.isCancellationEnabled, "Esc is armed while a transform runs")
        runner.cancel()
        XCTAssertFalse(app.hotkeyService.isCancellationEnabled)
        let outcome16 = await task.value
        XCTAssertEqual(outcome16, .cancelled)
    }
}
