import SwiftUI
import AppKit
import UniformTypeIdentifiers

/// Per-event choices; the parent retains the existing master switch and volume.
struct SoundEffectsSettingsView: View {
    @Binding var startSound: String
    @Binding var completionSound: String
    var volume: Float
    var store: RemySoundEffects.Store? = nil
    @State private var message: String?
    @State private var isImporting = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            soundRow("Recording started", event: .start, selection: $startSound)
            DSDivider()
            soundRow("Dictation completed", event: .completion, selection: $completionSound)
            if let message {
                DSCardCaption(text: message)
                    .accessibilityLabel(message)
            }
            DSCardCaption(text: "Import a local WAV, AIFF, CAF, MP3 or M4A clip, up to 10 seconds and 10 MiB. Remy keeps its own copy; your original stays unchanged.")
        }
    }

    private func soundRow(_ title: String, event: RemySoundEffects.Event, selection: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            DSInfoRow(label: title) {
                Picker(title, selection: selection) {
                    Text("None").tag("none")
                    ForEach(RemySoundEffects.builtIns, id: \.self) { name in
                        Text(name).tag("builtin:" + name)
                    }
                    if !selection.wrappedValue.hasPrefix("builtin:"), selection.wrappedValue != "none" {
                        Text(RemySoundEffects.label(selection.wrappedValue)).tag(selection.wrappedValue)
                    }
                }
                .labelsHidden()
                .accessibilityLabel(title + " sound")
                .frame(maxWidth: 190)
            }
            HStack(spacing: 10) {
                Button("Import audio…") { chooseAudio(event: event) }
                    .buttonStyle(.dsSecondary)
                    .accessibilityLabel("Import " + title.lowercased() + " sound")
                    .disabled(isImporting)
                Button("Preview") {
                    let available = RemySoundEffects.play(identifier: selection.wrappedValue,
                        event: event, volume: volume, store: store)
                    message = available ? nil : "That sound is unavailable. Remy used the default sound instead."
                }
                .buttonStyle(.dsSecondary)
                .accessibilityLabel("Preview " + title.lowercased() + " sound")
                .disabled(isImporting || selection.wrappedValue == "none")
                if isImporting { ProgressView().controlSize(.small).accessibilityLabel("Importing audio") }
                Spacer()
            }
            .padding(.horizontal, DS.Spacing.rowHorizontal)
            .padding(.bottom, 14)
        }
        .tint(DS.Colors.accentDeep)
    }

    private func chooseAudio(event: RemySoundEffects.Event) {
        let panel = NSOpenPanel()
        panel.title = "Choose a short sound effect"
        panel.allowedContentTypes = [.audio]
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.canDownloadUbiquitousContents = false
        panel.canResolveUbiquitousConflicts = false
        panel.begin { response in
            guard response == .OK, let source = panel.url else { return }
            Task { @MainActor in
                isImporting = true
                let scoped = source.startAccessingSecurityScopedResource()
                defer { if scoped { source.stopAccessingSecurityScopedResource() }; isImporting = false }
                let localStore = store ?? .current
                do {
                    let identifier = try await Task.detached(priority: .userInitiated) {
                        try localStore.importAudio(from: source)
                    }.value
                    switch event {
                    case .start: startSound = identifier
                    case .completion: completionSound = identifier
                    }
                    message = "Sound imported. Use Preview to listen."
                } catch {
                    message = "Audio could not be imported. Choose a supported local clip up to 10 seconds and 10 MiB."
                }
            }
        }
    }
}
