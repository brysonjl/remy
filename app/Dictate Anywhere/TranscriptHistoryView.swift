//
//  TranscriptHistoryView.swift
//  Dictate Anywhere
//
//  "History" page: local transcript history.
//

import AppKit
import SwiftUI

struct TranscriptHistoryView: View {
    @Environment(AppState.self) private var appState

    @State private var searchText = ""
    @State private var showClearAllConfirm = false
    @State private var retryingDeletion = false

    /// Uses the Mac's date order and preferred 12- or 24-hour clock.
    static let timestampFormat = Date.FormatStyle(date: .abbreviated, time: .shortened)

    static func filteredEntries(
        _ entries: [TranscriptHistoryEntry],
        searchText: String
    ) -> [TranscriptHistoryEntry] {
        let query = searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return entries }
        return entries.filter {
            $0.text.localizedCaseInsensitiveContains(query)
                || $0.rawText?.localizedCaseInsensitiveContains(query) == true
        }
    }

    var body: some View {
        @Bindable var settings = appState.settings
        let query = searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        let entries = Self.filteredEntries(
            Array(appState.historyEntries.reversed()),
            searchText: query
        )
        let sessions = Dictionary(uniqueKeysWithValues: (appState.history?.entries ?? []).map { ($0.id, $0) })
        let cancelledEntries = appState.recoveryStore.entries.filter {
            query.isEmpty || $0.preview.localizedCaseInsensitiveContains(query)
                || "Cancelled dictation".localizedCaseInsensitiveContains(query)
        }

        DSPage(spacing: 20) {
            DSSectionHeader(
                title: "History",
                subtitle: "Your recent dictations, kept on this Mac."
            )

            DSSection(overline: "History storage") {
                DSHint(text: "With history on, a temporary audio copy protects interrupted or failed dictations. Successful recordings are removed; recovery audio expires after 24 hours.", icon: "waveform")
                    .padding(DS.Spacing.rowHorizontal)
                DSDetailRow(label: "Save new dictations", caption: "Changes apply to your next recording. Turning this off keeps existing history and disables recovery audio for new sessions.") {
                    DSSwitch(accessibilityName: "Save new dictations", isOn: $settings.historyEnabled)
                }
                if !settings.historyEnabled {
                    DSHint(text: "History off. New transcripts stay in memory for delivery and cannot be recovered after Remy quits.", icon: "clock.badge.xmark")
                        .padding(DS.Spacing.rowHorizontal)
                }
                if appState.historyNotSaved {
                    DSFieldMessage(text: "Some history changes were not saved. Copy any unsaved text you need before quitting Remy. A paste that was already sent is never automatically retried.", tone: .warning)
                        .padding(DS.Spacing.rowHorizontal)
                }
                if let message = appState.storedHistoryDeletionError {
                    DSFieldMessage(text: message, tone: .warning)
                        .padding(DS.Spacing.rowHorizontal)
                }
                if let history = appState.history,
                   history.pendingClear || !history.pendingDeletionIDs.isEmpty {
                    VStack(alignment: .leading, spacing: 10) {
                        DSFieldMessage(
                            text: "Deleted history is hidden for this session, but its deletion from disk is not confirmed. It may return after Remy restarts.",
                            tone: .warning)
                        Button(retryingDeletion ? "Retrying deletion…" : "Retry deletion from disk") {
                            retryingDeletion = true
                            Task {
                                _ = await history.retryPendingDeletions()
                                retryingDeletion = false
                            }
                        }
                        .buttonStyle(.dsSecondary)
                        .disabled(retryingDeletion)
                        .help("Retries the earlier deletion. Dictations saved after Clear All are kept.")
                    }
                    .padding(DS.Spacing.rowHorizontal)
                }
                if appState.isPreparingHistory {
                    DSHint(text: "Preparing your history…", icon: "clock")
                        .padding(DS.Spacing.rowHorizontal)
                }
            }

            HStack(spacing: 10) {
                DSSearchField(
                    placeholder: "Search your dictations", text: $searchText,
                    accessibilityName: "Search transcript history"
                )
                Button("Clear All…") {
                    showClearAllConfirm = true
                }
                .buttonStyle(.dsDestructive)
                .disabled(appState.isPreparingHistory || appState.isDeletingStoredHistory)
                .disabled(appState.recoveringEntryID != nil || appState.continuingEntryID != nil)
            }

            if !cancelledEntries.isEmpty {
                DSSection(overline: "Recovery sessions") {
                    DSHint(
                        text: "Continue restores your words and resumes recording. Stop finishes the combined dictation; if the original app is unavailable, the text is copied. Recover text saves it here without pasting. Sessions expire after 24 hours.",
                        icon: "arrow.counterclockwise"
                    )
                    .padding(.vertical, 12)
                    .padding(.horizontal, DS.Spacing.rowHorizontal)
                    ForEach(cancelledEntries) { entry in
                        DSDivider()
                        HStack(alignment: .top, spacing: 16) {
                            VStack(alignment: .leading, spacing: 5) {
                                Text("Saved for recovery · \(entry.createdAt.formatted(Self.timestampFormat))")
                                    .font(DS.Fonts.ui(12, .semibold))
                                    .foregroundStyle(DS.Colors.ink)
                                Text(entry.preview.isEmpty ? "Audio saved for recovery" : entry.preview)
                                    .font(DS.Fonts.ui(13))
                                    .foregroundStyle(DS.Colors.textSecondary)
                                    .lineLimit(3)
                                Text("\(Int(entry.duration)) sec · Expires in \(entry.expiresAt, style: .relative)")
                                    .font(DS.Fonts.ui(11.5))
                                    .foregroundStyle(DS.Colors.textSecondary)
                                if entry.captureError != nil || (!entry.hasAudio && entry.completedTranscript == nil && entry.transcriptPrefix == nil) {
                                    DSFieldMessage(
                                        text: "Partial recovery copy — some audio may be unavailable.",
                                        tone: .warning
                                    )
                                }
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                            VStack(spacing: 8) {
                                let continueTitle = appState.continuingEntryID == entry.id
                                    ? (appState.recoveringEntryID == entry.id ? "Preparing…"
                                       : (appState.status == .recording ? "Stop" : "Finishing…"))
                                    : "Continue"
                                let recoverTitle = appState.recoveringEntryID == entry.id && appState.continuingEntryID == nil
                                    ? "Recovering…" : "Recover text"
                                Button(continueTitle) {
                                    Task {
                                        if appState.continuingEntryID == entry.id { await appState.stopDictation() }
                                        else { await appState.continueCancelledDictation(entry) }
                                    }
                                }
                                .buttonStyle(.dsPrimary)
                                .accessibilityLabel("\(continueTitle) recovery session from \(entry.createdAt.formatted(Self.timestampFormat))")
                                .disabled(appState.continuingEntryID == entry.id
                                          ? !appState.canStopDictation : appState.status != .idle)
                                Button(recoverTitle) {
                                    Task { await appState.recoverCancelledDictation(entry) }
                                }
                                .buttonStyle(.dsSecondary)
                                .accessibilityLabel("\(recoverTitle) from recovery session on \(entry.createdAt.formatted(Self.timestampFormat))")
                                .disabled(appState.status != .idle)
                            }
                            DSIconButton(
                                systemImage: "trash",
                                accessibilityLabel: "Delete cancelled session from \(entry.createdAt.formatted(Self.timestampFormat))"
                            ) {
                                do { try appState.recoveryStore.remove(id: entry.id) }
                                catch { appState.recoveryStore.errorMessage = error.localizedDescription }
                            }
                            .disabled(appState.recoveringEntryID == entry.id || appState.continuingEntryID == entry.id)
                        }
                        .padding(16)
                    }
                }
            }

            if entries.isEmpty && cancelledEntries.isEmpty {
                DSCard {
                    DSEmptyState(
                        systemImage: "clock.arrow.circlepath",
                        title: query.isEmpty ? "No transcripts" : "No matches",
                        message: query.isEmpty
                            ? "Completed dictations will appear here."
                            : "No dictations match “\(query)”."
                    )
                    .padding(.vertical, 20)
                }
            } else if !entries.isEmpty {
                DSCard {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(Array(entries.enumerated()), id: \.element.id) { index, entry in
                            if index > 0 {
                                DSDivider()
                            }
                            TranscriptHistoryRow(
                                entry: entry,
                                session: sessions[entry.id],
                                history: appState.history,
                                onCopy: copyToPasteboard,
                                onDelete: { Task { await appState.removeHistoryEntry(id: entry.id) } }
                            )
                        }
                    }
                }
            }
        }
        .alert("Clear all stored history?", isPresented: $showClearAllConfirm) {
            Button("Clear All", role: .destructive) {
                Task {
                    await appState.clearAllStoredHistory()
                }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Delete this app's saved transcripts, recovery recordings and history rollback backups. This removes history rollback options. Models, settings and API keys are kept. Copies you exported, the clipboard and system backups are not cleared. This cannot be undone.")
        }
        .task {
            await appState.history?.refresh()
            do { try appState.recoveryStore.reload() }
            catch { appState.recoveryStore.errorMessage = error.localizedDescription }
        }
    }

    private func copyToPasteboard(_ text: String) -> Bool {
        NSPasteboard.general.clearContents()
        return NSPasteboard.general.setString(text, forType: .string)
    }
}

