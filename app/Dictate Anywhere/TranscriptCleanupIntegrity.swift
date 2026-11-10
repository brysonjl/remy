import Foundation

/// A conservative preservation gate for long model transformations, not a
/// semantic verifier. A rejected passage keeps its complete original text.
nonisolated enum TranscriptCleanupIntegrity {
    private static let numericLiteral = try! NSRegularExpression(pattern: #"\d+"#)
    private static let commaGrouping = try! NSRegularExpression(pattern: #",(?=\d{3}(?:\D|$))"#)

    static func preservesNumericLiterals(from source: String, in output: String) -> Bool {
        func counts(_ text: String) -> [String: Int] {
            let folded = text.folding(options: .widthInsensitive, locale: nil)
            // Accept common comma thousands formatting without treating a
            // decimal point as a grouping separator or dropping digit groups.
            let normalized = commaGrouping.stringByReplacingMatches(in: folded,
                range: NSRange(folded.startIndex..<folded.endIndex, in: folded), withTemplate: "")
            var counts: [String: Int] = [:]
            for match in numericLiteral.matches(in: normalized,
                range: NSRange(normalized.startIndex..<normalized.endIndex, in: normalized)) {
                guard let range = Range(match.range, in: normalized) else { continue }
                counts[String(normalized[range]), default: 0] += 1
            }
            return counts
        }
        let actual = counts(output)
        return counts(source).allSatisfy { actual[$0.key, default: 0] >= $0.value }
    }

    // MARK: - Known terms

    private struct Word {
        let key: String
        let range: Range<String.Index>
    }

    /// Undoes known terms (vocabulary or on-screen names) that cleanup added
    /// without a similar-sounding source. Small models given "prefer these
    /// spellings" insert a term over unrelated words ("the petri dish" became
    /// "the Zephyrcore dish") or append it. Each unsupported occurrence's
    /// differing region goes back to the source words, keeping the rest of the
    /// cleanup. Returns the output and the number of regions restored.
    static func revertingUnsupportedTerms(source: String, output: String, terms: [String]) -> (text: String, reverted: Int) {
        let termKeys = terms.map { words(in: $0).map(\.key) }.filter { !$0.isEmpty }
        guard !termKeys.isEmpty else { return (output, 0) }
        let sourceWords = words(in: source), outputWords = words(in: output)
        let sourceKeys = sourceWords.map(\.key), outputKeys = outputWords.map(\.key)
        let added = termKeys.filter { occurrences(of: $0, in: outputKeys).count > occurrences(of: $0, in: sourceKeys).count }
        guard !added.isEmpty, let matches = alignment(sourceKeys, outputKeys) else { return (output, 0) }

        // Each differing region lies between the nearest aligned words.
        var regions: [(out: Range<Int>, src: Range<Int>)] = []
        for term in added {
            for occurrence in occurrences(of: term, in: outputKeys) {
                guard occurrence.contains(where: { matches[$0] == nil }) else { continue }
                let before = (0..<occurrence.lowerBound).last { matches[$0] != nil }
                let after = (occurrence.upperBound..<outputKeys.count).first { matches[$0] != nil }
                let out = (before.map { $0 + 1 } ?? 0)..<(after ?? outputKeys.count)
                let src = (before.flatMap { matches[$0] }.map { $0 + 1 } ?? 0)..<(after.flatMap { matches[$0] } ?? sourceKeys.count)
                guard !regions.contains(where: { $0.out == out }),
                      !hasSimilarSpan(to: term.joined(), in: Array(sourceKeys[src]), maxWords: term.count + 2)
                else { continue }
                regions.append((out, src))
            }
        }
        guard !regions.isEmpty else { return (output, 0) }

        var text = output
        for region in regions.sorted(by: { $0.out.lowerBound > $1.out.lowerBound }) {
            let start = outputWords[region.out.lowerBound].range.lowerBound
            var range = start..<outputWords[region.out.upperBound - 1].range.upperBound
            var replacement = region.src.isEmpty ? ""
                : String(source[sourceWords[region.src.lowerBound].range.lowerBound..<sourceWords[region.src.upperBound - 1].range.upperBound])
            if replacement.isEmpty {
                // Drop one neighbouring space run so no gap is left behind.
                var lower = range.lowerBound
                while lower > text.startIndex, text[text.index(before: lower)] == " " { lower = text.index(before: lower) }
                if lower < range.lowerBound { range = lower..<range.upperBound }
                else {
                    var upper = range.upperBound
                    while upper < text.endIndex, text[upper] == " " { upper = text.index(after: upper) }
                    range = range.lowerBound..<upper
                }
            } else if text[start].isUppercase, isSentenceStart(start, in: text) {
                replacement = replacement.prefix(1).uppercased() + replacement.dropFirst()
            }
            text.replaceSubrange(range, with: replacement)
        }
        return (text, regions.count)
    }

    /// Case- and accent-insensitive runs of letters and digits.
    private static func words(in text: String) -> [Word] {
        var result: [Word] = []
        var start: String.Index?
        for index in text.indices {
            if text[index].isLetter || text[index].isNumber {
                if start == nil { start = index }
            } else if let first = start {
                result.append(word(text, first..<index)); start = nil
            }
        }
        if let first = start { result.append(word(text, first..<text.endIndex)) }
        return result
    }

    private static func word(_ text: String, _ range: Range<String.Index>) -> Word {
        Word(key: text[range].folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: nil),
             range: range)
    }

    private static func occurrences(of term: [String], in keys: [String]) -> [Range<Int>] {
        guard keys.count >= term.count else { return [] }
        return (0...(keys.count - term.count)).compactMap { start in
            keys[start..<(start + term.count)].elementsEqual(term) ? start..<(start + term.count) : nil
        }
    }

    /// Output index → aligned source index (longest common subsequence of
    /// words). Nil for very long, heavily rewritten text, which is left as is.
    private static func alignment(_ source: [String], _ output: [String]) -> [Int?]? {
        var matches = [Int?](repeating: nil, count: output.count)
        var prefix = 0
        while prefix < min(source.count, output.count), source[prefix] == output[prefix] {
            matches[prefix] = prefix; prefix += 1
        }
        var suffix = 0
        while suffix < min(source.count, output.count) - prefix,
              source[source.count - 1 - suffix] == output[output.count - 1 - suffix] {
            matches[output.count - 1 - suffix] = source.count - 1 - suffix; suffix += 1
        }
        let src = Array(source[prefix..<(source.count - suffix)]), out = Array(output[prefix..<(output.count - suffix)])
        guard !src.isEmpty, !out.isEmpty else { return matches }
        guard src.count * out.count <= 4_000_000 else { return nil }
        let width = out.count + 1
        var table = [UInt16](repeating: 0, count: (src.count + 1) * width)
        for i in stride(from: src.count - 1, through: 0, by: -1) {
            for j in stride(from: out.count - 1, through: 0, by: -1) {
                table[i * width + j] = src[i] == out[j] ? table[(i + 1) * width + j + 1] + 1
                    : max(table[(i + 1) * width + j], table[i * width + j + 1])
            }
        }
        var i = 0, j = 0
        while i < src.count, j < out.count {
            if src[i] == out[j] { matches[prefix + j] = prefix + i; i += 1; j += 1 }
            else if table[(i + 1) * width + j] >= table[i * width + j + 1] { i += 1 }
            else { j += 1 }
        }
        return matches
    }

    /// True when some run of up to `maxWords` source words is spelled within
    /// about a third of the term's letters ("zephyr core", "cloud code").
    private static func hasSimilarSpan(to term: String, in source: [String], maxWords: Int) -> Bool {
        for start in source.indices {
            var joined = ""
            for end in start..<min(source.count, start + maxWords) {
                joined += source[end]
                let longest = max(joined.count, term.count)
                if editDistance(joined, term) * 3 <= longest { return true }
                if joined.count > term.count * 2 { break }
            }
        }
        return false
    }

    private static func editDistance(_ lhs: String, _ rhs: String) -> Int {
        let a = Array(lhs), b = Array(rhs)
        guard !a.isEmpty, !b.isEmpty else { return max(a.count, b.count) }
        var row = Array(0...b.count)
        for i in 1...a.count {
            var diagonal = row[0]
            row[0] = i
            for j in 1...b.count {
                let above = row[j]
                row[j] = a[i - 1] == b[j - 1] ? diagonal : min(diagonal, above, row[j - 1]) + 1
                diagonal = above
            }
        }
        return row[b.count]
    }

    private static func isSentenceStart(_ index: String.Index, in text: String) -> Bool {
        let before = text[..<index].trimmingCharacters(in: .whitespaces)
        guard let last = before.last else { return true }
        return ".!?\n".contains(last)
    }
}
