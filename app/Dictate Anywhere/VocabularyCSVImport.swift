//
//  VocabularyCSVImport.swift
//  Dictate Anywhere
//
//  Parses vocabulary CSV files and merges them into the saved vocabulary.
//

import Foundation

/// One column is a term. Two columns are `misspelling,correction`, the
/// misspelling first. A recognized header row is skipped.
nonisolated enum VocabularyCSVImport {
    static let maximumBytes = 3 * 1_024 * 1_024
    static let maximumEntries = 1_000

    enum Failure: Error, Equatable {
        case tooLarge
        case unreadable
    }

    struct Entry: Equatable {
        let term: String
        let misspelling: String?
    }

    struct Parsed: Equatable {
        let entries: [Entry]
        let skipped: Int
    }

    struct Merged: Equatable {
        let terms: [String]
        let misspellings: [String: [String]]
        let imported: Int
        let skipped: Int
    }

    private static let headers: Set<[String]> = [
        ["word"], ["words"], ["term"], ["terms"], ["vocabulary"],
        ["misspelling", "correction"], ["misspelling", "correct spelling"],
    ]

    /// Checks the size before reading so an oversized file is never loaded.
    static func read(contentsOf url: URL) throws -> Parsed {
        let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize
        guard (size ?? 0) <= maximumBytes else { throw Failure.tooLarge }
        guard let data = try? Data(contentsOf: url) else { throw Failure.unreadable }
        return try parse(data)
    }

    static func parse(_ data: Data) throws -> Parsed {
        guard data.count <= maximumBytes else { throw Failure.tooLarge }
        guard var text = String(data: data, encoding: .utf8) else { throw Failure.unreadable }
        if text.hasPrefix("\u{FEFF}") { text.removeFirst() }
        var entries: [Entry] = []
        var skipped = 0
        var isFirstRow = true
        for row in rows(in: text) {
            var fields = row.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            while fields.last?.isEmpty == true { fields.removeLast() }
            if fields.isEmpty { continue }
            defer { isFirstRow = false }
            if isFirstRow, headers.contains(fields.map { $0.lowercased() }) { continue }
            guard entries.count < maximumEntries,
                  (1...2).contains(fields.count),
                  fields.allSatisfy({ !$0.isEmpty && $0.count <= DictationTextRules.maximumPhraseLength })
            else {
                skipped += 1
                continue
            }
            entries.append(fields.count == 1
                ? Entry(term: fields[0], misspelling: nil)
                : Entry(term: fields[1], misspelling: fields[0]))
        }
        return Parsed(entries: entries, skipped: skipped)
    }

    /// RFC 4180 records: quoted fields may contain commas, line breaks and
    /// doubled quotes. Quotes inside an unquoted field are kept literally.
    static func rows(in text: String) -> [[String]] {
        var rows: [[String]] = []
        var row: [String] = []
        var field = ""
        var inQuotes = false
        var fieldWasQuoted = false
        var iterator = Array(text).makeIterator()
        var pending = iterator.next()
        while let character = pending {
            pending = iterator.next()
            if inQuotes {
                if character == "\"" {
                    if pending == "\"" {
                        field.append("\"")
                        pending = iterator.next()
                    } else {
                        inQuotes = false
                    }
                } else {
                    field.append(character)
                }
            } else if character == "\"", field.isEmpty, !fieldWasQuoted {
                inQuotes = true
                fieldWasQuoted = true
            } else if character == "," {
                row.append(field)
                field = ""
                fieldWasQuoted = false
            } else if character.isNewline {
                row.append(field)
                rows.append(row)
                row = []
                field = ""
                fieldWasQuoted = false
            } else {
                field.append(character)
            }
        }
        if !field.isEmpty || fieldWasQuoted || !row.isEmpty {
            row.append(field)
            rows.append(row)
        }
        return rows
    }

    /// Terms match case-insensitively; an existing spelling is kept. Entries
    /// that add nothing new are counted as skipped.
    static func merge(_ parsed: Parsed, terms: [String], misspellings: [String: [String]]) -> Merged {
        var terms = terms
        var misspellings = misspellings
        var imported = 0
        var skipped = parsed.skipped
        for entry in parsed.entries {
            let existing = terms.first { $0.caseInsensitiveCompare(entry.term) == .orderedSame }
            let term = existing ?? entry.term
            var added = existing == nil
            if existing == nil { terms.append(term) }
            if let misspelling = entry.misspelling, misspelling != term {
                var list = misspellings[term] ?? []
                if !list.contains(where: { $0.caseInsensitiveCompare(misspelling) == .orderedSame }) {
                    list.append(misspelling)
                    misspellings[term] = list
                    added = true
                }
            }
            if added { imported += 1 } else { skipped += 1 }
        }
        return Merged(terms: terms, misspellings: misspellings, imported: imported, skipped: skipped)
    }
}
