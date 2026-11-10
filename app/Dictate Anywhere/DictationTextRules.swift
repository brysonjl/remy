//
//  DictationTextRules.swift
//  Dictate Anywhere
//
//  Deterministic, local spelling corrections and snippet expansion.
//

import Foundation

/// A custom vocabulary term and the spellings recognition produces for it.
nonisolated struct VocabularyCorrection: Codable, Equatable, Sendable {
    let term: String
    let misspellings: [String]
}

/// Spoken trigger phrase replaced by plain text.
nonisolated struct DictationSnippet: Codable, Equatable, Sendable, Identifiable {
    var id = UUID()
    var trigger: String
    var expansion: String

    static let maximumTriggerLength = 60
    static let maximumExpansionLength = 4_000

    /// Trimmed copy suitable for saving, or nil when either field is invalid.
    /// An empty expansion is valid: it removes the trigger phrase.
    func validated() -> DictationSnippet? {
        let trigger = trigger.trimmingCharacters(in: .whitespacesAndNewlines)
        let expansion = expansion.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trigger.isEmpty, trigger.count <= Self.maximumTriggerLength,
              expansion.count <= Self.maximumExpansionLength else { return nil }
        return DictationSnippet(id: id, trigger: trigger, expansion: expansion)
    }
}

/// Immutable per-session rules. Matching is literal: whole word/phrase,
/// case-insensitive on input, never inside a larger word, longest match first,
/// and a single left-to-right pass so replacements never cascade. The saved
/// spelling or expansion is output exactly.
///
/// Corrections run once on the local transcript before cleanup, leaving any
/// snippet trigger in place. Snippets expand after cleanup, so an expansion is
/// never sent to a cleanup provider.
nonisolated struct DictationTextRules {
    /// Longest accepted vocabulary term or heard-as spelling.
    static let maximumPhraseLength = 60

    let corrections: [VocabularyCorrection]
    let snippets: [DictationSnippet]

    init(corrections: [VocabularyCorrection] = [], snippets: [DictationSnippet] = []) {
        self.corrections = corrections
        self.snippets = snippets
    }

    var isEmpty: Bool { corrections.allSatisfy(\.misspellings.isEmpty) && snippets.isEmpty }

    /// Replaces heard-as spellings with their saved terms. Snippet triggers win
    /// any overlap and are left untouched for `expandingSnippets(in:)`.
    func applyingCorrections(to text: String) -> String {
        let rules = snippetRules + correctionRules
        guard rules.contains(where: { !$0.isSnippet }) else { return text }
        return Self.replacing(in: text, rules: rules, expandSnippets: false)
    }

    /// Replaces snippet triggers with their expansions. A dictation that is
    /// only a trigger, apart from surrounding punctuation, becomes exactly
    /// the expansion.
    func expandingSnippets(in text: String) -> String {
        let rules = snippetRules
        guard !rules.isEmpty else { return text }
        let core = text.trimmingCharacters(in: Self.edgeTrimmed)
        let coreCharacters = Array(core)
        if !coreCharacters.isEmpty,
           let whole = Self.candidates(in: coreCharacters, rules: rules)
               .filter({ $0.start == 0 && $0.end == coreCharacters.count })
               .min(by: { $0.rule < $1.rule }) {
            return rules[whole.rule].replacement
        }
        return Self.replacing(in: text, rules: rules, expandSnippets: true)
    }

    // MARK: - Matching

    private struct Rule {
        let units: [String]
        let first: Character
        let last: Character
        let replacement: String
        let isSnippet: Bool

        init?(phrase: String, replacement: String, isSnippet: Bool) {
            let phrase = phrase.trimmingCharacters(in: .whitespacesAndNewlines)
            guard let first = phrase.first, let last = phrase.last else { return nil }
            var units: [String] = []
            for character in phrase {
                if character.isWhitespace {
                    if units.last != " " { units.append(" ") }
                } else {
                    units.append(String(character).lowercased())
                }
            }
            self.units = units
            self.first = first
            self.last = last
            self.replacement = replacement
            self.isSnippet = isSnippet
        }
    }

    private struct Candidate {
        let start: Int
        let end: Int
        let rule: Int
    }

    private static let edgeTrimmed = CharacterSet.whitespacesAndNewlines.union(.punctuationCharacters)

    private var snippetRules: [Rule] {
        snippets.compactMap { Rule(phrase: $0.trigger, replacement: $0.expansion, isSnippet: true) }
    }

    private var correctionRules: [Rule] {
        corrections.flatMap { correction in
            correction.misspellings.compactMap {
                Rule(phrase: $0, replacement: correction.term, isSnippet: false)
            }
        }
    }

    private static func replacing(in text: String, rules: [Rule], expandSnippets: Bool) -> String {
        let characters = Array(text)
        let selected = selectedMatches(candidates(in: characters, rules: rules), rules: rules)
        guard !selected.isEmpty else { return text }
        var output = ""
        var cursor = 0
        for match in selected {
            guard match.start >= cursor else { continue }
            output.append(contentsOf: characters[cursor..<match.start])
            cursor = match.end
            let rule = rules[match.rule]
            if rule.isSnippet && !expandSnippets {
                output.append(contentsOf: characters[match.start..<match.end])
                continue
            }
            output += rule.replacement
            guard rule.replacement.isEmpty else { continue }
            // Removing a phrase must not leave a doubled or dangling space.
            let next = cursor < characters.count ? characters[cursor] : nil
            if output.last == " ", next == nil || next == " " || next?.isPunctuation == true {
                while output.last == " " { output.removeLast() }
            } else if output.last?.isWhitespace ?? true {
                while cursor < characters.count, characters[cursor] == " " { cursor += 1 }
            }
        }
        output.append(contentsOf: characters[cursor...])
        return output
    }

    /// Every rule occurrence with valid boundaries, including overlaps.
    private static func candidates(in characters: [Character], rules: [Rule]) -> [Candidate] {
        var byFirstUnit: [String: [Int]] = [:]
        for (index, rule) in rules.enumerated() {
            byFirstUnit[rule.units[0], default: []].append(index)
        }
        let folded = characters.map { $0.isWhitespace ? " " : String($0).lowercased() }
        var result: [Candidate] = []
        for start in characters.indices {
            guard let ruleIndices = byFirstUnit[folded[start]] else { continue }
            let previous = start > 0 ? characters[start - 1] : nil
            for index in ruleIndices {
                let rule = rules[index]
                guard isBoundary(previous, edge: rule.first),
                      let end = matchEnd(rule, at: start, characters: characters, folded: folded),
                      isBoundary(end < characters.count ? characters[end] : nil, edge: rule.last)
                else { continue }
                result.append(Candidate(start: start, end: end, rule: index))
            }
        }
        return result
    }

    private static func matchEnd(_ rule: Rule, at start: Int, characters: [Character], folded: [String]) -> Int? {
        var position = start
        for unit in rule.units {
            guard position < characters.count else { return nil }
            if unit == " " {
                guard characters[position].isWhitespace else { return nil }
                while position < characters.count, characters[position].isWhitespace { position += 1 }
            } else {
                guard folded[position] == unit else { return nil }
                position += 1
            }
        }
        return position
    }

    /// Snippets beat any overlapping correction; then longest first, the
    /// earlier position and the earlier rule. Overlapping candidates are dropped.
    private static func selectedMatches(_ candidates: [Candidate], rules: [Rule]) -> [Candidate] {
        let ordered = candidates.sorted { lhs, rhs in
            if rules[lhs.rule].isSnippet != rules[rhs.rule].isSnippet { return rules[lhs.rule].isSnippet }
            let lhsLength = lhs.end - lhs.start, rhsLength = rhs.end - rhs.start
            if lhsLength != rhsLength { return lhsLength > rhsLength }
            if lhs.start != rhs.start { return lhs.start < rhs.start }
            return lhs.rule < rhs.rule
        }
        var taken: [Candidate] = []
        for candidate in ordered where !taken.contains(where: { candidate.start < $0.end && $0.start < candidate.end }) {
            taken.append(candidate)
        }
        return taken.sorted { $0.start < $1.start }
    }

    /// Letters, digits and underscore join words. Han and kana text is
    /// unsegmented, so a boundary is not required beside those characters.
    private static func isBoundary(_ neighbor: Character?, edge: Character) -> Bool {
        guard let neighbor, neighbor.isLetter || neighbor.isNumber || neighbor == "_" else { return true }
        return isUnsegmented(neighbor) || isUnsegmented(edge)
    }

    private static func isUnsegmented(_ character: Character) -> Bool {
        guard let scalar = character.unicodeScalars.first else { return false }
        return CJKText.isCJK(scalar) || (0x3040...0x30FF).contains(scalar.value)
    }
}
