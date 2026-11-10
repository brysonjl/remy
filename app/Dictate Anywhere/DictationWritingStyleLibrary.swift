//
//  DictationWritingStyleLibrary.swift
//  Dictate Anywhere
//
//  User-editable instructions for built-in writing styles, plus user-defined
//  styles. Built-ins keep their raw values as stable IDs.
//

import Foundation

/// A per-category style choice. Stored as `id`: a built-in's raw value
/// (unchanged from earlier releases) or `custom:<UUID>`.
nonisolated enum DictationWritingStyleSelection: Hashable, Identifiable, Sendable {
    case builtIn(DictationWritingStyle)
    case custom(UUID)

    private static let customPrefix = "custom:"

    /// Unknown or malformed IDs yield nil so callers can use the category default.
    init?(id: String) {
        if let style = DictationWritingStyle(rawValue: id) {
            self = .builtIn(style)
        } else if id.hasPrefix(Self.customPrefix),
                  let uuid = UUID(uuidString: String(id.dropFirst(Self.customPrefix.count))) {
            self = .custom(uuid)
        } else {
            return nil
        }
    }

    var id: String {
        switch self {
        case .builtIn(let style): return style.rawValue
        case .custom(let uuid): return Self.customPrefix + uuid.uuidString
        }
    }

    var builtInStyle: DictationWritingStyle? {
        if case .builtIn(let style) = self { return style }
        return nil
    }
}

nonisolated struct DictationCustomWritingStyle: Codable, Equatable, Identifiable, Sendable {
    let id: UUID
    var name: String
    var instruction: String
}

/// The style frozen into one dictation's cleanup context.
nonisolated struct DictationResolvedWritingStyle: Equatable, Sendable {
    let selection: DictationWritingStyleSelection
    let displayName: String
    let instruction: String

    static func builtIn(_ style: DictationWritingStyle) -> Self {
        Self(selection: .builtIn(style), displayName: style.displayName, instruction: style.cleanupInstruction)
    }

    var builtInStyle: DictationWritingStyle? { selection.builtInStyle }

    /// Custom styles and edited built-ins carry text the user wrote.
    var isUserAuthored: Bool { instruction != builtInStyle?.cleanupInstruction }

    /// Custom styles are valid in every category; built-ins keep their limits.
    func sanitized(for category: DictationContextCategory) -> Self {
        guard let builtInStyle, builtInStyle.sanitized(for: category) != builtInStyle else { return self }
        return .builtIn(builtInStyle.sanitized(for: category))
    }
}