private struct TranscriptHistoryRow: View {
    private enum CopiedKind { case transcript, raw }

    let entry: TranscriptHistoryEntry
    let session: RemyHistorySession?
    let history: RemyHistoryController?
    let onCopy: (String) -> Bool
    let onDelete: () -> Void
    @State private var copiedKind: CopiedKind?
    @State private var copyResetTask: Task<Void, Never>?
    @State private var detailsExpanded = false

    var body: some View {
        HStack(alignment: .top, spacing: 16) {
            VStack(alignment: .leading, spacing: 5) {
                Text(entry.createdAt.formatted(TranscriptHistoryView.timestampFormat))
                    .font(DS.Fonts.ui(11.5, .semibold))
                    .tracking(0.2)
                    .foregroundStyle(DS.Colors.textSecondary)
                if let warning = TranscriptProcessingDetailsText.warning(for: session) {
                    DSFieldMessage(text: warning, tone: .warning)
                }
                Text(entry.text)
                    .font(DS.Fonts.ui(13.5))
                    .lineSpacing(13.5 * 0.55 - 4)
                    .foregroundStyle(DS.Colors.ink)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
                if let rawText = entry.rawText {
                    DisclosureGroup("Raw transcript") {
                        VStack(alignment: .leading, spacing: 8) {
                            Text(rawText)
                                .font(DS.Fonts.ui(13))
                                .foregroundStyle(DS.Colors.textSecondary)
                                .textSelection(.enabled)
                                .frame(maxWidth: .infinity, alignment: .leading)
                            DSInsetButton(
                                title: copiedKind == .raw ? "Copied raw" : "Copy raw",
                                systemImage: "doc.on.doc"
                            ) {
                                copy(rawText, kind: .raw)
                            }
                            .accessibilityLabel(copiedKind == .raw
                                ? "Copied raw transcript"
                                : "Copy raw transcript from \(entry.createdAt.formatted(TranscriptHistoryView.timestampFormat))")
                        }
                        .padding(.top, 6)
                    }
                    .font(DS.Fonts.ui(12, .medium))
                }
                DisclosureGroup("Processing details", isExpanded: $detailsExpanded) {
                    if detailsExpanded {
                        TranscriptProcessingDetails(session: session, history: history)
                            .padding(.top, 8)
                    }
                }
                .font(DS.Fonts.ui(12, .medium))
                .padding(.top, 4)
            }

            HStack(spacing: 6) {
                DSInsetButton(
                    title: copiedKind == .transcript ? "Copied" : "Copy",
                    systemImage: "doc.on.doc"
                ) {
                    copy(entry.text, kind: .transcript)
                }
                .accessibilityLabel(copiedKind == .transcript
                    ? "Copied transcript"
                    : "Copy transcript from \(entry.createdAt.formatted(TranscriptHistoryView.timestampFormat))")
                DSIconButton(
                    systemImage: "trash",
                    accessibilityLabel: "Delete transcript from \(entry.createdAt.formatted(TranscriptHistoryView.timestampFormat))",
                    action: onDelete
                )
                .help("Delete transcript")
            }
        }
        .padding(.vertical, 14)
        .padding(.horizontal, DS.Spacing.rowHorizontal)
        .onDisappear { copyResetTask?.cancel() }
    }

