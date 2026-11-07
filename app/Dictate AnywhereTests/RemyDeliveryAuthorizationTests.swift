import XCTest
import AppKit
@testable import Dictate_Anywhere

final class RemyDeliveryAuthorizationTests: XCTestCase {
    func testFocusMutationNeedsNoClipboardClaimAndCannotRunAfterRevocationOrPaste() {
        let lease = RemyDeliveryAuthorization()
        var effects = 0
        XCTAssertTrue(lease.performFocusMutation { effects += 1; return true })
        XCTAssertEqual(lease.snapshot, .init(copied: false, pasteClaimed: false))
        lease.revoke()
        XCTAssertFalse(lease.performFocusMutation { effects += 1; return true })
        XCTAssertEqual(effects, 1)
        let dispatched = RemyDeliveryAuthorization()
        XCTAssertTrue(dispatched.consumeClipboard())
        XCTAssertTrue(dispatched.performClipboardMutation { true })
        XCTAssertTrue(dispatched.consumePaste())
        XCTAssertFalse(dispatched.performFocusMutation { XCTFail("No focus changes after dispatch"); return true })
    }

    func testFocusMutationSerializesWithRevocation() async {
        let lease = RemyDeliveryAuthorization()
        let entered = expectation(description: "focus effect entered")
        let finished = expectation(description: "focus and revoke complete")
        let release = DispatchSemaphore(value: 0)
        DispatchQueue(label: "remy.focus.effect.test").async {
            XCTAssertTrue(lease.performFocusMutation { entered.fulfill(); release.wait(); return true })
        }
        await fulfillment(of: [entered], timeout: 2)
        DispatchQueue(label: "remy.focus.revoke.test").async {
            XCTAssertEqual(lease.revoke(), .init(copied: false, pasteClaimed: false))
            finished.fulfill()
        }
        release.signal()
        await fulfillment(of: [finished], timeout: 2)
        XCTAssertFalse(lease.performFocusMutation { XCTFail("Late focus effect"); return true })
    }

    func testRevokedQueuedWorkerCannotClaimAnyEffect() async {
        let lease = RemyDeliveryAuthorization()
        let release = DispatchSemaphore(value: 0)
        let finished = expectation(description: "queued worker completed")
        DispatchQueue(label: "remy.delivery.test").async {
            release.wait()
            XCTAssertFalse(lease.consumeClipboard())
            XCTAssertFalse(lease.consumePaste())
            finished.fulfill()
        }
        XCTAssertEqual(lease.revoke(), .init(copied: false, pasteClaimed: false))
        release.signal()
        await fulfillment(of: [finished], timeout: 2)
    }

    func testClipboardMutationAndSuccessAreRecordedBeforeRevocation() async {
        let lease = RemyDeliveryAuthorization()
        XCTAssertTrue(lease.consumeClipboard())
        let mutationEntered = expectation(description: "mutation entered")
        let releaseMutation = DispatchSemaphore(value: 0)
        let revoked = expectation(description: "revocation classified completed write")
        DispatchQueue(label: "remy.clipboard.test").async {
            XCTAssertTrue(lease.performClipboardMutation {
                mutationEntered.fulfill()
                releaseMutation.wait()
                return true // Deliberate fake bounded side effect, no pasteboard.
            })
        }
        await fulfillment(of: [mutationEntered], timeout: 2)
        DispatchQueue(label: "remy.revoke.test").async {
            XCTAssertEqual(lease.revoke(), .init(copied: true, pasteClaimed: false))
            revoked.fulfill()
        }
        releaseMutation.signal()
        await fulfillment(of: [revoked], timeout: 2)
        XCTAssertFalse(lease.performClipboardMutation { XCTFail("Late write must not run"); return true })
    }

    func testPasteRequiresSuccessfulCopyAndCanOnlyBeClaimedOnceAcrossWorkers() {
        let lease = RemyDeliveryAuthorization()
        XCTAssertFalse(lease.consumePaste())
        XCTAssertTrue(lease.consumeClipboard())
        XCTAssertFalse(lease.consumeClipboard())
        XCTAssertFalse(lease.consumePaste(), "Authorization alone does not mean clipboard write succeeded")
        XCTAssertTrue(lease.performClipboardMutation { true })
        let count = LockedClaimCount()
        DispatchQueue.concurrentPerform(iterations: 64) { _ in
            if lease.consumePaste() { count.increment() }
        }
        XCTAssertEqual(count.value, 1)
        XCTAssertEqual(lease.revoke(), .init(copied: true, pasteClaimed: true))
        XCTAssertFalse(lease.consumePaste())
    }

    @MainActor
    func testClaimedScriptFailureNeverFallsBackToKeyboardPaste() async {
        let lease = RemyDeliveryAuthorization()
        XCTAssertTrue(lease.consumeClipboard())
        XCTAssertTrue(lease.performClipboardMutation { true })
        var keyboardAttempts = 0
        let result = await TextInserter.deliverToTarget(isTargetReady: { true }, authorization: lease,
            pasteScript: {
                XCTAssertTrue(lease.consumePaste())
                return .automationDenied // Conservative: worker already claimed possible dispatch.
            }, pasteEvent: { keyboardAttempts += 1; return true })
        XCTAssertEqual(result, .possiblyDispatched)
        XCTAssertEqual(keyboardAttempts, 0)
    }

    @MainActor
    func testRevokeDuringQueuedPasteStopsWorkerAndFallback() async {
        let lease = RemyDeliveryAuthorization()
        XCTAssertTrue(lease.consumeClipboard())
        XCTAssertTrue(lease.performClipboardMutation { true })
        let queued = expectation(description: "paste queued")
        var resumeWorker: CheckedContinuation<Void, Never>?
        var keyboardAttempts = 0
        let task = Task { @MainActor in
            await TextInserter.deliverToTarget(isTargetReady: { true }, authorization: lease,
                pasteScript: {
                    await withCheckedContinuation { continuation in
                        resumeWorker = continuation
                        queued.fulfill()
                    }
                    XCTAssertFalse(lease.consumePaste())
                    return .targetUnavailable
                }, pasteEvent: { keyboardAttempts += 1; return true })
        }
        await fulfillment(of: [queued], timeout: 2)
        XCTAssertEqual(lease.revoke(), .init(copied: true, pasteClaimed: false))
        resumeWorker?.resume()
        let result = await task.value
        XCTAssertEqual(result, .copiedOnly)
        XCTAssertEqual(keyboardAttempts, 0)
    }

    @MainActor
    func testRevocationDuringListVerificationPreventsLateClipboardRewrite() async {
        let board = NSPasteboard(name: .init("remy-delivery-test-\(UUID())"))
        defer { board.releaseGlobally() }
        let lease = RemyDeliveryAuthorization()
        XCTAssertTrue(lease.consumeClipboard())
        XCTAssertTrue(lease.performClipboardMutation {
            board.clearContents()
            return board.setString("deliberate replacement", forType: .string)
        })
        let count = board.changeCount
        let delivered = await TextInserter.finalizeListPaste(delivery: .success,
            insertionText: "deliberate original", replacementText: "deliberate replacement",
            clipboardChangeCount: count, pasteboard: board, authorization: lease,
            finishEdit: { lease.revoke(); return true }, restoreSelection: { XCTFail("No rollback after success") })
        XCTAssertTrue(delivered)
        XCTAssertEqual(board.changeCount, count)
        XCTAssertEqual(board.string(forType: .string), "deliberate replacement")
    }
}

private nonisolated final class LockedClaimCount: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    var value: Int { lock.withLock { count } }
    func increment() { lock.withLock { count += 1 } }
}
