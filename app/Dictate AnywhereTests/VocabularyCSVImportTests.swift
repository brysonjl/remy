import XCTest
@testable import Dictate_Anywhere

final class VocabularyCSVImportTests: XCTestCase {
    private func parse(_ text: String) throws -> VocabularyCSVImport.Parsed {
        try VocabularyCSVImport.parse(Data(text.utf8))
    }

    func testOneColumnIsATermAndTwoColumnsAreMisspellingThenCorrection() throws {
        let parsed = try parse("Remy\nwhisper,Wispr\r\nzephyr core,ZephyrCore\n")
        XCTAssertEqual(parsed.entries, [
            .init(term: "Remy", misspelling: nil),
            .init(term: "Wispr", misspelling: "whisper"),
            .init(term: "ZephyrCore", misspelling: "zephyr core"),
        ])
        XCTAssertEqual(parsed.skipped, 0)
    }

    func testQuotedFieldsWithCommasEscapedQuotesAndLineBreaks() throws {
        let parsed = try parse("\"Smith, Jones\"\n\"say \"\"hi\"\"\",Hi\n\"two\nlines\",Term\n")
        XCTAssertEqual(parsed.entries, [
            .init(term: "Smith, Jones", misspelling: nil),
            .init(term: "Hi", misspelling: "say \"hi\""),
            .init(term: "Term", misspelling: "two\nlines"),
        ])
    }

    func testRowsSplitOnCommasOutsideQuotesOnly() {
        XCTAssertEqual(VocabularyCSVImport.rows(in: "a,\"b,c\",d\ne"), [["a", "b,c", "d"], ["e"]])
        XCTAssertEqual(VocabularyCSVImport.rows(in: "\"\",x"), [["", "x"]])
        XCTAssertEqual(VocabularyCSVImport.rows(in: "ab\"c\"d"), [["ab\"c\"d"]])
    }

    func testHeaderRowIsSkippedOnlyWhenFirst() throws {
        XCTAssertEqual(try parse("misspelling,correction\nwhisper,Wispr").entries,
                       [.init(term: "Wispr", misspelling: "whisper")])
        XCTAssertEqual(try parse("\nWord\nRemy").entries, [.init(term: "Remy", misspelling: nil)])
        XCTAssertEqual(try parse("Remy\nword").entries,
                       [.init(term: "Remy", misspelling: nil), .init(term: "word", misspelling: nil)])
    }

    func testInvalidRowsAreSkippedAndCounted() throws {
        let long = String(repeating: "x", count: 61)
        let parsed = try parse("\n,Wispr\n\(long)\na,b,c\nok,\n  \nfine\n\(String(repeating: "y", count: 60))\n")
        XCTAssertEqual(parsed.entries.map(\.term), ["ok", "fine", String(repeating: "y", count: 60)])
        XCTAssertEqual(parsed.skipped, 3)
    }

    func testByteOrderMarkIsIgnored() throws {
        XCTAssertEqual(try parse("\u{FEFF}Remy").entries, [.init(term: "Remy", misspelling: nil)])
    }

    func testLimits() throws {
        let rows = (0..<1_005).map { "term\($0)" }.joined(separator: "\n")
        let parsed = try parse(rows)
        XCTAssertEqual(parsed.entries.count, 1_000)
        XCTAssertEqual(parsed.skipped, 5)
        let oversized = Data(repeating: UInt8(ascii: "a"), count: VocabularyCSVImport.maximumBytes + 1)
        XCTAssertThrowsError(try VocabularyCSVImport.parse(oversized)) {
            XCTAssertEqual($0 as? VocabularyCSVImport.Failure, .tooLarge)
        }
        XCTAssertThrowsError(try VocabularyCSVImport.parse(Data([0xFF, 0xFE, 0x00]))) {
            XCTAssertEqual($0 as? VocabularyCSVImport.Failure, .unreadable)
        }
    }

    func testReadChecksFileSizeBeforeLoading() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("VocabularyCSVImportTests-\(UUID()).csv")
        defer { try? FileManager.default.removeItem(at: url) }
        try Data("whisper,Wispr\n".utf8).write(to: url)
        XCTAssertEqual(try VocabularyCSVImport.read(contentsOf: url).entries, [.init(term: "Wispr", misspelling: "whisper")])
        try Data(count: VocabularyCSVImport.maximumBytes + 1).write(to: url)
        XCTAssertThrowsError(try VocabularyCSVImport.read(contentsOf: url)) {
            XCTAssertEqual($0 as? VocabularyCSVImport.Failure, .tooLarge)
        }
    }

    func testMergeIsCaseInsensitiveOnTermsAndKeepsExistingSpelling() throws {
        let parsed = try parse("remy\nNew Term\nwhisper,wispr\nWISPER,Wispr\nwhisper,Wispr\nZephyrCore,ZephyrCore")
        let merged = VocabularyCSVImport.merge(parsed, terms: ["Remy", "Wispr"], misspellings: ["Wispr": ["whisper"]])
        XCTAssertEqual(merged.terms, ["Remy", "Wispr", "New Term", "ZephyrCore"])
        XCTAssertEqual(merged.misspellings, ["Wispr": ["whisper", "WISPER"]])
        XCTAssertEqual(merged.imported, 3) // New Term, WISPER, ZephyrCore
        XCTAssertEqual(merged.skipped, 3)  // remy, both duplicate whisper rows
    }
}
