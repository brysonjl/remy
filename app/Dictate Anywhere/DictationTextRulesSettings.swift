//
//  DictationTextRulesSettings.swift
//  Dictate Anywhere
//
//  Spelling-correction and snippet editors for the Transcript Cleanup page.
//

import SwiftUI
import AppKit
import UniformTypeIdentifiers

/// Heard-as spellings attached to custom vocabulary terms, plus CSV import.
struct VocabularyCorrectionsSection: View {
    @Environment(AppState.self) private var appState
    @State private var term = ""
    @State private var heardAs = ""
    @State private var editingTerm: String?
    @State private var importMessage: String?
    @State private var importFailed = false
    @State private var isImporting = false

    var body: some View {
        let corrections = appState.settings.vocabularyCorrections

        DSSection(overline: "Spelling Corrections") {
            ForEach(corrections, id: \.term) { correction in
                DSDetailRow(label: correction.term,
                            caption: "Heard as: " + correction.misspellings.joined(separator: ", ")) {
                    HStack(spacing: 6) {
                        DSIconButton(systemImage: "pencil",
                                     accessibilityLabel: "Edit correction for \(correction.term)") {
                            editingTerm = correction.term
                            term = correction.term
                            heardAs = correction.misspellings.joined(separator: ", ")
                        }
                        DSIconButton(systemImage: "trash",
                                     accessibilityLabel: "Remove correction for \(correction.term)") {
                            appState.settings.setVocabularyCorrection(term: correction.term, misspellings: [])
                            if editingTerm == correction.term { resetForm() }
                        }
                    }
                }
                DSDivider()
            }

            TextRulesCardContent {
                HStack(spacing: 8) {
                    DSTextField(placeholder: "Heard as (comma-separated)…", text: $heardAs,
                                accessibilityName: "Spellings to correct")
                        .frame(width: 220)
                        .onSubmit(save)
                    Image(systemName: "arrow.right")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(DS.Colors.textSecondary)
                        .accessibilityHidden(true)
                    DSTextField(placeholder: "Correct spelling…", text: $term,
                                accessibilityName: "Correct spelling")
                        .frame(width: 180)
                        .disabled(editingTerm != nil)
                        .onSubmit(save)
                    Button(editingTerm == nil ? "Add" : "Save", action: save)
                        .buttonStyle(.dsSecondary)
                        .disabled(!canSave)
                    if editingTerm != nil {
                        Button("Cancel", action: resetForm)
                            .buttonStyle(.dsSecondary)
                    }
                }
                if let lengthError {
                    DSFieldMessage(text: lengthError, tone: .error)
                }
                HStack(spacing: 10) {
                    Button("Import CSV…", action: importCSV)
                        .buttonStyle(.dsSecondary)
                        .disabled(isImporting)
                        .accessibilityLabel("Import vocabulary from a CSV file")
                    if let importMessage {
                        DSFieldMessage(text: importMessage, tone: importFailed ? .error : .success)
                    }
                }
            }

            DSDivider()
            DSCardCaption(text: "Replaces a spelling you name with the saved term, on this Mac, before any cleanup. Matching ignores case and only replaces whole words or phrases. Saved terms are also added to Custom Vocabulary. A CSV can list one term per row or misspelling,correction pairs (up to 1,000 rows, 3 MB).")
        }
    }

    private var misspellings: [String] {
        VocabularyInputParser.terms(from: heardAs, existingTerms: [])
    }

    private var lengthError: String? {
        let limit = DictationTextRules.maximumPhraseLength
        let tooLong = term.trimmingCharacters(in: .whitespacesAndNewlines).count > limit
            || misspellings.contains { $0.count > limit }
        return tooLong ? "Terms and spellings can be up to \(limit) characters." : nil
    }

    private var canSave: Bool {
        !term.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !misspellings.isEmpty && lengthError == nil
    }

    private func save() {
        guard canSave else { return }
        let settings = appState.settings
        // Adding to a term that already has corrections keeps its spellings;
        // editing replaces them.
        let existing = editingTerm == nil
            ? settings.customVocabulary.first { $0.caseInsensitiveCompare(term.trimmingCharacters(in: .whitespacesAndNewlines)) == .orderedSame }
                .flatMap { settings.vocabularyMisspellings[$0] } ?? []
            : []
        settings.setVocabularyCorrection(term: editingTerm ?? term, misspellings: existing + misspellings)
        resetForm()
    }

    private func resetForm() {
        editingTerm = nil
        term = ""
        heardAs = ""
    }

