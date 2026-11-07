import XCTest
import ApplicationServices
@testable import Dictate_Anywhere

final class RemyInsertionTargetTests: XCTestCase {
    @MainActor
    func testUnavailableAndLegacyHaveNoRestorationAuthority() async {
        let lease = RemyDeliveryAuthorization()
        let unavailable = await RemyInsertionTarget.unavailable.restoreFocus(authorization: lease)
        let legacy = await RemyInsertionTarget.legacyUnchecked.restoreFocus(authorization: lease)
        XCTAssertFalse(unavailable)
        XCTAssertFalse(legacy)
        XCTAssertEqual(lease.snapshot, .init(copied: false, pasteClaimed: false))
    }

    @MainActor
    func testRevokedRestorationDoesNotInvokeEffectAndQueuedRestorationRechecksLease() async {
        let effects = FakeFocusEffects()
        let revoked = RemyDeliveryAuthorization()
        revoked.revoke()
        let target = RemyInsertionTarget(terminal: false, check: { _ in true }, restoration: { lease in
            lease.performFocusMutation { effects.increment(); return true }
        })
        let result = await target.restoreFocus(authorization: revoked)
        XCTAssertFalse(result)
        XCTAssertEqual(effects.count, 0)

        let entered = expectation(description: "restore awaiting worker")
        var release: CheckedContinuation<Void, Never>?
        let queued = RemyInsertionTarget(terminal: false, check: { _ in true }, restoration: { lease in
            await withCheckedContinuation { release = $0; entered.fulfill() }
            return lease.performFocusMutation { effects.increment(); return true }
        })
        let lease = RemyDeliveryAuthorization()
        let request = Task { await queued.restoreFocus(authorization: lease) }
        await fulfillment(of: [entered], timeout: 2)
        lease.revoke()
        release?.resume()
        let lateResult = await request.value
        XCTAssertFalse(lateResult)
        XCTAssertEqual(effects.count, 0)
    }

    func testUnavailableProofFailsClosedAndLegacyIsExplicit() {
        XCTAssertFalse(RemyInsertionTarget.unavailable.permitsPaste(processIdentifier: 123, payload: "hello"))
        XCTAssertTrue(RemyInsertionTarget.legacyUnchecked.permitsPaste(processIdentifier: 123, payload: "hello"))
    }

    func testTerminalRejectsEveryNewlineIncludingTrailingUnicodeWithoutChangingPayload() {
        let terminal = RemyInsertionTarget(terminal: true, check: { $0 == 123 })
        for newline in ["\n", "\r", "\r\n", "\u{0085}", "\u{2028}", "\u{2029}"] {
            XCTAssertFalse(terminal.permitsPaste(processIdentifier: 123, payload: "unexecuted" + newline))
        }
        XCTAssertTrue(terminal.permitsPaste(processIdentifier: 123, payload: "Raw terminal check seven eight nine"))
        XCTAssertFalse(terminal.permitsPaste(processIdentifier: 124, payload: "single line"))
        let editor = RemyInsertionTarget(terminal: false, check: { _ in true })
        XCTAssertTrue(editor.permitsPaste(processIdentifier: 123, payload: "two\nlines"))
    }

    func testQueuedRevalidationRejectsChangedFieldBeforePasteClaim() async {
        let focus = FakeFocus()
        let target = RemyInsertionTarget(terminal: false, check: { _ in focus.matches })
        XCTAssertTrue(target.permitsPaste(processIdentifier: 123, payload: "payload"))
        let lease = RemyDeliveryAuthorization()
        XCTAssertTrue(lease.consumeClipboard())
        XCTAssertTrue(lease.performClipboardMutation { true })
        focus.matches = false
        let claimed = await target.performOnWorker {
            target.permitsPaste(processIdentifier: 123, payload: "payload") && lease.consumePaste()
        }
        XCTAssertFalse(claimed)
        XCTAssertEqual(lease.snapshot, .init(copied: true, pasteClaimed: false))
    }

