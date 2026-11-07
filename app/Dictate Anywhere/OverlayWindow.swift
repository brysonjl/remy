//
//  OverlayWindow.swift
//  Dictate Anywhere
//
//  Floating NSWindow controller for dictation overlay.
//

import AppKit
import SwiftUI

/// A recording indicator belongs alongside the destination app, without ever
/// becoming the keyboard target or Remy's main settings window.
final class DictationOverlayPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

@Observable
final class OverlayWindow {
    // MARK: - Properties

    private var window: NSWindow?
    private var hostingView: NSHostingView<OverlayContent>?
    private var hideTask: Task<Void, Never>?
    @ObservationIgnored private var presentationProbeID: UUID?
    private let model = OverlayModel()
    // The canvas already reserves `shadowInset` below the pill.
    private let bottomMargin: CGFloat = OverlayMetrics.size(24) - OverlaySize.shadowInset
    private var canvasWidth: CGFloat { Settings.shared.overlaySize.canvasSize.width }
    private var canvasHeight: CGFloat { Settings.shared.overlaySize.canvasSize.height }
    @ObservationIgnored private var preferenceObserver: NSObjectProtocol?

    @ObservationIgnored private let screenSession: OverlayScreenSession

    // MARK: - Init

    init(screenResolver: OverlayScreenResolving = SystemOverlayScreenResolver()) {
        screenSession = OverlayScreenSession(resolver: screenResolver)
        preferenceObserver = NotificationCenter.default.addObserver(
            forName: .overlayPreferencesChanged, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.applyAppearance()
                if self?.model.isVisible == true { self?.positionWindow() }
            }
        }
    }

    deinit {
        if let preferenceObserver { NotificationCenter.default.removeObserver(preferenceObserver) }
    }

    // MARK: - Public

    func setCancellationProgress(_ progress: Double?) {
        model.cancellationProgress = progress
    }

    /// Whether the overlay is currently presented.
    var isVisible: Bool {
        model.isVisible
    }

    var state: OverlayState { model.overlayState }

    /// Starts a dictation, so the overlay chooses its display afresh.
    ///
    /// - Parameter targetProcessIdentifier: the app the transcript will be
    ///   pasted into, captured before audio startup so the overlay follows it
    ///   rather than whichever app is frontmost once the microphone is live.
    func beginSession(targetProcessIdentifier: pid_t?) {
        // Cancelling the scheduled hide is what keeps it from resetting the
        // session that is about to be pinned — but the previous dictation's
        // badge is still on screen, and on the previous display. Dismiss it
        // here rather than leaving it up for the whole of this dictation's
        // startup, which is longer than the hide would have taken.
        hideTask?.cancel()
        hideTask = nil

        if Thread.isMainThread {
            beginSessionImpl(targetProcessIdentifier: targetProcessIdentifier)
        } else {
            DispatchQueue.main.async { [weak self] in
                self?.beginSessionImpl(targetProcessIdentifier: targetProcessIdentifier)
            }
        }
    }

    func showListening(level: Float, transcript: String) {
        model.updateListening(level: level, transcript: transcript)
        show(state: .listening)
    }

    func show(state: OverlayState) {
        let supersedesFinishingSession = hideTask != nil
        hideTask?.cancel()
        hideTask = nil

        if Thread.isMainThread {
            showImpl(state: state, startsNewSession: supersedesFinishingSession)
        } else {
            DispatchQueue.main.async { [weak self] in
                self?.showImpl(state: state, startsNewSession: supersedesFinishingSession)
            }
        }
    }

    /// Rejected-start feedback without replacing the active session's state,
    /// transcript or cancellation progress. No timer is scheduled: the next
    /// legitimate state update or hide clears this notice.
    func showBusy() {
        hideTask?.cancel()
        hideTask = nil
        if Thread.isMainThread { showBusyImpl() }
        else { DispatchQueue.main.async { [weak self] in self?.showBusyImpl() } }
    }

    private func showBusyImpl() {
        model.showBusy()
        showImpl(state: model.overlayState, startsNewSession: false, updatesState: false)
    }

    func hide(afterDelay delay: TimeInterval = 0.5) {
        hideTask?.cancel()

        if delay > 0 {
            hideTask = Task { @MainActor [weak self] in
                try? await Task.sleep(for: .seconds(delay))
                guard !Task.isCancelled else { return }
                self?.hideImpl()
            }
        } else {
            hideTask = nil
            if Thread.isMainThread {
                hideImpl()
            } else {
                DispatchQueue.main.async { [weak self] in
                    self?.hideImpl()
                }
            }
        }
    }

    // MARK: - Private

    private func showImpl(state: OverlayState, startsNewSession: Bool, updatesState: Bool = true) {
        if startsNewSession {
            screenSession.end()
        }

        if window == nil {
            window = createWindow()
            let content = OverlayContent(model: model)
            hostingView = NSHostingView(rootView: content)
            hostingView?.frame = NSRect(x: 0, y: 0, width: canvasWidth, height: canvasHeight)
            window?.contentView = hostingView
        }

        applyAppearance()
        if updatesState { model.updateState(state) }
        let needsPresentation = !model.isVisible || window?.isVisible != true
        if !model.isVisible { model.isVisible = true }

        positionWindow()
        if needsPresentation {
            window?.orderFrontRegardless()
            schedulePresentationProbe()
        }
    }

    /// One delayed, numeric-only observation per native presentation. Capture
    /// session metadata now, rather than attributing delayed work to a later run.
    private func schedulePresentationProbe() {
        guard PerfTrace.isEnabled, let presentedWindow = window else { return }
        let probeID = UUID()
        presentationProbeID = probeID
        let trace = PerfTrace.begin("overlay.presentation")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self, weak presentedWindow] in
            guard let self, let presentedWindow,
                  self.presentationProbeID == probeID,
                  self.model.isVisible, self.window === presentedWindow else {
                trace.end(outcome: "superseded")
                return
            }
            self.presentationProbeID = nil
            let frame = presentedWindow.frame
            var counts = [
                "model_visible": self.model.isVisible ? 1 : 0,
                "native_visible": presentedWindow.isVisible ? 1 : 0,
                "on_active_space": presentedWindow.isOnActiveSpace ? 1 : 0,
                "occlusion_visible": presentedWindow.occlusionState.contains(.visible) ? 1 : 0,
                "alpha_scaled": Int((presentedWindow.alphaValue * 1_000).rounded()),
                "frame_x": Int(frame.origin.x.rounded()),
                "frame_y": Int(frame.origin.y.rounded()),
                "frame_width": Int(frame.width.rounded()),
                "frame_height": Int(frame.height.rounded()),
                "activation_policy": NSApp.activationPolicy().rawValue,
            ]
            if let screen = presentedWindow.screen {
                let visible = screen.visibleFrame
                let full = screen.frame
                counts.merge([
                    "screen_x": Int(full.origin.x.rounded()),
                    "screen_y": Int(full.origin.y.rounded()),
                    "screen_width": Int(full.width.rounded()),
                    "screen_height": Int(full.height.rounded()),
                    "screen_visible_x": Int(visible.origin.x.rounded()),
                    "screen_visible_y": Int(visible.origin.y.rounded()),
                    "screen_visible_width": Int(visible.width.rounded()),
                    "screen_visible_height": Int(visible.height.rounded()),
                ]) { _, new in new }
            }
            trace.recordCounts(counts)
            trace.end(outcome: "observed")
        }
    }

    private func beginSessionImpl(targetProcessIdentifier: pid_t?) {
        dismissVisuals()
        screenSession.begin(targetProcessIdentifier: targetProcessIdentifier)
    }

    private func hideImpl() {
        dismissVisuals()

        // The next appearance is a new session and picks its display afresh.
        screenSession.end()
    }

    /// Takes the overlay off screen without touching the screen session, so
    /// dismissing a finished dictation cannot clear the display a newly begun
    /// one has already pinned.
    private func dismissVisuals() {
        presentationProbeID = nil
        model.resetBusy()
        model.isVisible = false

        // Allow fade-out animation to complete before removing window
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in
            guard let self else { return }
            // Guard against show-during-fade race condition
            if !self.model.isVisible {
                self.window?.orderOut(nil)
            }
        }
    }

    private func applyAppearance() {
        let scheme = Settings.shared.overlayThemeMode.overlayColorScheme
        let name: NSAppearance.Name = scheme == .dark ? .darkAqua : .aqua
        if window?.appearance?.name != name { window?.appearance = NSAppearance(named: name) }
    }

    private func createWindow() -> NSWindow {
        let win = DictationOverlayPanel(
            contentRect: NSRect(x: 0, y: 0, width: canvasWidth, height: canvasHeight),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        win.isOpaque = false
        win.backgroundColor = .clear
        win.hasShadow = false
        win.level = .floating
        win.isFloatingPanel = true
        win.hidesOnDeactivate = false
        win.canHide = false
        // All Spaces alone does not opt a regular app's window into other
        // applications' presentation sets (including Stage Manager).
        win.collectionBehavior = [.canJoinAllSpaces, .canJoinAllApplications, .stationary, .ignoresCycle, .fullScreenAuxiliary]
        win.identifier = NSUserInterfaceItemIdentifier("remy.recording-overlay")
        win.ignoresMouseEvents = true
        win.isExcludedFromWindowsMenu = true
        win.titleVisibility = .hidden
        win.titlebarAppearsTransparent = true
        return win
    }

    private func positionWindow() {
        guard let win = window, let visibleFrame = screenSession.visibleFrame() else { return }

        let size = NSSize(width: canvasWidth, height: canvasHeight)
        let origin = OverlayScreenPicker.overlayOrigin(
            inVisibleFrame: visibleFrame,
            size: size,
            bottomMargin: bottomMargin
        )

        let frame = NSRect(origin: origin, size: size)
        guard win.frame != frame else { return }

        win.setFrame(frame, display: true, animate: false)
    }
}
