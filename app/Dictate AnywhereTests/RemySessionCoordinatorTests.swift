import XCTest
@testable import Dictate_Anywhere

@MainActor
final class RemySessionCoordinatorTests: XCTestCase {
    private struct Configuration: Equatable {
        var model = "pinned-v2"
        var cleanup = "off"
    }
    private typealias Coordinator = RemySessionCoordinator<Configuration>

    private func ready(_ coordinator: Coordinator) throws -> Coordinator.Token {
        let token = try XCTUnwrap(coordinator.begin(configuration: Configuration()))
        XCTAssertTrue(coordinator.markRecording(token))
        XCTAssertTrue(coordinator.requestStop(token))
        XCTAssertTrue(coordinator.recordFinalRaw(token, text: "original words"))
        XCTAssertTrue(coordinator.markReady(token))
        return token
    }

    func testDuplicateEventsAndRawAreIdempotentlyRejected() throws {
        let coordinator = Coordinator()
        let token = try XCTUnwrap(coordinator.begin(configuration: Configuration()))
        XCTAssertNil(coordinator.begin(configuration: Configuration()))
        XCTAssertFalse(coordinator.requestStop(token))
        XCTAssertTrue(coordinator.markRecording(token))
        XCTAssertFalse(coordinator.markRecording(token))
        XCTAssertTrue(coordinator.requestStop(token))
        XCTAssertFalse(coordinator.requestStop(token))
        XCTAssertTrue(coordinator.recordFinalRaw(token, text: "immutable raw"))
        XCTAssertFalse(coordinator.recordFinalRaw(token, text: "replacement"))
        XCTAssertEqual(coordinator.finalRaw(for: token), "immutable raw")
        XCTAssertEqual(coordinator.eventSequence, 4)
        XCTAssertTrue(coordinator.beginCleanup(token))
        XCTAssertFalse(coordinator.beginCleanup(token))
        XCTAssertTrue(coordinator.markReady(token))
        XCTAssertFalse(coordinator.markReady(token))
        XCTAssertEqual(coordinator.eventSequence, 6)
    }

    func testConfigurationIsAValueSnapshotDuringCapture() throws {
        let coordinator = Coordinator()
        var settings = Configuration()
        let token = try XCTUnwrap(coordinator.begin(configuration: settings))
        coordinator.markRecording(token)
        settings.model = "changed-model"
        settings.cleanup = "changed-provider"
        XCTAssertEqual(coordinator.configuration(for: token), Configuration())
    }

    func testCancellationInEveryPreDeliveryPhaseRevokesAuthority() throws {
        for phase in 0...5 {
            let coordinator = Coordinator()
            let token = try XCTUnwrap(coordinator.begin(configuration: Configuration()))
            if phase >= 1 { coordinator.markRecording(token) }
            if phase >= 2 { coordinator.requestStop(token); coordinator.recordFinalRaw(token, text: "raw") }
            if phase >= 3 { coordinator.beginCleanup(token) }
            if phase >= 4 { coordinator.markReady(token) }
            let permit = phase == 5 ? coordinator.authorizeDelivery(token) : nil
            XCTAssertEqual(coordinator.cancel(token), .cancelled)
            XCTAssertEqual(coordinator.state, .cancelled)
            XCTAssertFalse(coordinator.isCurrent(token))
            XCTAssertNil(coordinator.authorizeDelivery(token))
            XCTAssertFalse(coordinator.markReady(token))
            XCTAssertFalse(coordinator.complete(token))
            XCTAssertEqual(coordinator.cancel(token), .ignored)
            if let permit { XCTAssertFalse(coordinator.consumeClipboardPermit(permit)) }
        }
    }

    func testClipboardCancellationBlocksPasteWithoutClaimingRetraction() throws {
        let coordinator = Coordinator()
        let token = try ready(coordinator)
        let permit = try XCTUnwrap(coordinator.authorizeDelivery(token))
        XCTAssertFalse(coordinator.consumePastePermit(permit))
        XCTAssertTrue(coordinator.consumeClipboardPermit(permit))
        permit.authorization.recordCopied()
        XCTAssertFalse(coordinator.consumeClipboardPermit(permit))
        XCTAssertEqual(coordinator.cancel(token), .clipboardAlreadyWritten)
        XCTAssertFalse(coordinator.consumePastePermit(permit))
        XCTAssertEqual(coordinator.state, .cancelled)
    }

    func testAfterDispatchCancellationReportsTooLateAndCompletesOnce() throws {
        let coordinator = Coordinator()
        let token = try ready(coordinator)
        let permit = try XCTUnwrap(coordinator.authorizeDelivery(token))
        XCTAssertNil(coordinator.authorizeDelivery(token))
        XCTAssertTrue(coordinator.consumeClipboardPermit(permit))
        permit.authorization.recordCopied()
        XCTAssertTrue(coordinator.consumePastePermit(permit))
        XCTAssertFalse(coordinator.consumePastePermit(permit))
        XCTAssertEqual(coordinator.cancel(token), .alreadyDispatched)
        XCTAssertTrue(coordinator.isCurrent(token))
        XCTAssertEqual(coordinator.state, .delivering)
        XCTAssertTrue(coordinator.complete(token))
        XCTAssertFalse(coordinator.complete(token))
        XCTAssertFalse(coordinator.consumePastePermit(permit))
    }