    func testClipboardGenerationAndContentMustBothMatchBeforeWorkerClaim() async {
        let board = FakeClipboard()
        let ownership = RemyClipboardOwnership(generation: 7, payload: "exact payload", read: { board.state })
        XCTAssertTrue(ownership.matches())
        let lease = RemyDeliveryAuthorization()
        XCTAssertTrue(lease.consumeClipboard())
        XCTAssertTrue(lease.performClipboardMutation { true })
        // Identical text copied externally still invalidates our generation.
        board.state = .init(generation: 8, text: "exact payload", stable: true)
        let claimed = await RemyInsertionTarget.legacyUnchecked.performOnWorker {
            ownership.matches() && lease.consumePaste()
        }
        XCTAssertFalse(claimed)
        XCTAssertFalse(lease.snapshot.pasteClaimed)
        board.state = .init(generation: 7, text: "external bytes", stable: true)
        XCTAssertFalse(ownership.matches())
        board.state = .init(generation: 7, text: "exact payload", stable: false)
        XCTAssertFalse(ownership.matches())
        let unicode = RemyClipboardOwnership(generation: 7, payload: "e\u{301}",
            read: { .init(generation: 7, text: "é", stable: true) })
        XCTAssertFalse(unicode.matches(), "Canonical Unicode equivalence does not prove byte-identical clipboard payload")
    }

    func testOptionalAXFactsAllowUnsupportedButRejectFailuresAndMalformedValues() {
        for status: AXError in [.attributeUnsupported, .noValue] {
            XCTAssertTrue(RemyInsertionTarget.acceptsOptionalAttribute(status: status, valuePresent: false, typeMatches: false))
        }
        for status: AXError in [.cannotComplete, .invalidUIElement, .failure, .notImplemented] {
            XCTAssertFalse(RemyInsertionTarget.acceptsOptionalAttribute(status: status, valuePresent: true, typeMatches: true))
        }
        XCTAssertFalse(RemyInsertionTarget.acceptsOptionalAttribute(status: .success, valuePresent: false, typeMatches: false))
        XCTAssertFalse(RemyInsertionTarget.acceptsOptionalAttribute(status: .success, valuePresent: true, typeMatches: false))
        XCTAssertTrue(RemyInsertionTarget.acceptsOptionalAttribute(status: .success, valuePresent: true, typeMatches: true))
    }

    func testSupportedEditableRolePolicyRejectsButtonsUnknownReadonlyDisabledAndProtected() {
        typealias Facts = RemyInsertionTarget.Editability
        for role in ["AXButton", "AXGroup", "AXWebArea", "AXStaticText", "AXUnknown"] {
            XCTAssertFalse(RemyInsertionTarget.supports(Facts(role: role, enabled: true, editable: true), terminal: false))
        }
        XCTAssertFalse(RemyInsertionTarget.supports(Facts(role: nil, enabled: true, editable: true), terminal: false))
        XCTAssertTrue(RemyInsertionTarget.supports(Facts(role: "AXTextField", editable: true), terminal: false))
        XCTAssertFalse(RemyInsertionTarget.supports(Facts(role: "AXTextArea", enabled: true), terminal: false))
        for facts in [Facts(role: "AXTextField", subrole: "AXSecureTextField", enabled: true, editable: true),
                      Facts(role: "AXTextArea", enabled: false, editable: true),
                      Facts(role: "AXTextArea", enabled: true, editable: true, readOnly: true),
                      Facts(role: "AXTextArea", enabled: true, editable: true, protected: true),
                      Facts(role: "AXTextArea", enabled: true, editable: false, valueSettable: true)] {
            XCTAssertFalse(RemyInsertionTarget.supports(facts, terminal: false))
            XCTAssertFalse(RemyInsertionTarget.supports(facts, terminal: true))
        }
        XCTAssertTrue(RemyInsertionTarget.supports(Facts(role: "AXTextArea", enabled: true, editable: true), terminal: false))
        XCTAssertTrue(RemyInsertionTarget.supports(Facts(role: "AXTextField", enabled: true, valueSettable: true), terminal: false))
        // A known terminal's text surface need not expose settable AXValue.
        XCTAssertTrue(RemyInsertionTarget.supports(Facts(role: "AXTextArea", enabled: true), terminal: true))
        XCTAssertFalse(RemyInsertionTarget.supports(Facts(role: "AXGroup", enabled: true), terminal: true))
    }

