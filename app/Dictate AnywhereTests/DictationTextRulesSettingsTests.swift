import XCTest
@testable import Dictate_Anywhere

/// Snapshots and restores every touched preference so the test host's
/// persisted settings are left unchanged.
@MainActor
final class DictationTextRulesSettingsTests: XCTestCase {
    private var savedVocabulary: [String] = []
    private var savedMisspellings: [String: [String]] = [:]
    private var savedSnippets: [DictationSnippet] = []

    override func setUp() async throws {
        try await super.setUp()
        let settings = Settings.shared
        savedVocabulary = settings.customVocabulary
        savedMisspellings = settings.vocabularyMisspellings
        savedSnippets = settings.dictationSnippets
        settings.customVocabulary = []
        settings.vocabularyMisspellings = [:]
        settings.dictationSnippets = []
    }

    override func tearDown() async throws {
        let settings = Settings.shared
        settings.customVocabulary = savedVocabulary
        settings.vocabularyMisspellings = savedMisspellings
        settings.dictationSnippets = savedSnippets
        try await super.tearDown()
    }

    func testLegacyPlainVocabularyLoadsAsTermsWithoutMisspellings() throws {
        let suiteName = "DictationTextRulesSettingsTests.Legacy.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        defaults.set(["Remy", "Parakeet"], forKey: "customVocabulary")
        XCTAssertEqual(Settings.loadVocabularyMisspellings(from: defaults), [:])
        XCTAssertEqual(Settings.loadDictationSnippets(from: defaults), [])
        XCTAssertEqual(defaults.object(forKey: "customVocabulary") as? [String], ["Remy", "Parakeet"])
    }

    func testMalformedOrInvalidStoredValuesAreDropped() throws {
        let suiteName = "DictationTextRulesSettingsTests.Invalid.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        defaults.set(Data("not json".utf8), forKey: "vocabularyMisspellings")
        defaults.set(Data("[{}]".utf8), forKey: "dictationSnippets")
        XCTAssertEqual(Settings.loadVocabularyMisspellings(from: defaults), [:])
        XCTAssertEqual(Settings.loadDictationSnippets(from: defaults), [])
        let long = String(repeating: "x", count: 61)
        defaults.set(try JSONEncoder().encode(["Wispr": ["whisper", long], "Empty": [long]]), forKey: "vocabularyMisspellings")
        let snippets = [DictationSnippet(trigger: " sig ", expansion: "Best"), DictationSnippet(trigger: long, expansion: "")]
        defaults.set(try JSONEncoder().encode(snippets), forKey: "dictationSnippets")
        XCTAssertEqual(Settings.loadVocabularyMisspellings(from: defaults), ["Wispr": ["whisper"]])
        XCTAssertEqual(Settings.loadDictationSnippets(from: defaults).map(\.trigger), ["sig"])
    }

    func testCorrectionAddsTermAndPersistsSeparatelyFromPlainVocabulary() throws {
        let settings = Settings.shared
        settings.customVocabulary = ["Remy"]
        XCTAssertTrue(settings.setVocabularyCorrection(term: " Wispr ", misspellings: ["whisper", "WHISPER", " ", "Wispr", "wisper"]))
        XCTAssertEqual(settings.customVocabulary, ["Remy", "Wispr"])
        XCTAssertEqual(settings.vocabularyCorrections, [.init(term: "Wispr", misspellings: ["whisper", "wisper"])])
        XCTAssertEqual(UserDefaults.standard.object(forKey: "customVocabulary") as? [String], ["Remy", "Wispr"])
        let stored = try JSONDecoder().decode([String: [String]].self,
            from: XCTUnwrap(UserDefaults.standard.data(forKey: "vocabularyMisspellings")))
        XCTAssertEqual(stored, ["Wispr": ["whisper", "wisper"]])
        XCTAssertFalse(settings.setVocabularyCorrection(term: String(repeating: "x", count: 61), misspellings: ["x"]))
    }

    func testRemovingTermDropsItsMisspellings() {
        let settings = Settings.shared
        settings.setVocabularyCorrection(term: "Wispr", misspellings: ["whisper"])
        settings.setVocabularyCorrection(term: "wispr", misspellings: ["wisper"])
        XCTAssertEqual(settings.customVocabulary, ["Wispr"], "Terms match case-insensitively")
        XCTAssertEqual(settings.vocabularyMisspellings, ["Wispr": ["wisper"]])
        settings.customVocabulary.removeAll { $0 == "Wispr" }
        XCTAssertEqual(settings.vocabularyMisspellings, [:])
    }

    func testImportMergesIntoSettings() throws {
        let settings = Settings.shared
        settings.customVocabulary = ["Remy"]
        let result = settings.importVocabulary(try VocabularyCSVImport.parse(Data("word\nremy\nwhisper,Wispr\n".utf8)))
        XCTAssertEqual(result.imported, 1)
        XCTAssertEqual(result.skipped, 1)
        XCTAssertEqual(settings.customVocabulary, ["Remy", "Wispr"])
        XCTAssertEqual(settings.vocabularyMisspellings, ["Wispr": ["whisper"]])
    }

    func testSessionCaptureFreezesCorrectionsAndSnippets() {
        let settings = Settings.shared
        let savedMode = settings.transcriptPostProcessingMode
        defer { settings.transcriptPostProcessingMode = savedMode }
        settings.transcriptPostProcessingMode = .none
        settings.setVocabularyCorrection(term: "Wispr", misspellings: ["whisper"])
        settings.dictationSnippets = [DictationSnippet(trigger: "sig", expansion: "Best")]
        let captured = RemyCleanupRuntime.capture(settings: settings).configuration
        settings.dictationSnippets = []
        settings.customVocabulary = []
        XCTAssertEqual(captured.capture.vocabulary, ["Wispr"])
        XCTAssertEqual(captured.capture.textRules.corrections, [.init(term: "Wispr", misspellings: ["whisper"])])
        XCTAssertEqual(captured.capture.textRules.snippets.map(\.trigger), ["sig"])
    }
}