    func testOldSessionAndProviderCompletionCannotAffectNextSession() throws {
        let coordinator = Coordinator()
        let first = try ready(coordinator)
        let oldPermit = try XCTUnwrap(coordinator.authorizeDelivery(first))
        // A retained fake provider callback ignores task cancellation.
        let delayedCompletion = { coordinator.complete(first) }
        coordinator.cancel(first)
        let second = try XCTUnwrap(coordinator.begin(configuration: Configuration(cleanup: "new-provider")))
        let sequence = coordinator.eventSequence
        XCTAssertGreaterThan(second.generation, first.generation)
        XCTAssertNotEqual(second.id, first.id)
        XCTAssertFalse(delayedCompletion())
        XCTAssertFalse(coordinator.recordFinalRaw(first, text: "late final"))
        XCTAssertFalse(coordinator.consumeClipboardPermit(oldPermit))
        XCTAssertNil(coordinator.configuration(for: first))
        XCTAssertEqual(coordinator.state, .preparing)
        XCTAssertEqual(coordinator.eventSequence, sequence)
        XCTAssertEqual(coordinator.configuration(for: second)?.cleanup, "new-provider")
    }

    func testTimeoutThenLateSuccessCannotAcquireDeliveryAuthority() throws {
        let coordinator = Coordinator()
        let token = try XCTUnwrap(coordinator.begin(configuration: Configuration()))
        coordinator.markRecording(token)
        coordinator.requestStop(token)
        coordinator.recordFinalRaw(token, text: "raw before provider")
        coordinator.beginCleanup(token)
        let lateProvider = { coordinator.markReady(token) }
        let timeout = Coordinator.Failure(stage: .cleanup, category: .timeout, retryable: true)
        XCTAssertTrue(coordinator.fail(token, failure: timeout))
        XCTAssertEqual(coordinator.failure, timeout)
        XCTAssertFalse(lateProvider())
        XCTAssertNil(coordinator.authorizeDelivery(token))
        XCTAssertFalse(coordinator.complete(token))
        XCTAssertFalse(coordinator.fail(token))
    }

    func testUnownedHistoryWorkCannotRequestForegroundDelivery() throws {
        let coordinator = Coordinator()
        let unrelated = Coordinator.Token(id: UUID(), generation: 1)
        XCTAssertNil(coordinator.authorizeDelivery(unrelated))
        let foreground = try ready(coordinator)
        XCTAssertNil(coordinator.authorizeDelivery(unrelated))
        XCTAssertTrue(coordinator.isCurrent(foreground))
        XCTAssertEqual(coordinator.state, .ready)
    }

    func testEmptyRawAndCopyOnlyCompletionRequireNoPaste() throws {
        let coordinator = Coordinator()
        let empty = try XCTUnwrap(coordinator.begin(configuration: Configuration()))
        coordinator.markRecording(empty)
        coordinator.requestStop(empty)
        XCTAssertFalse(coordinator.markReady(empty))
        coordinator.recordFinalRaw(empty, text: "")
        coordinator.markReady(empty)
        XCTAssertTrue(coordinator.complete(empty))
        let copy = try ready(coordinator)
        let permit = try XCTUnwrap(coordinator.authorizeDelivery(copy))
        XCTAssertTrue(coordinator.consumeClipboardPermit(permit))
        permit.authorization.recordCopied()
        XCTAssertTrue(coordinator.complete(copy))
        XCTAssertFalse(coordinator.consumePastePermit(permit))
    }

    func testQueuedWorkerLeaseIsRevokedBeforeFirstEffect() throws {
        let coordinator = Coordinator()
        let token = try ready(coordinator)
        let permit = try XCTUnwrap(coordinator.authorizeDelivery(token))
        let queuedClipboardWrite = { permit.authorization.consumeClipboard() }
        XCTAssertEqual(coordinator.cancel(token), .cancelled)
        XCTAssertFalse(queuedClipboardWrite())
        XCTAssertFalse(permit.authorization.consumePaste())
    }

    func testWorkerClaimsAreVisibleToCoordinatorCancellation() throws {
        let coordinator = Coordinator()
        let token = try ready(coordinator)
        let permit = try XCTUnwrap(coordinator.authorizeDelivery(token))
        // Simulate the output worker using the shared lease directly.
        XCTAssertTrue(permit.authorization.consumeClipboard())
        permit.authorization.recordCopied()
        XCTAssertTrue(permit.authorization.consumePaste())
        XCTAssertEqual(coordinator.cancel(token), .alreadyDispatched)
        XCTAssertTrue(coordinator.complete(token))
    }

    func testClipboardClaimWithoutSuccessfulWriteDoesNotClaimCopied() throws {
        let coordinator = Coordinator()
        let token = try ready(coordinator)
        let permit = try XCTUnwrap(coordinator.authorizeDelivery(token))
        XCTAssertTrue(permit.authorization.consumeClipboard())
        XCTAssertEqual(coordinator.cancel(token), .cancelled)
        XCTAssertFalse(permit.authorization.consumePaste())
    }

    func testRevocationReasonIsTypedAndClearsForNextSession() throws {
        let coordinator = Coordinator()
        let token = try XCTUnwrap(coordinator.begin(configuration: Configuration()))
        XCTAssertEqual(coordinator.revoke(token, reason: .shutdown), .cancelled)
        XCTAssertEqual(coordinator.cancellationReason, .shutdown)
        XCTAssertNotNil(coordinator.begin(configuration: Configuration()))
        XCTAssertNil(coordinator.cancellationReason)
        XCTAssertNil(coordinator.failure)
    }
}