    private func importCSV() {
        let panel = NSOpenPanel()
        panel.title = "Import vocabulary CSV"
        panel.allowedContentTypes = [.commaSeparatedText]
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
                do {
                    let parsed = try await Task.detached(priority: .userInitiated) {
                        try VocabularyCSVImport.read(contentsOf: source)
                    }.value
                    let result = appState.settings.importVocabulary(parsed)
                    importFailed = false
                    importMessage = "Imported \(result.imported), skipped \(result.skipped)."
                } catch VocabularyCSVImport.Failure.tooLarge {
                    importFailed = true
                    importMessage = "Choose a CSV file up to 3 MB."
                } catch {
                    importFailed = true
                    importMessage = "This file could not be read. Save it as a UTF-8 CSV and try again."
                }
            }
        }
    }
}

/// Spoken trigger phrases that insert saved plain text.
struct SnippetsSection: View {
    @Environment(AppState.self) private var appState
    @State private var trigger = ""
    @State private var expansion = ""
    @State private var editingID: UUID?

    var body: some View {
        DSSection(overline: "Snippets") {
            ForEach(appState.settings.dictationSnippets) { snippet in
                DSDetailRow(label: snippet.trigger, caption: preview(of: snippet.expansion)) {
                    HStack(spacing: 6) {
                        DSIconButton(systemImage: "pencil",
                                     accessibilityLabel: "Edit snippet \(snippet.trigger)") {
                            editingID = snippet.id
                            trigger = snippet.trigger
                            expansion = snippet.expansion
                        }
                        DSIconButton(systemImage: "trash",
                                     accessibilityLabel: "Delete snippet \(snippet.trigger)") {
                            appState.settings.dictationSnippets.removeAll { $0.id == snippet.id }
                            if editingID == snippet.id { resetForm() }
                        }
                    }
                }
                DSDivider()
            }

            TextRulesCardContent {
                DSTextField(placeholder: "Trigger phrase, e.g. my calendar link", text: $trigger,
                            accessibilityName: "Snippet trigger phrase")
                    .frame(width: 320)
                SettingsMultilineTextArea(text: $expansion, label: "Snippet text",
                                          placeholder: "Text to insert…", minHeight: 64, maxHeight: 200)
                HStack(spacing: 8) {
                    Button(editingID == nil ? "Add Snippet" : "Save", action: save)
                        .buttonStyle(.dsSecondary)
                        .disabled(validationError != nil || trigger.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    if editingID != nil {
                        Button("Cancel", action: resetForm)
                            .buttonStyle(.dsSecondary)
                    }
                    Spacer(minLength: 0)
                    Text("\(expansion.count.formatted()) / \(DictationSnippet.maximumExpansionLength.formatted())")
                        .font(DS.Fonts.ui(11.5))
                        .foregroundStyle(DS.Colors.textSecondary)
                }
                if let validationError {
                    DSFieldMessage(text: validationError, tone: .error)
                }
            }

            DSDivider()
            DSCardCaption(text: "Say a trigger phrase to insert its text. Snippets expand on this Mac after cleanup, so their text is never sent to a cleanup provider. Leave the text empty to remove the phrase.")
        }
    }

    private var validationError: String? {
        let phrase = trigger.trimmingCharacters(in: .whitespacesAndNewlines)
        if phrase.count > DictationSnippet.maximumTriggerLength {
            return "Trigger phrases can be up to \(DictationSnippet.maximumTriggerLength) characters."
        }
        if expansion.trimmingCharacters(in: .whitespacesAndNewlines).count > DictationSnippet.maximumExpansionLength {
            return "Snippet text can be up to \(DictationSnippet.maximumExpansionLength.formatted()) characters."
        }
        if appState.settings.dictationSnippets.contains(where: {
            $0.id != editingID && $0.trigger.caseInsensitiveCompare(phrase) == .orderedSame
        }) {
            return "Another snippet already uses this trigger phrase."
        }
        return nil
    }

    private func preview(of text: String) -> String {
        guard !text.isEmpty else { return "Removes the phrase" }
        let firstLine = String(text.prefix { !$0.isNewline })
        if firstLine.count > 120 { return String(firstLine.prefix(120)) + "…" }
        return firstLine.count < text.count ? firstLine + "…" : firstLine
    }

    private func save() {
        guard validationError == nil,
              let snippet = DictationSnippet(id: editingID ?? UUID(), trigger: trigger, expansion: expansion).validated()
        else { return }
        if let index = appState.settings.dictationSnippets.firstIndex(where: { $0.id == snippet.id }) {
            appState.settings.dictationSnippets[index] = snippet
        } else {
            appState.settings.dictationSnippets.append(snippet)
        }
        resetForm()
    }

    private func resetForm() {
        editingID = nil
        trigger = ""
        expansion = ""
    }
}

/// Padded form area inside a settings card.
private struct TextRulesCardContent<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            content
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.vertical, 12)
        .padding(.horizontal, DS.Spacing.rowHorizontal)
    }
}
