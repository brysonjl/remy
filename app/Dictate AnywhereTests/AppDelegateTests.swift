import XCTest
import AppKit
@testable import Dictate_Anywhere

@MainActor
final class AppDelegateTests: XCTestCase {
    func testApplicationDidBecomeActiveRefreshesPermissionState() async {
        let status = MutableAppDelegatePermissionStatus(microphoneGranted: false)
        let permissions = Permissions(statusProvider: {
            (mic: status.microphoneGranted, accessibility: false)
        })
        await permissions.refresh()
        XCTAssertFalse(permissions.micGranted)

        let appState = AppState(permissions: permissions)
        let appDelegate = AppDelegate(appState: appState)
        status.microphoneGranted = true

        // Exercise the production operation explicitly, without enabling
        // automatic lifecycle side effects in the XCTest host.
        await appDelegate.refreshAfterActivation()

        XCTAssertTrue(appState.permissions.micGranted)
        await appState.shutdown()
    }
    func testAutomaticTestHostActivationDoesNotRefreshPermissionsOrStartHotkeys() async {
        XCTAssertTrue(AppDelegate.isRunningTests)
        let unexpectedRefresh = expectation(description: "automatic permission refresh")
        unexpectedRefresh.isInverted = true
        let permissions = Permissions(statusProvider: {
            unexpectedRefresh.fulfill()
            return (true, false)
        })
        let appState = AppState(permissions: permissions)
        let delegate = AppDelegate(appState: appState)
        XCTAssertFalse(permissions.hasChecked)
        XCTAssertFalse(appState.hotkeyService.isMonitoring)

        delegate.applicationDidBecomeActive(Notification(name: NSApplication.didBecomeActiveNotification))
        // The unguarded callback used to enqueue asynchronous permission work.
        await fulfillment(of: [unexpectedRefresh], timeout: 0.1)

        XCTAssertFalse(permissions.hasChecked, "Host activation must not schedule permission refresh")
        XCTAssertFalse(appState.hotkeyService.isMonitoring, "Host activation must not register a global event tap")
        await appState.shutdown()
    }

    func testAutomaticTestHostReopenDoesNotPresentMainWindow() {
        XCTAssertTrue(AppDelegate.isRunningTests)
        let appState = AppState(permissions: Permissions(statusProvider: { (false, false) }))
        let delegate = AppDelegate(appState: appState)
        let policy = NSApp.activationPolicy()
        XCTAssertFalse(delegate.applicationShouldHandleReopen(NSApp, hasVisibleWindows: false))
        XCTAssertEqual(NSApp.activationPolicy(), policy)
        XCTAssertFalse(delegate.applicationShouldHandleReopen(NSApp, hasVisibleWindows: true))
    }

    func testMainSettingsWindowExcludesRecordingAndAuxiliaryWindowsRegardlessOfOrder() {
        let frame = NSRect(x: 0, y: 0, width: 320, height: 200)
        let recording = DictationOverlayPanel(contentRect: frame,
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        recording.identifier = NSUserInterfaceItemIdentifier("remy.recording-overlay")
        // A titled panel at normal level still must not become main settings.
        let auxiliaryPanel = NSPanel(contentRect: frame,
            styleMask: [.titled], backing: .buffered, defer: false)
        let floating = NSWindow(contentRect: frame,
            styleMask: [.titled], backing: .buffered, defer: false)
        floating.level = .floating
        let borderless = NSWindow(contentRect: frame,
            styleMask: [.borderless], backing: .buffered, defer: false)
        let settings = NSWindow(contentRect: frame,
            styleMask: [.titled], backing: .buffered, defer: false)
        let windows: [NSWindow] = [recording, auxiliaryPanel, floating, borderless, settings]
        for window in windows {
            window.isReleasedWhenClosed = false
            window.contentView = NSView(frame: frame)
        }
        defer { windows.forEach { $0.close() } }
        XCTAssertTrue(AppDelegate.mainSettingsWindow(in: windows) === settings)
        XCTAssertTrue(AppDelegate.mainSettingsWindow(in: Array(windows.reversed())) === settings)
        XCTAssertNil(AppDelegate.mainSettingsWindow(in: [recording, auxiliaryPanel]))
        XCTAssertNil(AppDelegate.mainSettingsWindow(in: [floating, borderless]))
        XCTAssertNil(AppDelegate.mainSettingsWindow(in: []))
    }

}

private final class MutableAppDelegatePermissionStatus: @unchecked Sendable {
    private let lock = NSLock()
    private var microphoneValue: Bool

    init(microphoneGranted: Bool) {
        microphoneValue = microphoneGranted
    }

    var microphoneGranted: Bool {
        get { lock.withLock { microphoneValue } }
        set { lock.withLock { microphoneValue = newValue } }
    }
}
