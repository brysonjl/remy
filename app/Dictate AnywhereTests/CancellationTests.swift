import XCTest
import CoreGraphics
@testable import Dictate_Anywhere

@MainActor
final class CancellationTests: XCTestCase {
    private func plainEscape(keyDown: Bool) -> CGEvent {
        let event = CGEvent(keyboardEventSource: nil, virtualKey: 53, keyDown: keyDown)!
        // Synthetic events otherwise inherit the machine's current modifiers,
        // making plain-Escape tests depend on the user's keyboard state.
        event.flags = []
        event.setIntegerValueField(.keyboardEventAutorepeat, value: 0)
        return event
    }

    private var binding: HotkeyBinding {
        HotkeyBinding(id: UUID(), keyCode: 7, modifiersRawValue: HotkeyModifiers([.command, .shift]).rawValue,
                      displayName: "⌘⇧X", mode: .handsFreeToggle)
    }

    private func press(_ gesture: inout CancellationGesture, now: TimeInterval = 10) {
        gesture.update(type: .keyDown, keyCode: 7, modifiers: [.command, .shift],
                       isRepeat: false, binding: binding, now: now)
    }

    func testHoldRequiresOneContinuousSecondAndFiresOnce() {
        var gesture = CancellationGesture()
        press(&gesture)
        XCTAssertEqual(gesture.progress(now: 10.5, requiresHold: true), 0.5)
        XCTAssertFalse(gesture.shouldCancel(now: 10.999, requiresHold: true))
        XCTAssertTrue(gesture.shouldCancel(now: 11, requiresHold: true))
        XCTAssertFalse(gesture.shouldCancel(now: 12, requiresHold: true))
    }

    func testImmediateModeDoesNotRequireHold() {
        var gesture = CancellationGesture()
        press(&gesture)
        XCTAssertTrue(gesture.shouldCancel(now: 10, requiresHold: false))
    }

    func testEarlyReleaseRequiresFreshFullHold() {
        var gesture = CancellationGesture()
        press(&gesture)
        gesture.update(type: .keyUp, keyCode: 7, modifiers: [.command, .shift],
                       isRepeat: false, binding: binding, now: 10.5)
        XCTAssertNil(gesture.startedAt)
        XCTAssertFalse(gesture.shouldCancel(now: 12, requiresHold: true))
        press(&gesture, now: 20)
        XCTAssertFalse(gesture.shouldCancel(now: 20.9, requiresHold: true))
        XCTAssertTrue(gesture.shouldCancel(now: 21, requiresHold: true))
    }

    func testReleasingModifierCancelsHoldEvenIfKeyStaysDown() {
        var gesture = CancellationGesture()
        press(&gesture)
        gesture.update(type: .flagsChanged, keyCode: 56, modifiers: [.command],
                       isRepeat: false, binding: binding, now: 10.5)
        gesture.update(type: .keyDown, keyCode: 7, modifiers: [.command, .shift],
                       isRepeat: true, binding: binding, now: 11)
        XCTAssertNil(gesture.startedAt)
        XCTAssertFalse(gesture.shouldCancel(now: 12, requiresHold: true))
    }

    func testRepeatsDoNotExtendHoldOrRearmAfterSessionReset() {
        var gesture = CancellationGesture()
        press(&gesture)
        gesture.update(type: .keyDown, keyCode: 7, modifiers: [.command, .shift],
                       isRepeat: true, binding: binding, now: 10.8)
        XCTAssertTrue(gesture.shouldCancel(now: 11, requiresHold: true))
        gesture.reset()
        gesture.update(type: .keyDown, keyCode: 7, modifiers: [.command, .shift],
                       isRepeat: true, binding: binding, now: 12)
        XCTAssertNil(gesture.startedAt)
    }

    func testEscapeAndWrongModifiersDoNotStartReboundShortcut() {
        for (code, modifiers) in [(UInt16(53), HotkeyModifiers()), (7, [.command]), (7, [.command, .shift, .option])] {
            var gesture = CancellationGesture()
            gesture.update(type: .keyDown, keyCode: code, modifiers: modifiers,
                           isRepeat: false, binding: binding, now: 10)
            XCTAssertNil(gesture.startedAt)
        }
    }

