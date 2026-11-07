//
//  HotkeyService.swift
//  Dictate Anywhere
//
//  Global shortcut via CGEvent tap. Captures ANY key combination.
//

import Foundation
import CoreGraphics
import os

@Observable
final class HotkeyService {
    // MARK: - Callbacks

    var onKeyDown: ((HotkeyBinding) -> Void)?
    var onKeyUp: ((HotkeyBinding) -> Void)?
    var onCancel: (() -> Void)?
    var onCancelProgress: ((Double?) -> Void)?
    var isCancellationEnabled = false {
        didSet { if !isCancellationEnabled { resetCancellationGesture() } }
    }
    private var cancelBinding = HotkeyBinding.defaultCancelBinding
    private var requiresCancelHold = true
    private var cancelGesture = CancellationGesture()
    private var cancelHoldTask: Task<Void, Never>?
    // A swallowed immediate Escape owns its physical release even when cancel
    // synchronously disables/reset the session gesture. Never rearm from repeats.
    private var ownsImmediateEscapePress = false

    func resetCancellationGesture() {
        cancelHoldTask?.cancel()
        cancelHoldTask = nil
        cancelGesture.reset()
        onCancelProgress?(nil)
    }

    // MARK: - State

    private(set) var isMonitoring = false
    fileprivate var eventTap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var activeBindingIDs: Set<UUID> = []
    private let functionKeyCodes: Set<UInt16> = [63, 179]
    private var retryWorkItem: DispatchWorkItem?
    private var retryAttempt = 0
    private(set) var isSuspendedForSleep = false
    private var resumeMonitoringAfterSleep = false
    private var waitsForNeutralModifiers = false
    private var gestureGeneration = UUID()

    /// Cached bindings snapshot — read from the CGEvent callback thread.
    /// Only updated at startMonitoring() / restartMonitoring() to avoid data races.
    fileprivate var cachedBindings: [HotkeyBinding] = []

    private let logger = Logger(
        subsystem: RemyDiagnostics.subsystem,
        category: "HotkeyService"
    )

    // MARK: - Initialization

    init() {}

    deinit {
        stopMonitoring()
    }

    // MARK: - Public

    func startMonitoring() {
        if isSuspendedForSleep {
            resumeMonitoringAfterSleep = true
            return
        }
        attemptMonitoringStart(resetRetryState: true)
    }

