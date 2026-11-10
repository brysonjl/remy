//
//  TransformsView.swift
//  Dictate Anywhere
//
//  "Transforms" page: saved prompts that rewrite selected text in place.
//

import SwiftUI

struct TransformsView: View {
    @Environment(AppState.self) private var appState

    @State private var activeRecorder: UUID?
    @State private var shouldResumeMonitoringAfterRecording = false
    @State private var shortcutErrors: [UUID: String] = [:]

    var body: some View {
        let store = appState.transforms

        DSPage {
            DSSectionHeader(
                title: "Transforms",
                subtitle: "Select text in any app and press a shortcut. Remy rewrites the selection in place with your Transcript Cleanup provider."
            )

            providerSection

            ForEach(store.transforms) { transform in
                DSSection(overline: transform.builtIn == nil ? "Custom" : "Built-in") {
                    TransformEditor(
                        transform: transform,
                        shortcutError: shortcutErrors[transform.id],
                        isRecording: activeRecorder == transform.id,
                        onRecord: { keyCode, modifiers, displayName in
                            record(transform: transform, keyCode: keyCode, modifiers: modifiers, displayName: displayName)
                        },
                        onClear: {
                            shortcutErrors[transform.id] = nil
                            store.clearShortcut(id: transform.id)
                            appState.hotkeyService.restartMonitoring()
                        },
                        onRecordingStarted: {
                            activeRecorder = transform.id
                            shouldResumeMonitoringAfterRecording = appState.hotkeyService.isMonitoring
                            appState.hotkeyService.stopMonitoring()
                        },
                        onRecordingStopped: {
                            activeRecorder = nil
                            if shouldResumeMonitoringAfterRecording || appState.permissions.accessibilityGranted {
                                appState.hotkeyService.restartMonitoring()
                            }
                            shouldResumeMonitoringAfterRecording = false
                        }
                    )
                    .disabled(activeRecorder != nil && activeRecorder != transform.id)
                }
            }

            if store.canAddCustom {
                DSAddButton(title: "Add custom transform") {
                    store.addCustom()
                }
                .disabled(activeRecorder != nil)
            }
            DSCardCaption(text: "\(store.customCount) of \(TransformStore.maximumCustomTransforms) custom transforms.")

            DSHint(
                text: "Transforms accept 1–\(TransformLimits.maximumWords.formatted()) selected words. Your cancel shortcut stops a running transform; use ⌘Z in the app to undo a replacement. If focus moves before the result is ready, Remy copies it instead of pasting. Transforms aren't saved to History.",
                icon: "keyboard"
            )
        }
    }

    @ViewBuilder
    private var providerSection: some View {
        let mode = appState.settings.transcriptPostProcessingMode
        DSSection(overline: "Provider") {
            if let notice = TransformProviderRoute.modeNotice(mode) {
                DSDetailRow(label: notice.title, caption: notice.detail) {
                    Button("Open Transcript Cleanup") { appState.selectedPage = .aiPostProcessing }
                        .buttonStyle(.dsSecondary)
                }
            } else {
                DSDetailRow(
                    label: mode.displayName,
                    caption: mode == .appleIntelligence
                        ? "Runs on this Mac. The selected text and transform instructions never leave it."
                        : "Running a transform sends the selected text and its instructions to this provider. A local endpoint keeps them on this Mac; remote endpoints must use HTTPS."
                ) {
                    Button("Change") { appState.selectedPage = .aiPostProcessing }
                        .buttonStyle(.dsSecondary)
                }
            }
        }
    }

    private func record(transform: TextTransform, keyCode: UInt16?, modifiers: HotkeyModifiers, displayName: String) {
        let settings = appState.settings
        var candidate = HotkeyBinding(id: transform.id, keyCode: keyCode, modifiersRawValue: 0,
                                      displayName: displayName, mode: .handsFreeToggle)
        candidate.modifiers = modifiers
        if let conflict = ConflictDetector.transformShortcutConflict(
            candidate, transformID: transform.id, transforms: appState.transforms.transforms,
            dictation: settings.hotkeyBindings, cancellation: settings.cancelShortcut) {
            shortcutErrors[transform.id] = conflict
            return
        }
        shortcutErrors[transform.id] = nil
        appState.transforms.setShortcut(id: transform.id, keyCode: keyCode, modifiers: modifiers, displayName: displayName)
        appState.hotkeyService.restartMonitoring()
        shouldResumeMonitoringAfterRecording = false
    }
}