    func testModifierOnlyCancellationSupportsEarlyRelease() {
        var gesture = CancellationGesture()
        var modifierBinding = binding
        modifierBinding.keyCode = nil
        gesture.update(type: .flagsChanged, keyCode: 56, modifiers: [.command, .shift],
                       isRepeat: false, binding: modifierBinding, now: 10)
        XCTAssertEqual(gesture.startedAt, 10)
        gesture.update(type: .flagsChanged, keyCode: 56, modifiers: [.command],
                       isRepeat: false, binding: modifierBinding, now: 10.8)
        XCTAssertFalse(gesture.shouldCancel(now: 11, requiresHold: true))
    }

    func testConflictIncludesModifierPrefixButAllowsUnrelatedKey() {
        var recording = binding
        recording.id = UUID()
        XCTAssertTrue(ConflictDetector.cancellationConflict(recording, binding))
        recording.keyCode = nil
        XCTAssertTrue(ConflictDetector.cancellationConflict(recording, binding))
        recording.keyCode = 8
        XCTAssertFalse(ConflictDetector.cancellationConflict(recording, binding))
        XCTAssertFalse(ConflictDetector.cancellationConflict(.defaultBinding, .defaultCancelBinding))
    }

    func testCancelShortcutRoundTripIncludesDisabledBinding() throws {
        for original in [binding, HotkeyBinding(id: UUID(), keyCode: nil, modifiersRawValue: 0,
                                               displayName: "", mode: .handsFreeToggle)] {
            let decoded = try JSONDecoder().decode(HotkeyBinding.self, from: JSONEncoder().encode(original))
            XCTAssertEqual(decoded, original)
        }
    }

    func testGlobalHandlerLeavesEscapeAndIdleChordAvailableToOtherApps() {
        let service = HotkeyService()
        service.configureBindings(recording: [], cancellation: binding, requiresHold: false)
        var cancelled = 0
        service.onCancel = { cancelled += 1 }
        let chord = CGEvent(keyboardEventSource: nil, virtualKey: 7, keyDown: true)!
        chord.flags = [.maskCommand, .maskShift]
        XCTAssertFalse(service.handleEvent(type: .keyDown, event: chord))
        service.isCancellationEnabled = true
        let escape = CGEvent(keyboardEventSource: nil, virtualKey: 53, keyDown: true)!
        XCTAssertFalse(service.handleEvent(type: .keyDown, event: escape))
        XCTAssertEqual(cancelled, 0)
        XCTAssertTrue(service.handleEvent(type: .keyDown, event: chord))
        XCTAssertEqual(cancelled, 1)
    }

    func testPlainEscapeHoldPassesThroughAndReleaseClearsProgress() {
        let service = HotkeyService()
        service.configureBindings(recording: [], cancellation: .defaultCancelBinding, requiresHold: true)
        service.isCancellationEnabled = true
        var progress: Double?
        var cancelled = false
        service.onCancelProgress = { progress = $0 }
        service.onCancel = { cancelled = true }
        XCTAssertFalse(service.handleEvent(type: .keyDown, event: plainEscape(keyDown: true)))
        XCTAssertNotNil(progress)
        XCTAssertFalse(service.handleEvent(type: .keyUp, event: plainEscape(keyDown: false)))
        XCTAssertNil(progress)
        XCTAssertFalse(cancelled)
    }

    func testEscapeCanCancelWhilePushToTalkModifiersAreHeld() {
        let service = HotkeyService()
        service.configureBindings(recording: [.defaultBinding], cancellation: .defaultCancelBinding, requiresHold: false)
        service.isCancellationEnabled = true
        var cancelled = false
        service.onCancel = { cancelled = true }
        let heldModifiers = CGEvent(keyboardEventSource: nil, virtualKey: 55, keyDown: true)!
        heldModifiers.flags = [.maskControl, .maskAlternate, .maskCommand]
        _ = service.handleEvent(type: .flagsChanged, event: heldModifiers)
        let escape = CGEvent(keyboardEventSource: nil, virtualKey: 53, keyDown: true)!
        escape.flags = heldModifiers.flags
        _ = service.handleEvent(type: .keyDown, event: escape)
        XCTAssertTrue(cancelled)
    }

