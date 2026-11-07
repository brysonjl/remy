import Foundation
import XCTest
@testable import Dictate_Anywhere

@MainActor final class RemyDictationDestinationTests: XCTestCase {
    private func destination(_ pid: pid_t = 123, allowed: Bool = true) -> RemyDestinationRouter.Destination {
        .init(processIdentifier: pid, target: RemyInsertionTarget(terminal: false, check: { $0 == pid }), allowsPaste: allowed)
    }
    func testStoredPreferenceDefaultsAndCodableChoices() throws {
        XCTAssertEqual(RemyDictationDestination.decodeStored(nil), .originalField)
        XCTAssertEqual(RemyDictationDestination.decodeStored("unknown-future-value"), .originalField)
        XCTAssertEqual(RemyDictationDestination.decodeStored(""), .originalField)
        for value in RemyDictationDestination.allCases {
            XCTAssertEqual(RemyDictationDestination.decodeStored(value.rawValue), value)
            XCTAssertEqual(try JSONDecoder().decode(RemyDictationDestination.self,
                from: JSONEncoder().encode(value)), value)
            XCTAssertFalse(value.displayName.isEmpty)
            XCTAssertFalse(value.caption.isEmpty)
        }
    }
    func testOriginalRestoresOnlyOriginalAndNeverCapturesEndField() async {
        let original = destination()
        var restored = 0
        var captured = 0
        let resolved = await RemyDestinationRouter.resolve(policy: .originalField, original: original,
            authorization: RemyDeliveryAuthorization(), captureCurrent: {
                captured += 1; return self.destination(456)
            }, restoreOriginal: { restored += 1; return true })
        XCTAssertEqual(restored, 1)
        XCTAssertEqual(captured, 0)
        XCTAssertEqual(resolved.processIdentifier, original.processIdentifier)
        XCTAssertTrue(resolved.target === original.target)
        XCTAssertTrue(resolved.allowsPaste)
    }
    func testFailedOriginalDoesNotFallBackToNewFieldAndRetainsOriginalIdentity() async {
        for originallyAllowed in [false, true] {
            let original = destination(123, allowed: originallyAllowed)
            var restored = 0
            let resolved = await RemyDestinationRouter.resolve(policy: .originalField, original: original,
                authorization: RemyDeliveryAuthorization(), captureCurrent: {
                    XCTFail("Original policy must never capture another field")
                    return self.destination(456)
                }, restoreOriginal: { restored += 1; return false })
            XCTAssertEqual(restored, originallyAllowed ? 1 : 0)
            XCTAssertEqual(resolved.processIdentifier, original.processIdentifier)
            XCTAssertTrue(resolved.target === original.target)
            XCTAssertFalse(resolved.allowsPaste)
        }
    }
    func testCurrentCapturesOnceAfterSuspensionAndNeverRestoresOriginal() async {
        let gate = DestinationResolutionGate()
        let original = destination(123)
        let end = destination(456)
        var selectedPolicy = RemyDictationDestination.currentField
        var captures = 0
        let task = Task {
            await RemyDestinationRouter.resolve(policy: selectedPolicy, original: original,
                authorization: RemyDeliveryAuthorization(), captureCurrent: {
                    captures += 1
                    await gate.wait()
                    return end
                }, restoreOriginal: { XCTFail("Current policy must not restore original"); return true })
        }
        await gate.waitUntilEntered()
        selectedPolicy = .originalField // Preference changes cannot reroute this in-flight selection.
        gate.release()
        let resolved = await task.value
        XCTAssertEqual(captures, 1)
        XCTAssertEqual(resolved.processIdentifier, end.processIdentifier)
        XCTAssertTrue(resolved.target === end.target)
        XCTAssertTrue(resolved.allowsPaste)
    }
    func testRevokedBeforeResolutionPerformsNoCaptureOrRestoration() async {
        for policy in RemyDictationDestination.allCases {
            let lease = RemyDeliveryAuthorization()
            lease.revoke()
            let resolved = await RemyDestinationRouter.resolve(policy: policy, original: destination(), authorization: lease,
                captureCurrent: { XCTFail("Revoked capture"); return self.destination() },
                restoreOriginal: { XCTFail("Revoked restoration"); return true })
            XCTAssertNil(resolved.processIdentifier)
            XCTAssertFalse(resolved.allowsPaste)
        }
    }
    func testRevocationDuringEitherAwaitDiscardsResolvedAuthority() async {
        for policy in RemyDictationDestination.allCases {
            let lease = RemyDeliveryAuthorization()
            let gate = DestinationResolutionGate()
            let result = Task {
                await RemyDestinationRouter.resolve(policy: policy, original: self.destination(), authorization: lease,
                    captureCurrent: { await gate.wait(); return self.destination(456) },
                    restoreOriginal: { await gate.wait(); return true })
            }
            await gate.waitUntilEntered()
            lease.revoke()
            gate.release()
            let resolved = await result.value
            XCTAssertNil(resolved.processIdentifier)
            XCTAssertFalse(resolved.allowsPaste)
        }
    }
    func testUnavailableCurrentNeverFallsBackToOriginal() async {
        let original = destination()
        let resolved = await RemyDestinationRouter.resolve(policy: .currentField, original: original,
            authorization: RemyDeliveryAuthorization(), captureCurrent: { .unavailable },
            restoreOriginal: { XCTFail("Must not restore original"); return true })
        XCTAssertNil(resolved.processIdentifier)
        XCTAssertFalse(resolved.allowsPaste)
        XCTAssertFalse(resolved.target === original.target)
    }
    func testTaskCancellationAndAlreadyDispatchedLeaseCannotResolveFocus() async {
        let gate = DestinationResolutionGate()
        let lease = RemyDeliveryAuthorization()
        let task = Task {
            await RemyDestinationRouter.resolve(policy: .currentField, original: self.destination(), authorization: lease,
                captureCurrent: { await gate.wait(); return self.destination(456) },
                restoreOriginal: { XCTFail("Must not restore"); return true })
        }
        await gate.waitUntilEntered()
        task.cancel()
        gate.release()
        let cancelled = await task.value
        XCTAssertFalse(cancelled.allowsPaste)
        XCTAssertNil(cancelled.processIdentifier)
        XCTAssertTrue(lease.consumeClipboard())
        lease.recordCopied()
        XCTAssertTrue(lease.consumePaste())
        let dispatched = await RemyDestinationRouter.resolve(policy: .originalField, original: destination(), authorization: lease,
            captureCurrent: { XCTFail("Already dispatched"); return self.destination() },
            restoreOriginal: { XCTFail("Already dispatched"); return true })
        XCTAssertFalse(dispatched.allowsPaste)
    }
}

@MainActor private final class DestinationResolutionGate {
    private var entered = false
    private var waiting: CheckedContinuation<Void, Never>?
    private var observers: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        entered = true
        let pending = observers; observers.removeAll()
        pending.forEach { $0.resume() }
        await withCheckedContinuation { waiting = $0 }
    }
    func waitUntilEntered() async {
        if entered { return }
        await withCheckedContinuation { observers.append($0) }
    }
    func release() { waiting?.resume(); waiting = nil }
}