    func stopMonitoring() {
        gestureGeneration = UUID()
        resumeMonitoringAfterSleep = false
        ownsImmediateEscapePress = false
        resetCancellationGesture()
        cancelRetry(resetAttempts: true)
        if let source = runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes)
        }
        if let tap = eventTap {
            CGEvent.tapEnable(tap: tap, enable: false)
            CFMachPortInvalidate(tap)
        }
        runLoopSource = nil
        eventTap = nil
        cachedBindings = []
        isMonitoring = false
        activeBindingIDs.removeAll()
    }

    func restartMonitoring() {
        stopMonitoring()
        startMonitoring()
    }

    /// System sleep revokes gestures and already-queued shortcut callbacks.
    /// AppState separately cancels the foreground session; no callback is fired.
    func suspendForSystemSleep() {
        guard !isSuspendedForSleep else { return }
        let shouldResume = isMonitoring || retryWorkItem != nil
        let bindings = cachedBindings
        isSuspendedForSleep = true
        isCancellationEnabled = false
        stopMonitoring()
        // Preserve the matching snapshot for a service configured without a tap
        // (deterministic tests); real monitoring refreshes it from settings on wake.
        cachedBindings = bindings
        resumeMonitoringAfterSleep = shouldResume
    }

    /// A held modifier must first return to neutral after wake. No release or
    /// autorepeat from the pre-sleep gesture may start/stop a new recording.
    func resumeAfterSystemWake(currentFlags: CGEventFlags = CGEventSource.flagsState(.combinedSessionState)) {
        guard isSuspendedForSleep else { return }
        isSuspendedForSleep = false
        gestureGeneration = UUID()
        waitsForNeutralModifiers = hasHeldWakeModifiers(currentFlags)
        let shouldResume = resumeMonitoringAfterSleep
        resumeMonitoringAfterSleep = false
        if shouldResume { startMonitoring() }
    }

    /// Caps Lock is a latch, not a held gesture. Ignore it only for wake
    /// rearming; normal key/modifier matching still receives the original flags.
    private func hasHeldWakeModifiers(_ flags: CGEventFlags) -> Bool {
        !Settings.hotkeyModifiers(from: flags).subtracting(.capsLock).isEmpty
    }

    private func enqueueGesture(_ binding: HotkeyBinding, isDown: Bool) {
        let generation = gestureGeneration
        DispatchQueue.main.async { [weak self] in
            guard let self, !self.isSuspendedForSleep,
                  self.gestureGeneration == generation else { return }
            if isDown { self.onKeyDown?(binding) }
            else { self.onKeyUp?(binding) }
        }
    }

    // MARK: - Startup

    private func attemptMonitoringStart(resetRetryState: Bool) {
        guard !isSuspendedForSleep, !isMonitoring else { return }

        let settings = Settings.shared
        guard settings.hasHotkey || settings.cancelShortcut.hasBinding else {
            cachedBindings = []
            cancelRetry(resetAttempts: true)
            return
        }

        if resetRetryState {
            cancelRetry(resetAttempts: true)
        } else {
            retryWorkItem = nil
        }

        // Snapshot + normalize bindings so the callback thread never touches Settings
        configureBindings(recording: settings.hotkeyBindings, cancellation: settings.cancelShortcut,
                          requiresHold: settings.holdToCancel)

        let eventMask: CGEventMask = (1 << CGEventType.keyDown.rawValue) | (1 << CGEventType.keyUp.rawValue) | (1 << CGEventType.flagsChanged.rawValue)

        // Use Unmanaged to pass self as user info
        let selfPtr = Unmanaged.passUnretained(self).toOpaque()

        guard let tap = CGEvent.tapCreate(
            tap: .cghidEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: eventMask,
            callback: hotkeyEventCallback,
            userInfo: selfPtr
        ) else {
            cachedBindings = []
            scheduleRetry()
            logger.error("Failed to create CGEvent tap. Will retry until the login session is ready.")
            return
        }

        eventTap = tap
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)

        isMonitoring = true
        cancelRetry(resetAttempts: true)
        logger.info("Hotkey monitoring started")
    }

    private func scheduleRetry() {
        guard !isMonitoring, retryWorkItem == nil else { return }

        retryAttempt += 1
        let delay = min(pow(2.0, Double(max(0, retryAttempt - 1))) * 0.5, 5.0)
        let workItem = DispatchWorkItem { [weak self] in
            self?.attemptMonitoringStart(resetRetryState: false)
        }
        retryWorkItem = workItem
        let attemptNumber = retryAttempt
        logger.info(
            "Scheduling hotkey monitoring retry in \(String(format: "%.1f", delay), privacy: .public)s (attempt \(attemptNumber, privacy: .public))"
        )
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: workItem)
    }

    private func cancelRetry(resetAttempts: Bool) {
        retryWorkItem?.cancel()
        retryWorkItem = nil
        if resetAttempts {
            retryAttempt = 0
        }
    }

    // MARK: - Event Handling

    func configureBindings(recording: [HotkeyBinding], cancellation: HotkeyBinding, requiresHold: Bool) {
        gestureGeneration = UUID()
        activeBindingIDs.removeAll()
        resetCancellationGesture()
        cachedBindings = recording.map(canonicalBindingForMatching)
        cancelBinding = canonicalBindingForMatching(cancellation)
        requiresCancelHold = requiresHold
    }

    func handleEvent(type: CGEventType, event: CGEvent) -> Bool {
        guard !isSuspendedForSleep else { return false }
        if waitsForNeutralModifiers {
            if !hasHeldWakeModifiers(event.flags) {
                waitsForNeutralModifiers = false
            }
            return false
        }
        let consumedByCancellation = handleCancellationEvent(type: type, event: event)
        if consumedByCancellation { return true }

        let bindings = cachedBindings
        var shouldConsumeEvent = false
        for binding in bindings where binding.hasBinding {
            if binding.keyCode == nil {
                handleModifierOnlyEvent(type: type, event: event, binding: binding)
            } else {
                if handleKeyedEvent(type: type, event: event, binding: binding) {
                    shouldConsumeEvent = true
                }
            }
        }
        return shouldConsumeEvent
    }

    private func handleCancellationEvent(type: CGEventType, event: CGEvent) -> Bool {
        let keyCode = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
        let isRepeat = event.getIntegerValueField(.keyboardEventAutorepeat) != 0
        if ownsImmediateEscapePress && keyCode == 53 {
            if type == .keyUp {
                ownsImmediateEscapePress = false
                resetCancellationGesture()
                return true
            }
            if type == .keyDown {
                if isRepeat { return true }
                // A fresh physical down means the release was missed while the
                // tap was unavailable. Do not swallow a later idle press.
                ownsImmediateEscapePress = false
                resetCancellationGesture()
            }
        }
        guard isCancellationEnabled, cancelBinding.hasBinding else { return false }
        let wasPressed = cancelGesture.startedAt != nil
        cancelGesture.update(
            type: type, keyCode: keyCode,
            modifiers: cancellationModifiers(from: Settings.hotkeyModifiers(from: event.flags)),
            isRepeat: isRepeat,
            binding: cancelBinding, now: ProcessInfo.processInfo.systemUptime
        )
        let isPressed = cancelGesture.startedAt != nil
        let isPlainEscape = cancelBinding.keyCode == 53 && cancelBinding.modifiers.isEmpty
        let consumesImmediateEscape = isPlainEscape && !requiresCancelHold
            && !wasPressed && isPressed
        if consumesImmediateEscape {
            // Establish ownership before onCancel can reset/disable cancellation.
            ownsImmediateEscapePress = true
        }
        if wasPressed && !isPressed {
            resetCancellationGesture()
        } else if !wasPressed && isPressed {
            advanceCancellationGesture()
            if requiresCancelHold {
                cancelHoldTask = Task { @MainActor [weak self] in
                    while !Task.isCancelled {
                        do { try await Task.sleep(for: .milliseconds(25)) } catch { return }
                        guard let self, self.isCancellationEnabled,
                              self.cancelGesture.startedAt != nil else { return }
                        self.advanceCancellationGesture()
                        if self.cancelGesture.fired { return }
                    }
                }
            }
        }
        // Plain Escape remains available to the foreground application during a hold.
        // Immediate cancellation consumes its matched press to avoid sending
        // Escape to the destination (and triggering its alert sound).
        // Other bound key combinations are reserved only during an active dictation.
        return consumesImmediateEscape || !isPlainEscape && cancelBinding.keyCode == keyCode
            && (type == .keyDown || type == .keyUp) && (wasPressed || isPressed)
    }

    private func advanceCancellationGesture() {
        let now = ProcessInfo.processInfo.systemUptime
        onCancelProgress?(requiresCancelHold ? cancelGesture.progress(now: now, requiresHold: true) : nil)
        if cancelGesture.shouldCancel(now: now, requiresHold: requiresCancelHold) {
            onCancelProgress?(nil)
            onCancel?()
        }
    }

    private func cancellationModifiers(from eventModifiers: HotkeyModifiers) -> HotkeyModifiers {
        var modifiers = eventModifiers
        let cancellationGroups = Settings.deviceIndependentModifiers(from: cancelBinding.modifiers)
        let groups: [(HotkeyModifiers, HotkeyModifiers)] = [
            (.command, [.command, .leftCommand, .rightCommand]),
            (.control, [.control, .leftControl, .rightControl]),
            (.option, [.option, .leftOption, .rightOption]),
            (.shift, [.shift, .leftShift, .rightShift]),
            (.function, [.function]),
        ]
        // A user holding their push-to-talk shortcut must still be able to
        // cancel. Ignore only those held recording modifiers that are not
        // themselves part of the cancellation shortcut.
        for binding in cachedBindings where binding.mode == .holdToRecord && activeBindingIDs.contains(binding.id) {
            let heldGroups = Settings.deviceIndependentModifiers(from: binding.modifiers)
            for (generic, allSides) in groups where heldGroups.contains(generic) && !cancellationGroups.contains(generic) {
                modifiers.subtract(allSides)
            }
        }
        return modifiers
    }

    private func canonicalBindingForMatching(_ binding: HotkeyBinding) -> HotkeyBinding {
        guard let keyCode = binding.keyCode, functionKeyCodes.contains(keyCode) else { return binding }
        var normalized = binding
        var modifiers = normalized.modifiers
        modifiers.insert(.function)
        normalized.keyCode = nil
        normalized.modifiers = modifiers
        return normalized
    }

    private func handleKeyedEvent(type: CGEventType, event: CGEvent, binding: HotkeyBinding) -> Bool {
        guard let targetKeyCode = binding.keyCode else { return false }
        guard type == .keyDown || type == .keyUp else { return false }

        let keyCode = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
        guard keyCode == targetKeyCode else { return false }

        let bindingID = binding.id
        let targetModifiers = binding.modifiers
        let eventFlags = Settings.hotkeyModifiers(from: event.flags)
        let isActive = activeBindingIDs.contains(bindingID)
        let modifiersMatch = targetModifiers.isEmpty || Settings.keyedModifiersMatch(
            event: eventFlags,
            target: targetModifiers
        )
        guard modifiersMatch || (type == .keyUp && isActive) else { return false }

        switch type {
        case .keyDown:
            let isRepeat = event.getIntegerValueField(.keyboardEventAutorepeat)
            guard isRepeat == 0, !isActive else { return true }
            activeBindingIDs.insert(bindingID)
            enqueueGesture(binding, isDown: true)
            return true

        case .keyUp:
            guard isActive else { return modifiersMatch }
            activeBindingIDs.remove(bindingID)
            enqueueGesture(binding, isDown: false)
            return true

        default:
            return false
        }
    }

    private func handleModifierOnlyEvent(type: CGEventType, event: CGEvent, binding: HotkeyBinding) {
        let targetModifiers = binding.modifiers
        guard !targetModifiers.isEmpty, type == .flagsChanged else { return }

        let eventModifiers = Settings.hotkeyModifiers(from: event.flags)
        let isHotkeyActive = Settings.modifierOnlyModifiersMatch(event: eventModifiers, target: targetModifiers)
        let bindingID = binding.id
        let isActive = activeBindingIDs.contains(bindingID)

        switch binding.mode {
        case .holdToRecord:
            if isHotkeyActive, !isActive {
                activeBindingIDs.insert(bindingID)
                enqueueGesture(binding, isDown: true)
            } else if !isHotkeyActive, isActive {
                activeBindingIDs.remove(bindingID)
                enqueueGesture(binding, isDown: false)
            }
        case .handsFreeToggle:
            if isHotkeyActive, !isActive {
                activeBindingIDs.insert(bindingID)
                enqueueGesture(binding, isDown: true)
            } else if !isHotkeyActive, isActive {
                activeBindingIDs.remove(bindingID)
            }
        }
    }

}

// MARK: - C Callback

private func hotkeyEventCallback(
    proxy: CGEventTapProxy,
    type: CGEventType,
    event: CGEvent,
    userInfo: UnsafeMutableRawPointer?
) -> Unmanaged<CGEvent>? {
    guard let userInfo else { return Unmanaged.passUnretained(event) }

    // Handle tap disabled events
    if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
        // Re-enable the tap
        let service = Unmanaged<HotkeyService>.fromOpaque(userInfo).takeUnretainedValue()
        service.resetCancellationGesture()
        if !service.isSuspendedForSleep, let tap = service.eventTap {
            CGEvent.tapEnable(tap: tap, enable: true)
        }
        return Unmanaged.passUnretained(event)
    }

    let service = Unmanaged<HotkeyService>.fromOpaque(userInfo).takeUnretainedValue()
    if service.handleEvent(type: type, event: event) {
        return nil
    }

    return Unmanaged.passUnretained(event)
}