    func testImmediateEscapeOwnsRepeatsAndReleaseAcrossCancellationAndNewSession() {
        let service = HotkeyService()
        service.configureBindings(recording: [], cancellation: .defaultCancelBinding, requiresHold: false)
        service.isCancellationEnabled = true
        var cancellations = 0
        service.onCancel = { [weak service] in
            cancellations += 1
            service?.isCancellationEnabled = false
        }
        XCTAssertTrue(service.handleEvent(type: .keyDown, event: plainEscape(keyDown: true)))
        XCTAssertEqual(cancellations, 1)
        let repeated = plainEscape(keyDown: true)
        repeated.setIntegerValueField(.keyboardEventAutorepeat, value: 1)
        XCTAssertTrue(service.handleEvent(type: .keyDown, event: repeated))
        // A new session must not turn the still-held physical key into a cancel.
        service.resetCancellationGesture()
        service.isCancellationEnabled = true
        XCTAssertTrue(service.handleEvent(type: .keyDown, event: repeated))
        XCTAssertEqual(cancellations, 1)
        let release = plainEscape(keyDown: false)
        release.flags = [.maskShift]
        XCTAssertTrue(service.handleEvent(type: .keyUp, event: release))
        service.isCancellationEnabled = false
        XCTAssertFalse(service.handleEvent(type: .keyDown, event: plainEscape(keyDown: true)))
        XCTAssertFalse(service.handleEvent(type: .keyUp, event: plainEscape(keyDown: false)))
        service.isCancellationEnabled = true
        XCTAssertTrue(service.handleEvent(type: .keyDown, event: plainEscape(keyDown: true)))
        XCTAssertEqual(cancellations, 2)
        XCTAssertTrue(service.handleEvent(type: .keyUp, event: plainEscape(keyDown: false)))
    }

    func testImmediateEscapeLeavesWrongModifiersAndUnrelatedKeysAvailable() {
        let service = HotkeyService()
        service.configureBindings(recording: [], cancellation: .defaultCancelBinding, requiresHold: false)
        service.isCancellationEnabled = true
        var cancellations = 0
        service.onCancel = { cancellations += 1 }
        let wrongModifiers = plainEscape(keyDown: true)
        wrongModifiers.flags = [.maskCommand]
        XCTAssertFalse(service.handleEvent(type: .keyDown, event: wrongModifiers))
        let unrelated = CGEvent(keyboardEventSource: nil, virtualKey: 0, keyDown: true)!
        unrelated.flags = []
        XCTAssertFalse(service.handleEvent(type: .keyDown, event: unrelated))
        XCTAssertEqual(cancellations, 0)
        XCTAssertTrue(service.handleEvent(type: .keyDown, event: plainEscape(keyDown: true)))
        XCTAssertFalse(service.handleEvent(type: .keyDown, event: unrelated))
        XCTAssertEqual(cancellations, 1)
        XCTAssertTrue(service.handleEvent(type: .keyUp, event: plainEscape(keyDown: false)))
        // Release also resets a gesture when its callback leaves the session enabled.
        XCTAssertTrue(service.handleEvent(type: .keyDown, event: plainEscape(keyDown: true)))
        XCTAssertEqual(cancellations, 2)
        XCTAssertTrue(service.handleEvent(type: .keyUp, event: plainEscape(keyDown: false)))
    }

    func testImmediateEscapeOwnershipDoesNotSurviveMissedReleaseOrMonitoringStop() {
        let service = HotkeyService()
        service.configureBindings(recording: [], cancellation: .defaultCancelBinding, requiresHold: false)
        service.isCancellationEnabled = true
        service.onCancel = { [weak service] in service?.isCancellationEnabled = false }
        XCTAssertTrue(service.handleEvent(type: .keyDown, event: plainEscape(keyDown: true)))
        // A nonrepeat keydown is a new physical press if keyup was lost.
        XCTAssertFalse(service.handleEvent(type: .keyDown, event: plainEscape(keyDown: true)))
        service.isCancellationEnabled = true
        XCTAssertTrue(service.handleEvent(type: .keyDown, event: plainEscape(keyDown: true)))
        service.stopMonitoring()
        XCTAssertFalse(service.handleEvent(type: .keyUp, event: plainEscape(keyDown: false)))
    }

    func testHoldTimerCancelsOnceAndDisablingResetsIt() async {
        let service = HotkeyService()
        service.configureBindings(recording: [], cancellation: .defaultCancelBinding, requiresHold: true)
        service.isCancellationEnabled = true
        let cancelled = expectation(description: "held cancellation")
        cancelled.assertForOverFulfill = true
        service.onCancel = { cancelled.fulfill() }
        _ = service.handleEvent(type: .keyDown, event: plainEscape(keyDown: true))
        await fulfillment(of: [cancelled], timeout: 2)
        service.isCancellationEnabled = false
        service.stopMonitoring()
    }
}
