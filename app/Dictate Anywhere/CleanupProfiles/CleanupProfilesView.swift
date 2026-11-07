import SwiftUI

/// Explicit editor boundary. Drafts never resolve stored credentials or contact a provider.
@MainActor
struct CleanupProfilesView: View {
    @Bindable var controller: CleanupProfileController
    /// Saved-profile synthetic check only. nil means success; any string means failure.
    /// Returned strings are deliberately never rendered (they may contain provider details).
    var testConnection: (@MainActor (CleanupProfile) async -> String?)? = nil

    @State private var draft = Draft()
    @State private var message: String?
    @State private var confirmingDelete = false
    @State private var checking = false
    @State private var checkID: UUID?
    @State private var connectionTask: Task<Void, Never>?

    private struct Draft: Equatable {
        var base: CleanupProfile?
        var id = UUID()
        var name = "New profile"
        var adapter = CleanupAdapterKind.openAICompatible
        var baseURL = "https://ollama.com/v1"
        var model = ""
        var prompt = Settings.recommendedTranscriptCleanupPrompt
        var timeout = 30.0
        var newKey = ""
        var clearCredential = false

        init() {}
        init(_ profile: CleanupProfile) {
            base = profile; id = profile.id; name = profile.name; adapter = profile.adapter
            baseURL = profile.baseURL; model = profile.model; prompt = profile.prompt
            timeout = profile.timeoutSeconds
        }
        var unchanged: Bool { base.map { self == Draft($0) } ?? false }
        var keepsCredential: Bool {
            !clearCredential && baseURL == base?.baseURL && base?.credential != nil
        }
        func profile() throws -> CleanupProfile {
            guard (base?.revision ?? 0) < UInt64.max,
                  prompt == base?.prompt || (base?.promptVersion ?? 0) < Int.max else {
                throw CleanupProfileController.ControllerError.revisionExhausted
            }
            return CleanupProfile(id: id, revision: (base?.revision ?? 0) + 1,
                name: name, adapter: adapter, baseURL: baseURL, model: model, prompt: prompt,
                promptVersion: base.map { prompt == $0.prompt ? $0.promptVersion : $0.promptVersion + 1 } ?? 1,
                timeoutSeconds: timeout,
                capabilities: base.flatMap { $0.adapter == adapter ? $0.capabilities : nil }
                    ?? .init(transport: .completeJSON, discovery: false, structuredOutput: false, thinkingControl: false),
                routePolicy: base?.routePolicy ?? .ordinary,
                allowInsecureRemoteHTTP: baseURL == base?.baseURL ? (base?.allowInsecureRemoteHTTP ?? false) : false,
                credential: keepsCredential ? base?.credential : nil)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            DSSectionHeader(title: "Cleanup profiles", subtitle: "Give each service its own endpoint, model and instructions.")
            DSCard {
                VStack(alignment: .leading, spacing: 14) {
                    HStack {
                        Picker("Saved profile", selection: Binding<UUID?>(
                            get: { draft.base?.id }, set: select)) {
                            Text("Unsaved profile").tag(UUID?.none)
                            ForEach(controller.collection.profiles) { profile in
                                Text(profile.name).tag(Optional(profile.id))
                            }
                        }
                        Button("New") { reset(nil) }.buttonStyle(.dsSecondary)
                        Button("Reload") {
                            do { try controller.load(); reset(controller.selectedProfile) }
                            catch { message = "Profiles could not be reloaded. Your draft has been kept." }
                        }.buttonStyle(.dsSecondary)
                    }
                    Text("Switching profiles, New and Reload discard unsaved changes.")
                        .font(DS.Fonts.ui(12)).foregroundStyle(DS.Colors.textSecondary)
                    labeledField("Profile name", text: $draft.name)
                    Picker("Protocol", selection: $draft.adapter) {
                        Text("Ollama").tag(CleanupAdapterKind.ollama)
                        Text("OpenAI-compatible").tag(CleanupAdapterKind.openAICompatible)
                    }
                    labeledField("Base URL", text: $draft.baseURL)
                        .help("Enter the exact service URL. Remy does not add /v1. Remote endpoints require HTTPS.")
                    labeledField("Model ID", text: $draft.model)
                        .help("Use the exact model identifier accepted by your service.")
                    Text(draft.keepsCredential ? "API key: configured" : "API key: not configured")
                        .font(DS.Fonts.ui(12)).foregroundStyle(DS.Colors.textSecondary)
                    SecureField("New API key (optional)", text: $draft.newKey)
                        .accessibilityLabel("New API key")
                        .help("Stored keys are never displayed. Leave this empty to preserve the current endpoint's key reference.")
                    if draft.base?.credential != nil {
                        Toggle("Remove key from this profile on Save", isOn: $draft.clearCredential)
                        if draft.baseURL != draft.base?.baseURL {
                            Text("Changing the endpoint clears its saved key reference. Enter a new key if required.")
                                .font(DS.Fonts.ui(12)).foregroundStyle(DS.Colors.textSecondary)
                        }
                    }
                    Text("Cleanup instructions").font(DS.Fonts.ui(13, .semibold))
                    TextEditor(text: $draft.prompt)
                        .font(DS.Fonts.ui(13)).frame(minHeight: 140)
                        .accessibilityLabel("Cleanup instructions")
                        .overlay(RoundedRectangle(cornerRadius: 5).stroke(DS.Colors.border))
                    HStack {
                        Text("Request timeout")
                        Stepper(value: $draft.timeout, in: 1...120, step: 1) {
                            Text("\(Int(draft.timeout)) seconds").monospacedDigit()
                        }
                    }
                    Text(routeDescription)
                        .font(DS.Fonts.ui(12)).foregroundStyle(DS.Colors.textSecondary)
                    HStack(spacing: 10) {
                        Button("Save profile", action: save).buttonStyle(.dsPrimary)
                        Button(checking ? "Checking…" : "Test connection", action: check)
                            .buttonStyle(.dsSecondary)
                            .disabled(testConnection == nil || !draft.unchanged || checking)
                            .help("Checks only the saved profile using a synthetic input. Save changes first.")
                        Spacer()
                        if draft.base != nil {
                            Button("Delete…") { confirmingDelete = true }.buttonStyle(.dsDestructive)
                        }
                    }
                    if let message {
                        Text(message).font(DS.Fonts.ui(12)).foregroundStyle(DS.Colors.textSecondary)
                            .accessibilityLabel(message)
                    }
                }.padding(18)
            }
        }
        .textFieldStyle(.roundedBorder)
        .tint(DS.Colors.accentDeep)
        .task {
            do { try controller.load(); reset(controller.selectedProfile) }
            catch { message = "Profiles could not be loaded. No changes were saved." }
        }
        .onDisappear { checkID = nil; connectionTask?.cancel(); connectionTask = nil; draft.newKey = "" }
        .confirmationDialog("Delete this cleanup profile?", isPresented: $confirmingDelete) {
            Button("Delete profile", role: .destructive, action: remove)
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The profile will be removed. Its stored key is not deleted.")
        }
    }