    func testNativeTextAreaWithoutOptionalEnabledFlagStillRequiresWritingCapability() {
        typealias Facts = RemyInsertionTarget.Editability
        // Observed TextEdit facts: AXTextArea; AXEnabled and AXEditable are
        // unsupported, while AXValue is writable. Missing differs from false.
        let native = Facts(role: "AXTextArea", valueSettable: true)
        XCTAssertTrue(RemyInsertionTarget.supports(native, terminal: false))
        XCTAssertFalse(RemyInsertionTarget.supports(Facts(role: "AXTextArea"), terminal: false))
        XCTAssertFalse(RemyInsertionTarget.supports(Facts(role: "AXTextArea", enabled: false, valueSettable: true), terminal: false))
        XCTAssertFalse(RemyInsertionTarget.supports(Facts(role: "AXTextArea", readOnly: true, valueSettable: true), terminal: false))
        XCTAssertFalse(RemyInsertionTarget.supports(Facts(role: "AXTextArea", protected: true, valueSettable: true), terminal: false))
        XCTAssertFalse(RemyInsertionTarget.supports(Facts(role: "AXTextArea", subrole: "AXSecureTextField", valueSettable: true), terminal: false))
        // Known terminal text surfaces retain their narrow typing exception;
        // absent optional metadata does not broaden supported roles.
        XCTAssertTrue(RemyInsertionTarget.supports(Facts(role: "AXTextArea"), terminal: true))
        XCTAssertFalse(RemyInsertionTarget.supports(Facts(role: "AXGroup"), terminal: true))
        XCTAssertFalse(RemyInsertionTarget.supports(Facts(role: "AXTextArea", enabled: false), terminal: true))
    }

    func testAncestorWalkAcceptsDeepBrowserTreeAndExactMaximumDepth() {
        for depth in [24, RemyInsertionTarget.maximumAncestryDepth] {
            var checked: [Int] = []
            let result = RemyInsertionTarget.ancestorDepth(from: 0, to: depth,
                parent: { $0 < depth ? $0 + 1 : nil },
                isSafe: { checked.append($0); return true },
                equals: { $0 == $1 }, withinBudget: { true })
            XCTAssertEqual(result, depth)
            XCTAssertEqual(checked, Array(1...depth), "Every ancestor, including the window, must be checked")
        }
    }

    func testAncestorWalkRejectsTreeBeyondMaximumWithoutAnotherParentRead() {
        let maximum = RemyInsertionTarget.maximumAncestryDepth
        var reads = 0
        let result = RemyInsertionTarget.ancestorDepth(from: 0, to: maximum + 1,
            parent: { reads += 1; return $0 + 1 }, isSafe: { _ in true },
            equals: { $0 == $1 }, withinBudget: { true })
        XCTAssertNil(result)
        XCTAssertEqual(reads, maximum)
    }

    func testAncestorWalkRejectsUnsafeFactsBeyondOldBrowserDepthLimit() {
        typealias Facts = RemyInsertionTarget.Editability
        let unsafeAncestors = [
            Facts(role: "AXTextArea", enabled: false, editable: true),
            Facts(role: "AXTextArea", subrole: "AXSecureTextField", editable: true),
            Facts(role: "AXSecureTextField", editable: true),
            Facts(role: "AXTextArea", editable: true, readOnly: true),
            Facts(role: "AXTextArea", editable: true, protected: true)
        ]
        for facts in unsafeAncestors {
            var checked: [Int] = []
            let result = RemyInsertionTarget.ancestorDepth(from: 0, to: 24,
                parent: { $0 + 1 },
                isSafe: {
                    checked.append($0)
                    let ancestor = $0 == 16 ? facts : Facts(role: "AXTextArea", editable: true)
                    return RemyInsertionTarget.supports(ancestor, terminal: false)
                }, equals: { $0 == $1 }, withinBudget: { true })
            XCTAssertNil(result)
            XCTAssertEqual(checked, Array(1...16), "Unsafe ancestor must stop traversal even after depth 12")
        }
        let unsafeWindow = RemyInsertionTarget.ancestorDepth(from: 0, to: 1,
            parent: { _ in 1 }, isSafe: { _ in false },
            equals: { $0 == $1 }, withinBudget: { true })
        XCTAssertNil(unsafeWindow, "Reaching the retained window cannot skip its safety check")
    }