nonisolated struct DictationWritingStyleLibrary: Codable, Equatable, Sendable {
    static let maximumNameLength = 40
    static let maximumInstructionLength = 1_000

    enum ValidationError: Error, Equatable {
        case emptyName
        case nameTooLong
        case duplicateName
        case emptyInstruction
        case instructionTooLong

        var message: String {
            switch self {
            case .emptyName: return "Enter a style name."
            case .nameTooLong: return "Use a name of \(DictationWritingStyleLibrary.maximumNameLength) characters or fewer."
            case .duplicateName: return "A style with this name already exists."
            case .emptyInstruction: return "Enter instructions for this style."
            case .instructionTooLong:
                return "Use instructions of \(DictationWritingStyleLibrary.maximumInstructionLength) characters or fewer."
            }
        }
    }

    private(set) var customStyles: [DictationCustomWritingStyle] = []
    /// Edited built-in instructions keyed by built-in raw value.
    private(set) var instructionOverrides: [String: String] = [:]

    // MARK: Built-in styles

    func instruction(for style: DictationWritingStyle) -> String {
        instructionOverrides[style.rawValue] ?? style.cleanupInstruction
    }

    func isCustomized(_ style: DictationWritingStyle) -> Bool {
        instructionOverrides[style.rawValue] != nil
    }

    mutating func setInstruction(_ instruction: String, for style: DictationWritingStyle) throws {
        let instruction = try Self.validatedInstruction(instruction)
        instructionOverrides[style.rawValue] = instruction == style.cleanupInstruction ? nil : instruction
    }

    mutating func resetInstruction(for style: DictationWritingStyle) {
        instructionOverrides[style.rawValue] = nil
    }

    // MARK: Custom styles

    func customStyle(id: UUID) -> DictationCustomWritingStyle? {
        customStyles.first { $0.id == id }
    }

    @discardableResult
    mutating func addCustomStyle(name: String, instruction: String) throws -> DictationCustomWritingStyle {
        let style = DictationCustomWritingStyle(
            id: UUID(),
            name: try validatedName(name, excluding: nil),
            instruction: try Self.validatedInstruction(instruction)
        )
        customStyles.append(style)
        return style
    }

    mutating func updateCustomStyle(id: UUID, name: String, instruction: String) throws {
        guard let index = customStyles.firstIndex(where: { $0.id == id }) else { return }
        let name = try validatedName(name, excluding: id)
        let instruction = try Self.validatedInstruction(instruction)
        customStyles[index].name = name
        customStyles[index].instruction = instruction
    }

    mutating func removeCustomStyle(id: UUID) {
        customStyles.removeAll { $0.id == id }
    }

    // MARK: Selection

    func displayName(for selection: DictationWritingStyleSelection) -> String {
        switch selection {
        case .builtIn(let style): return style.displayName
        case .custom(let id): return customStyle(id: id)?.name ?? "Deleted style"
        }
    }

    /// A missing custom style falls back to the category's default style.
    func sanitized(
        _ selection: DictationWritingStyleSelection,
        for category: DictationContextCategory
    ) -> DictationWritingStyleSelection {
        switch selection {
        case .builtIn(let style):
            return .builtIn(style.sanitized(for: category))
        case .custom(let id):
            return customStyle(id: id) == nil ? .builtIn(.defaultStyle(for: category)) : selection
        }
    }

    func resolved(
        _ selection: DictationWritingStyleSelection,
        for category: DictationContextCategory
    ) -> DictationResolvedWritingStyle {
        let selection = sanitized(selection, for: category)
        switch selection {
        case .builtIn(let style):
            return DictationResolvedWritingStyle(
                selection: selection, displayName: style.displayName, instruction: instruction(for: style)
            )
        case .custom(let id):
            // `sanitized` guarantees the style exists.
            let style = customStyle(id: id)
            return DictationResolvedWritingStyle(
                selection: selection, displayName: style?.name ?? "", instruction: style?.instruction ?? ""
            )
        }
    }

    // MARK: Persistence

    /// Decodes saved data, dropping entries that no longer validate. Corrupt
    /// data yields an empty library rather than failing Settings load.
    static func loaded(from data: Data?) -> Self {
        guard let data, let decoded = try? JSONDecoder().decode(Self.self, from: data) else { return Self() }
        var result = Self()
        for (key, value) in decoded.instructionOverrides {
            guard let style = DictationWritingStyle(rawValue: key) else { continue }
            try? result.setInstruction(value, for: style)
        }
        for style in decoded.customStyles {
            guard let name = try? result.validatedName(style.name, excluding: nil),
                  let instruction = try? validatedInstruction(style.instruction) else { continue }
            result.customStyles.append(.init(id: style.id, name: name, instruction: instruction))
        }
        return result
    }

    // MARK: Validation

    /// Trims and collapses whitespace so a name is always one prompt-safe line.
    func validatedName(_ name: String, excluding id: UUID?) throws -> String {
        let name = name.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        guard !name.isEmpty else { throw ValidationError.emptyName }
        guard name.count <= Self.maximumNameLength else { throw ValidationError.nameTooLong }
        let taken = DictationWritingStyle.allCases.map(\.displayName)
            + customStyles.filter { $0.id != id }.map(\.name)
        guard !taken.contains(where: { $0.caseInsensitiveCompare(name) == .orderedSame }) else {
            throw ValidationError.duplicateName
        }
        return name
    }

    static func validatedInstruction(_ instruction: String) throws -> String {
        let instruction = instruction.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !instruction.isEmpty else { throw ValidationError.emptyInstruction }
        guard instruction.count <= maximumInstructionLength else { throw ValidationError.instructionTooLong }
        return instruction
    }
}