    private func copy(_ text: String, kind: CopiedKind) {
        guard onCopy(text) else { return }
        copiedKind = kind
        copyResetTask?.cancel()
        copyResetTask = Task {
            try? await Task.sleep(for: .seconds(1.5))
            guard !Task.isCancelled else { return }
            copiedKind = nil
        }
    }
}

/// Human-readable labels deliberately avoid guessing why an older record lacks an AI pass.
enum TranscriptProcessingDetailsText {
    static let noAttempts = "No AI pass recorded. Older records may not include processing details."

    static func warning(for session: RemyHistorySession?) -> String? {
        guard let session else { return nil }
        if session.selectionKind == .fallback {
            return "AI cleanup wasn't applied. Raw transcript used."
        }
        if session.status == .interrupted {
            return "Processing was interrupted. Review the details below."
        }
        return nil
    }

    static func status(_ status: RemyHistorySession.Status) -> String {
        switch status {
        case .recognized: "Transcription saved; processing not completed"
        case .completed: "Processing completed"
        case .interrupted: "Processing interrupted"
        }
    }

    static func selection(_ kind: RemyHistorySession.SelectionKind) -> String {
        switch kind {
        case .raw: "Raw transcript selected"
        case .attempt: "AI pass output selected"
        case .fallback: "Raw transcript selected instead of AI cleanup"
        case .legacy: "Imported transcript; original processing details unavailable"
        case .recovered: "Recovered text selected"
        }
    }

