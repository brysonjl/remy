//
//  ScreenNameTerms.swift
//  Dictate Anywhere
//
//  Picks likely names and identifiers out of active-window text so speech
//  and cleanup can spell them. A single regex-free pass; no NLTagger or
//  bundled word list. Results live only in memory for one dictation.
//

import Foundation

nonisolated enum ScreenNameTerms {
    static let maximumTerms = 25
    private static let maximumTermCharacters = 60
    private static let maximumNameWords = 4

    private enum Kind { case name, identifier, word }

    private struct Candidate {
        var term: String
        var kind: Kind
        var order: Int
        var count = 1
        var midSentence: Bool
        var displayName = false
    }

    /// Lowercase name particles that may sit between capitalized name words.
    private static let particles: Set<String> = [
        "al", "bin", "da", "das", "de", "del", "der", "di", "do", "dos", "du", "la", "le", "van", "von",
    ]

    /// Capitalized words that are almost never a name worth spelling: days,
    /// months, mail/chat chrome, greetings and common sentence starters. Shares
    /// the focused-field hint stoplist so both lists drop the same words.
    static let stoplist: Set<String> = DictationContext.commonHintWords.union([
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "mon", "tue", "tues", "wed", "thu", "thur", "thurs", "fri", "sat", "sun",
        "january", "february", "march", "april", "may", "june", "july", "august",
        "september", "october", "november", "december",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
        "today", "yesterday", "tomorrow", "now", "week", "month", "year",
        "inbox", "sent", "drafts", "draft", "archive", "archived", "junk", "trash", "spam", "outbox",
        "reply", "replies", "forward", "send", "compose", "new", "subject", "re", "fwd", "fw",
        "to", "cc", "bcc", "from", "attachment", "attachments", "unread", "read", "mark", "flag", "flagged",
        "starred", "important", "snoozed", "label", "labels", "folder", "folders", "mailbox", "mailboxes",
        "favorites", "notifications", "mentions", "activity", "threads", "thread", "channels", "channel",
        "direct", "messages", "chat", "chats", "members", "people", "contacts", "calendar", "notes",
        "online", "offline", "away", "active", "home", "menu", "search", "settings", "preferences",
        "file", "files", "edit", "view", "window", "help", "open", "close", "save", "cancel", "delete",
        "done", "ok", "okay", "yes", "no", "back", "next", "previous", "more", "less", "show", "hide",
        "all", "none", "add", "remove", "copy", "paste", "undo", "redo", "format", "insert", "tools",
        "account", "accounts", "profile", "sign", "log", "untitled", "share", "invite", "join", "leave",
        "the", "a", "an", "and", "or", "but", "if", "so", "then", "it", "its", "i", "we", "you", "he",
        "she", "my", "our", "his", "her", "hi", "hello", "hey", "dear", "thanks", "thank", "regards",
        "best", "cheers", "sincerely", "please", "sorry", "sure", "great", "good", "who", "why", "how",
        "is", "are", "was", "be", "will", "can", "do", "does", "did", "has", "had", "not", "let", "lets",
        "string", "int", "bool", "double", "float", "array", "error", "void", "true", "false", "self",
        "optional", "nil", "null", "none",
    ])

    /// Ranked, deduplicated terms from bounded window strings, in walk order.
    static func extract(from texts: [String], limit: Int = maximumTerms) -> [String] {
        var candidates: [String: Candidate] = [:]
        var order = 0

        func add(_ rawTerm: String, kind: Kind, midSentence: Bool, displayName: Bool = false) {
            let term = rawTerm.trimmingCharacters(in: .whitespaces)
            guard term.count >= 3, term.count <= maximumTermCharacters,
                  !stoplist.contains(term.lowercased()) else { return }
            let key = term.lowercased()
            if var existing = candidates[key] {
                existing.count += 1
                existing.midSentence = existing.midSentence || midSentence
                existing.displayName = existing.displayName || displayName
                candidates[key] = existing
            } else {
                candidates[key] = Candidate(term: term, kind: kind, order: order,
                    midSentence: midSentence, displayName: displayName)
                order += 1
            }
        }

        for text in texts {
            for line in text.split(whereSeparator: \.isNewline) {
                scan(line, add: add)
            }
        }

        return candidates.values
            .filter { candidate in
                candidate.kind != .word || candidate.midSentence || candidate.displayName
                    || candidate.count > 1 || hasDistinctiveForm(candidate.term)
            }
            .sorted { lhs, rhs in
                let left = score(lhs), right = score(rhs)
                return left == right ? lhs.order < rhs.order : left > right
            }
            .prefix(limit)
            .map(\.term)
    }

    /// Focused-field hints first, then screen terms not already present.
    static func merged(_ primary: [String], _ secondary: [String]) -> [String] {
        var seen: Set<String> = []
        return (primary + secondary).filter { seen.insert($0.lowercased()).inserted }
    }

    // MARK: - Scanner

    private static func scan(
        _ line: Substring,
        add: (String, Kind, Bool, Bool) -> Void
    ) {
        var run: [String] = []
        var runIsSentenceInitial = false
        var atSentenceStart = true

        func flush(displayName: Bool = false) {
            while let last = run.last, particles.contains(last) { run.removeLast() }
            defer { run.removeAll() }
            guard !run.isEmpty else { return }
            if run.count > maximumNameWords {
                // Title Case headings: keep only words that look unusual.
                for word in run where hasDistinctiveForm(word) { add(word, .word, true, false) }
            } else if run.count >= 2 {
                add(run.joined(separator: " "), .name, true, displayName)
            } else {
                add(run[0], .word, !runIsSentenceInitial, displayName)
            }
        }

        var index = line.startIndex
        while index < line.endIndex {
            // Split on whitespace and bracket/quote delimiters.
            while index < line.endIndex, isSeparator(line[index]) { index = line.index(after: index) }
            guard index < line.endIndex else { break }
            let start = index
            while index < line.endIndex, !isSeparator(line[index]) { index = line.index(after: index) }
            let token = line[start..<index]

            if token.contains("@") {
                // An address after a name marks an email display name. The
                // address itself is never a term.
                flush(displayName: true)
                atSentenceStart = false
                continue
            }
            if token.contains("://") || token.lowercased().hasPrefix("www.") {
                flush()
                atSentenceStart = false
                continue
            }

            let endsSentence = token.last.map { ".!?…".contains($0) } ?? false
            let endsClause = endsSentence || (token.last.map { ",;:)".contains($0) } ?? false)
            var word = String(token).trimmingCharacters(in: trimmedPunctuation)
            for suffix in ["'s", "’s"] where word.hasSuffix(suffix) { word.removeLast(2) }
            defer { atSentenceStart = endsSentence }

            guard let first = word.first else { flush(); continue }

            if word.contains("."), word.contains(where: \.isLowercase) {
                // A dotted member path is several identifiers, never a name.
                flush()
                for part in word.split(separator: ".") where isIdentifier(part) {
                    add(String(part), .identifier, true, false)
                }
                continue
            }
            if isIdentifier(Substring(word)), first.isLowercase || first == "_" || word.contains("_") {
                flush()
                add(word, .identifier, true, false)
                continue
            }
            if isNameWord(word) {
                if stoplist.contains(word.lowercased()) {
                    // UI labels and greetings break a run. Unless this word
                    // ends a sentence, the next word is no longer initial.
                    flush()
                    continue
                }
                if run.isEmpty { runIsSentenceInitial = atSentenceStart }
                run.append(word)
                if endsClause { flush() }
                continue
            }
            if !run.isEmpty, particles.contains(word), !endsClause {
                run.append(word)
                continue
            }
            flush()
        }
        flush()
    }

    private static func isSeparator(_ character: Character) -> Bool {
        character.isWhitespace || "()[]{}<>\"“”=|•·".contains(character)
    }

    private static let trimmedPunctuation = CharacterSet(charactersIn: " .,:;!?…'’\"“”()[]{}<>*#-–—/\\")

    /// Capitalized natural-language word: letters plus inner hyphen/apostrophe,
    /// with at least one lowercase letter so ALL-CAPS labels are not names.
    private static func isNameWord(_ word: String) -> Bool {
        guard let first = word.first, first.isUppercase, word.contains(where: \.isLowercase) else { return false }
        return word.allSatisfy { $0.isLetter || $0 == "-" || $0 == "'" || $0 == "’" }
    }

    /// camelCase, PascalCase with a second hump, or snake_case.
    private static func isIdentifier(_ word: Substring) -> Bool {
        guard word.count >= 3 else { return false }
        var hasLowercase = false, hasInnerHump = false, hasInnerUnderscore = false
        var previous: Character?
        for (offset, character) in word.enumerated() {
            if character == "_" {
                if offset > 0, offset < word.count - 1 { hasInnerUnderscore = true }
            } else if character.isLowercase {
                hasLowercase = true
            } else if character.isUppercase {
                if let previous, previous.isLowercase || previous.isNumber { hasInnerHump = true }
            } else if !character.isNumber {
                return false
            }
            previous = character
        }
        return hasLowercase && (hasInnerHump || hasInnerUnderscore)
    }

    private static func hasDistinctiveForm(_ term: String) -> Bool {
        term.unicodeScalars.contains { $0.value > 127 } || isIdentifier(Substring(term))
    }

    private static func score(_ candidate: Candidate) -> Double {
        var value: Double
        switch candidate.kind {
        case .name: value = 3
        case .identifier: value = 2.5
        case .word: value = candidate.midSentence ? 1 : 0.5
        }
        if candidate.displayName { value += 1.5 }
        if candidate.term.unicodeScalars.contains(where: { $0.value > 127 }) { value += 1 }
        if candidate.kind == .word, isIdentifier(Substring(candidate.term)) { value += 1.5 }
        if candidate.term.contains(where: { $0 == "-" || $0 == "'" || $0 == "’" }) { value += 0.5 }
        value += 0.5 * Double(min(candidate.count - 1, 3))
        return value
    }
}