    func testAncestorWalkRejectsMissingParentAndDifferentWindow() {
        let missing = RemyInsertionTarget.ancestorDepth(from: 0, to: 24,
            parent: { $0 < 16 ? $0 + 1 : nil }, isSafe: { _ in true },
            equals: { $0 == $1 }, withinBudget: { true })
        XCTAssertNil(missing)
        let anotherWindow = RemyInsertionTarget.ancestorDepth(from: 0, to: 25,
            parent: { $0 < 24 ? $0 + 1 : nil }, isSafe: { _ in true },
            equals: { $0 == $1 }, withinBudget: { true })
        XCTAssertNil(anotherWindow)
    }

    func testAncestorWalkRejectsSelfAndNonAdjacentCyclesImmediately() {
        for parents in [[0: 0], [0: 1, 1: 2, 2: 1]] {
            var reads = 0
            let result = RemyInsertionTarget.ancestorDepth(from: 0, to: 24,
                parent: { reads += 1; return parents[$0] }, isSafe: { _ in true },
                equals: { $0 == $1 }, withinBudget: { true })
            XCTAssertNil(result)
            XCTAssertEqual(reads, parents.count)
        }
    }

    func testAncestorWalkDoesNotReadParentAfterBudgetExpires() {
        var reads = 0
        let expired = RemyInsertionTarget.ancestorDepth(from: 0, to: 1,
            parent: { reads += 1; return $0 + 1 }, isSafe: { _ in true },
            equals: { $0 == $1 }, withinBudget: { false })
        XCTAssertNil(expired)
        XCTAssertEqual(reads, 0)

        for expiresDuringParent in [true, false] {
            var budgetAvailable = true
            var parentReads = 0
            let result = RemyInsertionTarget.ancestorDepth(from: 0, to: 1,
                parent: {
                    parentReads += 1
                    if expiresDuringParent { budgetAvailable = false }
                    return $0 + 1
                }, isSafe: { _ in
                    if !expiresDuringParent { budgetAvailable = false }
                    return true
                }, equals: { $0 == $1 }, withinBudget: { budgetAvailable })
            XCTAssertNil(result, "A parent or safety read that exhausts the budget cannot establish ancestry")
            XCTAssertEqual(parentReads, 1, "Expired traversal must not start another parent read")
        }
    }

    @MainActor
    func testMissingOrChangedProofCopiesExactPayloadWithoutSystemPasteboardOrPaste() async {
        let payload = " \tCafe\u{301} 👩🏽‍💻\r\nfinal line\n"
        for target in [RemyInsertionTarget.unavailable,
                       RemyInsertionTarget(terminal: false, check: { _ in false }),
                       RemyInsertionTarget(terminal: true, check: { _ in true })] {
            var copied: String?
            let inserter = TextInserter(clipboardWriter: { copied = $0; return true })
            let lease = RemyDeliveryAuthorization()
            let result = await inserter.insertText(payload, pasteAutomatically: true,
                verbatimPayload: true, targetSnapshot: target, authorization: lease)
            if case .copiedOnly = result {} else { XCTFail("Expected safe copy-only") }
            XCTAssertEqual(Array((copied ?? "").utf8), Array(payload.utf8))
            XCTAssertEqual(lease.snapshot, .init(copied: true, pasteClaimed: false))
        }
    }

    @MainActor
    func testRevokedLeasePreventsEvenRecoveryCopyWithUnavailableTarget() async {
        var writes = 0
        let inserter = TextInserter(clipboardWriter: { _ in writes += 1; return true })
        let lease = RemyDeliveryAuthorization()
        lease.revoke()
        _ = await inserter.insertText("cancelled", verbatimPayload: true,
            targetSnapshot: .unavailable, authorization: lease)
        XCTAssertEqual(writes, 0)
        XCTAssertFalse(lease.snapshot.pasteClaimed)
    }
}

private nonisolated final class FakeFocus: @unchecked Sendable {
    private let lock = NSLock()
    private var value = true
    var matches: Bool {
        get { lock.withLock { value } }
        set { lock.withLock { value = newValue } }
    }
}

private nonisolated final class FakeClipboard: @unchecked Sendable {
    private let lock = NSLock()
    private var value = RemyClipboardOwnership.State(generation: 7, text: "exact payload", stable: true)
    var state: RemyClipboardOwnership.State {
        get { lock.withLock { value } }
        set { lock.withLock { value = newValue } }
    }
}

private nonisolated final class FakeFocusEffects: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    var count: Int { lock.withLock { value } }
    func increment() { lock.withLock { value += 1 } }
}