// MARK: - Transform editor

private struct TransformEditor: View {
    @Environment(AppState.self) private var appState
    let transform: TextTransform
    let shortcutError: String?
    let isRecording: Bool
    let onRecord: (UInt16?, HotkeyModifiers, String) -> Void
    let onClear: () -> Void
    let onRecordingStarted: () -> Void
    let onRecordingStopped: () -> Void

    @State private var isConfirmingDelete = false

    private var store: TransformStore { appState.transforms }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 14) {
                if transform.builtIn == nil {
                    DSTextField(
                        placeholder: "Transform name",
                        text: Binding(get: { transform.name }, set: { store.setName($0, id: transform.id) }),
                        accessibilityName: "Transform name"
                    )
                } else {
                    Text(transform.displayName)
                        .font(DS.Fonts.ui(13.5, .medium))
                        .foregroundStyle(DS.Colors.ink)
                }
                Spacer(minLength: 0)
                DSSwitch(
                    accessibilityName: "Enable \(transform.displayName)",
                    isOn: Binding(get: { transform.isEnabled }, set: { enabled in
                        store.setEnabled(enabled, id: transform.id)
                        appState.hotkeyService.restartMonitoring()
                    })
                )
                .disabled(isRecording)
            }
            .padding(16)

            DSDivider()

            DSDetailRow(label: "Shortcut", caption: "Select text in another app, then press this shortcut.") {
                ShortcutRecorderView(
                    accessibilityName: "\(transform.displayName) shortcut",
                    displayName: transform.shortcut?.displayName ?? "",
                    onRecord: onRecord,
                    onClear: onClear,
                    onRecordingStarted: onRecordingStarted,
                    onRecordingStopped: onRecordingStopped
                )
            }
            ForEach(shortcutMessages, id: \.text) { message in
                DSFieldMessage(text: message.text, tone: message.tone)
                    .padding(.horizontal, DS.Spacing.rowHorizontal)
                    .padding(.bottom, 10)
            }

            DSDivider()

            VStack(alignment: .leading, spacing: 10) {
                SettingsMultilineTextArea(
                    text: Binding(get: { transform.prompt }, set: { store.setPrompt($0, id: transform.id) }),
                    label: "\(transform.displayName) instructions",
                    placeholder: "Describe how to rewrite the selected text."
                )
                HStack {
                    Spacer(minLength: 0)
                    if transform.builtIn != nil {
                        Button("Reset to default") { store.resetPrompt(id: transform.id) }
                            .buttonStyle(.dsSecondary)
                            .disabled(!transform.hasCustomizedPrompt || isRecording)
                    } else {
                        Button("Delete transform") { isConfirmingDelete = true }
                            .buttonStyle(.dsSecondary)
                            .disabled(isRecording)
                    }
                }
            }
            .padding(16)
        }
        .confirmationDialog("Delete \u{201C}\(transform.displayName)\u{201D}?", isPresented: $isConfirmingDelete) {
            Button("Delete", role: .destructive) {
                store.removeCustom(id: transform.id)
                appState.hotkeyService.restartMonitoring()
            }
        } message: {
            Text("Its instructions and shortcut are removed.")
        }
    }

    /// F1–F20 do not type characters, so they are not warned about.
    private static let functionKeyCodes: Set<UInt16> = [
        122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80, 90,
    ]

    private var shortcutMessages: [(text: String, tone: DSFieldMessage.Tone)] {
        var messages: [(text: String, tone: DSFieldMessage.Tone)] = []
        if let shortcutError { messages.append((shortcutError, .error)) }
        if let shortcut = transform.shortcut {
            if let warning = ConflictDetector.systemConflict(for: shortcut) { messages.append((warning, .warning)) }
            if let keyCode = shortcut.keyCode, !Self.functionKeyCodes.contains(keyCode),
               Settings.deviceIndependentModifiers(from: shortcut.modifiers).subtracting([.option, .shift]).isEmpty {
                messages.append(("This combination normally types a character. Remy will intercept it in every app.", .warning))
            }
        }
        return messages
    }
}