    private func labeledField(_ title: String, text: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(DS.Fonts.ui(13, .semibold))
            DSTextField(placeholder: title, text: text, accessibilityName: title)
        }
    }

    private var routeDescription: String {
        let host = URLComponents(string: draft.baseURL)?.host?.lowercased() ?? ""
        if ["localhost", "127.0.0.1", "::1", "[::1]"].contains(host) {
            return "Local execution has not been verified. A server on this Mac may still use a cloud model."
        }
        return "This server receives your transcript and vocabulary for cleanup."
    }

    private func reset(_ profile: CleanupProfile?) {
        connectionTask?.cancel(); connectionTask = nil
        checkID = nil; checking = false; message = nil
        draft = profile.map(Draft.init) ?? Draft()
    }
    private func select(_ id: UUID?) {
        guard let id else { reset(nil); return }
        do {
            try controller.select(id: id)
            reset(controller.selectedProfile)
        } catch { message = "The profile could not be selected. Reload profiles and try again." }
    }
    private func save() {
        do {
            let profile = try draft.profile()
            try controller.save(profile: profile, credentialValue: draft.newKey.isEmpty ? nil : draft.newKey)
            reset(controller.selectedProfile)
            message = "Profile saved."
        } catch {
            // Keep draft fields for correction, but never retain a failed secret entry.
            draft.newKey = ""
            if error as? CleanupProfileController.ControllerError == .stateUnknown {
                message = "The save could not be confirmed. Reload profiles before making another change."
            } else {
                message = "The profile could not be saved. Check its fields, or reload if it changed elsewhere."
            }
        }
    }
    private func remove() {
        guard let id = draft.base?.id else { return }
        do { try controller.remove(id: id); reset(controller.selectedProfile) }
        catch { message = "The profile could not be deleted. Reload profiles and try again." }
    }
    private func check() {
        guard !checking, draft.unchanged, let base = draft.base,
              let saved = controller.collection.profiles.first(where: { $0 == base }),
              let testConnection else { return }
        let token = UUID(); checkID = token; checking = true; message = nil
        connectionTask = Task { @MainActor in
            let failure = await testConnection(saved)
            guard checkID == token else { return }
            checking = false
            connectionTask = nil
            guard draft.unchanged, draft.base == saved else {
                message = "The saved profile check finished. Save your changes before testing this draft."
                return
            }
            message = failure == nil ? "Connection check passed." : "Connection check failed. Check the saved endpoint, model and key."
        }
    }
}
