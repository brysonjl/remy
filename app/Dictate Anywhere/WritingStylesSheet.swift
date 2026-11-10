//
//  WritingStylesSheet.swift
//  Dictate Anywhere
//
//  Edit built-in writing style instructions and manage custom styles.
//

import SwiftUI

struct WritingStylesSheet: View {
    @Environment(\.dismiss) private var dismiss
    let settings: Settings

    private enum Target: Equatable {
        case builtIn(DictationWritingStyle)
        case custom(UUID?) // nil adds a new style
    }

    @State private var editing: Target?
    @State private var draftName = ""
    @State private var draftInstruction = ""
    @State private var errorMessage: String?
    @State private var pendingDeletion: DictationCustomWritingStyle?

    private var library: DictationWritingStyleLibrary { settings.dictationWritingStyleLibrary }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: DS.Spacing.section) {
                    DSSectionHeader(
                        title: "Writing Styles",
                        subtitle: "Style instructions tell cleanup how the text should sound. Core cleanup rules always apply."
                    )
                    if let editing {
                        editor(for: editing)
                    } else {
                        styleLists
                    }
                }
                .padding(28)
            }
            DSDivider()
            HStack {
                Spacer()
                Button("Done") { dismiss() }
                    .buttonStyle(.dsPrimary)
                    .keyboardShortcut(.defaultAction)
                    .disabled(editing != nil)
            }
            .padding(.horizontal, 28)
            .padding(.vertical, 14)
        }
        .frame(width: 600, height: 640)
        .background(DS.Colors.bgWindow)
        .confirmationDialog(
            "Delete “\(pendingDeletion?.name ?? "")”?",
            isPresented: Binding(get: { pendingDeletion != nil }, set: { if !$0 { pendingDeletion = nil } }),
            presenting: pendingDeletion
        ) { style in
            Button("Delete Style", role: .destructive) {
                settings.deleteCustomWritingStyle(id: style.id)
                pendingDeletion = nil
            }
            Button("Cancel", role: .cancel) { pendingDeletion = nil }
        } message: { style in
            Text(deletionMessage(for: style))
        }
    }

    // MARK: - Lists

    @ViewBuilder
    private var styleLists: some View {
        DSSection(overline: "Built-in Styles") {
            ForEach(Array(DictationWritingStyle.allCases.enumerated()), id: \.element) { index, style in
                if index > 0 { DSDivider() }
                styleRow(
                    name: style.displayName,
                    detail: library.isCustomized(style) ? "Edited" : nil,
                    instruction: library.instruction(for: style)
                ) {
                    DSIconButton(systemImage: "pencil", accessibilityLabel: "Edit \(style.displayName)") {
                        beginEditing(.builtIn(style))
                    }
                }
            }
        }

        DSSection(overline: "Custom Styles") {
            if library.customStyles.isEmpty {
                DSCardCaption(text: "Add a style to choose it for any destination.")
            }
            ForEach(library.customStyles) { style in
                styleRow(name: style.name, detail: nil, instruction: style.instruction) {
                    HStack(spacing: 8) {
                        DSIconButton(systemImage: "pencil", accessibilityLabel: "Edit \(style.name)") {
                            beginEditing(.custom(style.id))
                        }
                        DSIconButton(
                            systemImage: "trash",
                            tint: DS.Colors.destructive,
                            accessibilityLabel: "Delete \(style.name)"
                        ) {
                            pendingDeletion = style
                        }
                    }
                }
                DSDivider()
            }
            DSAddButton(title: "Add Style") { beginEditing(.custom(nil)) }
                .padding(DS.Spacing.rowHorizontal)
        }
    }

    private func styleRow<Actions: View>(
        name: String,
        detail: String?,
        instruction: String,
        @ViewBuilder actions: () -> Actions
    ) -> some View {
        HStack(alignment: .top, spacing: DS.Spacing.rowControlGap) {
            VStack(alignment: .leading, spacing: DS.Spacing.labelToCaption) {
                HStack(spacing: 8) {
                    Text(name)
                        .font(DS.Fonts.ui(13.5, .medium))
                        .foregroundStyle(DS.Colors.ink)
                    if let detail { DSChip(text: detail) }
                }
                Text(instruction)
                    .font(DS.Fonts.ui(12.5))
                    .foregroundStyle(DS.Colors.textSecondary)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
            actions()
        }
        .padding(.vertical, DS.Spacing.rowVertical)
        .padding(.horizontal, DS.Spacing.rowHorizontal)
    }

    // MARK: - Editor

    private func editor(for target: Target) -> some View {
        DSSection(overline: editorTitle(for: target)) {
            VStack(alignment: .leading, spacing: 12) {
                switch target {
                case .builtIn(let style):
                    Text(style.displayName)
                        .font(DS.Fonts.ui(13.5, .semibold))
                        .foregroundStyle(DS.Colors.ink)
                case .custom:
                    Text("Name").font(DS.Fonts.ui(13, .semibold))
                    DSTextField(placeholder: "Style name", text: $draftName, accessibilityName: "Style name")
                }

                Text("Instructions").font(DS.Fonts.ui(13, .semibold))
                SettingsMultilineTextArea(
                    text: $draftInstruction,
                    label: "Style instructions",
                    placeholder: "Describe the tone and wording, for example: Use short, friendly sentences.",
                    minHeight: 140,
                    maxHeight: 320
                )
                Text("\(draftInstruction.trimmingCharacters(in: .whitespacesAndNewlines).count)/\(DictationWritingStyleLibrary.maximumInstructionLength) characters")
                    .font(DS.Fonts.ui(12))
                    .foregroundStyle(DS.Colors.textSecondary)
                    .monospacedDigit()

                if let errorMessage {
                    DSFieldMessage(text: errorMessage, tone: .error)
                }

                HStack(spacing: 10) {
                    if case .builtIn(let style) = target {
                        Button("Reset to Default") { resetBuiltIn(style) }
                            .buttonStyle(.dsSecondary)
                            .disabled(!library.isCustomized(style) && draftInstruction == style.cleanupInstruction)
                    }
                    Spacer()
                    Button("Cancel") { editing = nil }
                        .buttonStyle(.dsSecondary)
                        .keyboardShortcut(.cancelAction)
                    Button("Save", action: save)
                        .buttonStyle(.dsPrimary)
                }
            }
            .padding(DS.Spacing.rowHorizontal)
        }
    }

    private func editorTitle(for target: Target) -> String {
        switch target {
        case .builtIn: return "Edit Built-in Style"
        case .custom(nil): return "New Custom Style"
        case .custom: return "Edit Custom Style"
        }
    }

    private func beginEditing(_ target: Target) {
        errorMessage = nil
        switch target {
        case .builtIn(let style):
            draftName = style.displayName
            draftInstruction = library.instruction(for: style)
        case .custom(let id):
            let style = id.flatMap(library.customStyle(id:))
            draftName = style?.name ?? ""
            draftInstruction = style?.instruction ?? ""
        }
        editing = target
    }

    private func save() {
        guard let editing else { return }
        var updated = library
        do {
            switch editing {
            case .builtIn(let style):
                try updated.setInstruction(draftInstruction, for: style)
            case .custom(nil):
                try updated.addCustomStyle(name: draftName, instruction: draftInstruction)
            case .custom(let id?):
                try updated.updateCustomStyle(id: id, name: draftName, instruction: draftInstruction)
            }
        } catch let error as DictationWritingStyleLibrary.ValidationError {
            errorMessage = error.message
            return
        } catch {
            return
        }
        settings.dictationWritingStyleLibrary = updated
        self.editing = nil
    }

    private func resetBuiltIn(_ style: DictationWritingStyle) {
        settings.dictationWritingStyleLibrary.resetInstruction(for: style)
        editing = nil
    }

    private func deletionMessage(for style: DictationCustomWritingStyle) -> String {
        let categories = settings.categoriesUsingCustomWritingStyle(id: style.id)
        guard !categories.isEmpty else { return "No destination uses this style." }
        let uses = categories.map {
            "\($0.displayName) → \(DictationWritingStyle.defaultStyle(for: $0).displayName)"
        }
        return "These destinations use this style and will return to their default style: "
            + uses.joined(separator: ", ") + "."
    }
}
