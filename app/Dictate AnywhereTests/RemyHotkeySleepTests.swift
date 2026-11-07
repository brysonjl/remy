import CoreGraphics
import XCTest
@testable import Dictate_Anywhere

@MainActor
final class RemyHotkeySleepTests: XCTestCase {
    private var rightOptionFlags: CGEventFlags {
        CGEventFlags(rawValue: CGEventFlags.maskAlternate.rawValue | HotkeyModifiers.rightOption.rawValue)
    }

    private func event(key: CGKeyCode = 61, flags: CGEventFlags = [], repeatKey: Bool = false) -> CGEvent {
        let event = CGEvent(keyboardEventSource: nil, virtualKey: key, keyDown: true)!
        event.flags = flags
        event.setIntegerValueField(.keyboardEventAutorepeat, value: repeatKey ? 1 : 0)
        return event
    }

    private func service(mode: HotkeyMode = .handsFreeToggle, key: UInt16? = nil) -> HotkeyService {
        let service = HotkeyService()
        let modifiers: HotkeyModifiers = key == nil ? [.rightOption] : []
        let binding = HotkeyBinding(id: UUID(), keyCode: key,
            modifiersRawValue: modifiers.rawValue, displayName: "Test shortcut", mode: mode)
        service.configureBindings(recording: [binding], cancellation: .defaultCancelBinding, requiresHold: false)
        return service
    }

    private func drainCallbacks() async {
        await withCheckedContinuation { continuation in
            DispatchQueue.main.async { continuation.resume() }
        }
    }

    func testSleepInvalidatesAlreadyQueuedToggleCallback() async {
        let service = service()
        var downs = 0
        service.onKeyDown = { _ in downs += 1 }
        _ = service.handleEvent(type: .flagsChanged, event: event(flags: rightOptionFlags))
        service.suspendForSystemSleep()
        service.resumeAfterSystemWake(currentFlags: [])
        await drainCallbacks()
        XCTAssertEqual(downs, 0)
        XCTAssertFalse(service.isMonitoring) // No real tap is opened by this test.
        _ = service.handleEvent(type: .flagsChanged, event: event(flags: rightOptionFlags))
        await drainCallbacks()
        XCTAssertEqual(downs, 1)
    }

    func testHeldModifierMustReleaseAfterWakeWithoutStaleHoldStop() async {
        let service = service(mode: .holdToRecord)
        var downs = 0
        var ups = 0
        service.onKeyDown = { _ in downs += 1 }
        service.onKeyUp = { _ in ups += 1 }
        _ = service.handleEvent(type: .flagsChanged, event: event(flags: rightOptionFlags))
        await drainCallbacks()
        XCTAssertEqual(downs, 1)
        service.suspendForSystemSleep()
        _ = service.handleEvent(type: .flagsChanged, event: event(flags: rightOptionFlags))
        service.resumeAfterSystemWake(currentFlags: rightOptionFlags)
        _ = service.handleEvent(type: .flagsChanged, event: event(flags: rightOptionFlags))
        _ = service.handleEvent(type: .flagsChanged, event: event())
        await drainCallbacks()
        XCTAssertEqual(downs, 1)
        XCTAssertEqual(ups, 0)
        _ = service.handleEvent(type: .flagsChanged, event: event(flags: rightOptionFlags))
        _ = service.handleEvent(type: .flagsChanged, event: event())
        await drainCallbacks()
        XCTAssertEqual(downs, 2)
        XCTAssertEqual(ups, 1)
    }

    func testStaleKeyReleaseAndRepeatCannotInvokeRecordingAfterWake() async {
        let service = service(mode: .holdToRecord, key: 49)
        var downs = 0
        var ups = 0
        service.onKeyDown = { _ in downs += 1 }
        service.onKeyUp = { _ in ups += 1 }
        _ = service.handleEvent(type: .keyDown, event: event(key: 49))
        await drainCallbacks()
        service.suspendForSystemSleep()
        service.resumeAfterSystemWake(currentFlags: [])
        _ = service.handleEvent(type: .keyDown, event: event(key: 49, repeatKey: true))
        _ = service.handleEvent(type: .keyUp, event: event(key: 49))
        await drainCallbacks()
        XCTAssertEqual(downs, 1)
        XCTAssertEqual(ups, 0)
        _ = service.handleEvent(type: .keyDown, event: event(key: 49))
        await drainCallbacks()
        XCTAssertEqual(downs, 2)
    }

    func testCapsLockLatchDoesNotBlockWakeRearming() async {
        let service = service()
        var downs = 0
        service.onKeyDown = { _ in downs += 1 }
        service.suspendForSystemSleep()
        service.resumeAfterSystemWake(currentFlags: .maskAlphaShift)
        _ = service.handleEvent(type: .flagsChanged,
            event: event(flags: rightOptionFlags.union(.maskAlphaShift)))
        await drainCallbacks()
        XCTAssertEqual(downs, 1)

        // A real held Option must still release; the remaining Caps Lock
        // latch counts as neutral without requiring Caps Lock to be toggled.
        service.suspendForSystemSleep()
        service.resumeAfterSystemWake(currentFlags: rightOptionFlags.union(.maskAlphaShift))
        _ = service.handleEvent(type: .flagsChanged, event: event(flags: .maskAlphaShift))
        _ = service.handleEvent(type: .flagsChanged,
            event: event(flags: rightOptionFlags.union(.maskAlphaShift)))
        await drainCallbacks()
        XCTAssertEqual(downs, 2)
    }

    func testSleepResetsQuietEscapeAndDoesNotReenableSessionCancellation() {
        let service = service()
        var cancels = 0
        service.onCancel = { cancels += 1 }
        service.isCancellationEnabled = true
        XCTAssertTrue(service.handleEvent(type: .keyDown, event: event(key: 53)))
        XCTAssertEqual(cancels, 1)
        service.suspendForSystemSleep()
        service.resumeAfterSystemWake(currentFlags: [])
        XCTAssertFalse(service.isCancellationEnabled)
        XCTAssertFalse(service.handleEvent(type: .keyUp, event: event(key: 53)))
        XCTAssertFalse(service.handleEvent(type: .keyDown, event: event(key: 53, repeatKey: true)))
        XCTAssertEqual(cancels, 1)
        service.isCancellationEnabled = true
        XCTAssertTrue(service.handleEvent(type: .keyDown, event: event(key: 53)))
        XCTAssertEqual(cancels, 2)
    }
}