    static func attemptStatus(_ status: RemyHistoryAttempt.Status, reason: String? = nil) -> String {
        if reason == RemyCleanupFailure.useRaw.rawValue { return "Skipped by you" }
        return switch status {
        case .succeeded: "Succeeded"
        case .failed: "Failed"
        case .cancelled: "Cancelled"
        }
    }

    static func deliveryOutcome(_ outcome: RemyHistoryDelivery.Outcome) -> String {
        switch outcome {
        case .intent: "Delivery intent recorded; no result recorded"
        case .copied: "Copied to clipboard"
        case .dispatched: "Paste sent; insertion not verified"
        case .verified: "Insertion verified"
        case .uncertain: "Paste may have been sent; insertion uncertain"
        case .failed: "Delivery failed"
        }
    }

    static func duration(_ milliseconds: Int?) -> String {
        guard let milliseconds else { return "Not recorded" }
        if milliseconds < 1_000 { return "\(milliseconds) ms" }
        return "\((Double(milliseconds) / 1_000).formatted(.number.precision(.fractionLength(2)))) s"
    }
}

private struct TranscriptProcessingDetails: View {
    let session: RemyHistorySession?
    let history: RemyHistoryController?
    @State private var attempts: [RemyHistoryAttempt] = []
    @State private var deliveries: [RemyHistoryDelivery] = []
    @State private var isLoading = true
    @State private var loadFailed = false
    @State private var retryCount = 0

    private struct LoadRequest: Equatable {
        let session: RemyHistorySession?
        let retryCount: Int
    }

    static let preciseTimestamp = Date.FormatStyle(date: .abbreviated, time: .standard)

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let session {
                VStack(alignment: .leading, spacing: 4) {
                    metadata("Status", TranscriptProcessingDetailsText.status(session.status))
                    metadata("Selected text", TranscriptProcessingDetailsText.selection(session.selectionKind))
                    if let engine = session.engine { metadata("Transcription engine", engine) }
                    if let model = session.model { metadata("Transcription model", model) }
                    if let language = session.language { metadata("Language", language) }
                    if let finishedAt = session.finishedAt {
                        metadata("Finished", finishedAt.formatted(Self.preciseTimestamp))
                    }
                }
            }

            if isLoading {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Loading processing details…")
                }
            } else if loadFailed {
                VStack(alignment: .leading, spacing: 8) {
                    DSFieldMessage(text: "Processing details couldn't be loaded. Retry to read this session's log.", tone: .warning)
                    Button("Retry loading details") { retryCount += 1 }
                        .buttonStyle(.dsSecondary)
                }
            } else {
                if attempts.isEmpty {
                    Text(TranscriptProcessingDetailsText.noAttempts)
                        .foregroundStyle(DS.Colors.textSecondary)
                }
                ForEach(attempts, id: \.id) { attempt in
                    attemptDetails(attempt)
                }
                DSDivider()
                Text("Delivery")
                    .font(DS.Fonts.ui(12, .semibold))
                    .foregroundStyle(DS.Colors.ink)
                if deliveries.isEmpty {
                    Text("No delivery result recorded. Older records may not include delivery details.")
                        .foregroundStyle(DS.Colors.textSecondary)
                }
                ForEach(deliveries, id: \.id) { delivery in
                    VStack(alignment: .leading, spacing: 6) {
                        Text(TranscriptProcessingDetailsText.deliveryOutcome(delivery.outcome))
                            .font(DS.Fonts.ui(12, .semibold))
                            .foregroundStyle(DS.Colors.ink)
                        metadata("Started", delivery.createdAt.formatted(Self.preciseTimestamp))
                        textDisclosure("Delivery text", text: delivery.payload)
                    }
                }
                if history != nil, session != nil {
                    DSInsetButton(title: "Refresh details", systemImage: "arrow.clockwise") {
                        retryCount += 1
                    }
                    .help("Reload processing and delivery results for this dictation.")
                }
            }
        }
        .font(DS.Fonts.ui(12))
        .frame(maxWidth: .infinity, alignment: .leading)
        .task(id: LoadRequest(session: session, retryCount: retryCount)) {
            await loadDetails()
        }
    }

    private func attemptDetails(_ attempt: RemyHistoryAttempt) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            DSDivider()
            HStack(spacing: 8) {
                Text("AI pass \(attempt.index) · \(TranscriptProcessingDetailsText.attemptStatus(attempt.status, reason: attempt.failureCategory))")
                    .font(DS.Fonts.ui(12, .semibold))
                if session?.selectedAttemptID == attempt.id {
                    Text("Selected")
                        .font(DS.Fonts.ui(11, .semibold))
                        .foregroundStyle(DS.Colors.successText)
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(DS.Colors.successSoft, in: RoundedRectangle(cornerRadius: 4))
                }
            }
            .foregroundStyle(DS.Colors.ink)
            if attempt.status != .succeeded {
                DSFieldMessage(text: RemyCleanupFailure.message(for: attempt.failureCategory), tone: .warning)
            }
            VStack(alignment: .leading, spacing: 4) {
                metadata("Provider", attempt.provider ?? "Not recorded")
                metadata("Model", attempt.model ?? "Not recorded")
                if let startedAt = attempt.startedAt {
                    metadata("Started", startedAt.formatted(Self.preciseTimestamp))
                } else {
                    metadata("Started", "Not recorded")
                }
                metadata("Recorded", attempt.createdAt.formatted(Self.preciseTimestamp))
                metadata("Duration", TranscriptProcessingDetailsText.duration(attempt.durationMilliseconds))
                if let profile = attempt.profile {
                    metadata("Cleanup mode", profile.cleanupMode)
                    metadata("Reasoning", profile.reasoningEnabled ? "On" : "Off")
                    metadata("App context setting", profile.contextEnabled ? "On" : "Off")
                    if profile.cleanupMode == TranscriptPostProcessingMode.s1Mini.rawValue {
                        if let style = profile.style { metadata("S1 style", style) }
                        if let structure = profile.structure { metadata("S1 structure", structure) }
                    }
                }
            }
            textDisclosure("AI input", text: attempt.input)
            if let output = attempt.output {
                textDisclosure("AI output", text: output)
            } else {
                Text("No AI output recorded.")
                    .foregroundStyle(DS.Colors.textSecondary)
            }
            if let prompt = attempt.prompt {
                textDisclosure("Saved cleanup instructions", text: prompt,
                               caption: "Generated formatting instructions and app context aren't fully captured in this log.")
            } else {
                Text("AI instructions not recorded.")
                    .foregroundStyle(DS.Colors.textSecondary)
            }
        }
    }

    private func metadata(_ label: String, _ value: String) -> some View {
        (Text("\(label): ").fontWeight(.medium) + Text(value))
            .foregroundStyle(DS.Colors.textSecondary)
            .textSelection(.enabled)
            .fixedSize(horizontal: false, vertical: true)
    }

    private func textDisclosure(_ title: String, text: String, caption: String? = nil) -> some View {
        DisclosureGroup(title) {
            VStack(alignment: .leading, spacing: 6) {
                if let caption {
                    Text(caption)
                        .font(DS.Fonts.ui(11))
                }
                Text(text.isEmpty ? "(Empty)" : text)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .font(DS.Fonts.ui(12))
            .foregroundStyle(DS.Colors.textSecondary)
            .padding(.vertical, 5)
        }
    }

    private func loadDetails() async {
        isLoading = true
        loadFailed = false
        attempts = []
        deliveries = []
        guard let session, let history else {
            isLoading = false
            return
        }
        do {
            async let loadedAttempts = history.attempts(sessionID: session.id)
            async let loadedDeliveries = history.deliveries(sessionID: session.id)
            let result = try await (loadedAttempts, loadedDeliveries)
            // A collapse, refresh or deletion may invalidate this request while storage is reading.
            guard !Task.isCancelled, history.entries.contains(where: { $0.id == session.id }) else { return }
            attempts = result.0
            deliveries = result.1
            isLoading = false
        } catch {
            guard !Task.isCancelled, history.entries.contains(where: { $0.id == session.id }) else { return }
            isLoading = false
            loadFailed = true
        }
    }
}
