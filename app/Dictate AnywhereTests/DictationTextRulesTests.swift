import XCTest
@testable import Dictate_Anywhere

final class DictationTextRulesTests: XCTestCase {
    private let corrections = DictationTextRules(corrections: [
        .init(term: "Wispr", misspellings: ["whisper", "wisper"]),
        .init(term: "ZephyrCore", misspellings: ["zephyr core"]),
    ])

    private func snippet(_ trigger: String, _ expansion: String) -> DictationSnippet {
        DictationSnippet(trigger: trigger, expansion: expansion)
    }

    // MARK: - Corrections

    func testEmptyRulesAreANoOp() {
        let rules = DictationTextRules()
        XCTAssertTrue(rules.isEmpty)
        let text = "  Keep  this exactly, please.  "
        XCTAssertEqual(rules.applyingCorrections(to: text), text)
        XCTAssertEqual(rules.expandingSnippets(in: text), text)
    }

    func testTermWithoutMisspellingsChangesNothing() {
        let rules = DictationTextRules(corrections: [.init(term: "Wispr", misspellings: [])])
        XCTAssertTrue(rules.isEmpty)
        XCTAssertEqual(rules.applyingCorrections(to: "whisper"), "whisper")
    }

    func testCaseInsensitiveInputUsesSavedSpellingExactly() {
        XCTAssertEqual(corrections.applyingCorrections(to: "I use whisper daily"), "I use Wispr daily")
        XCTAssertEqual(corrections.applyingCorrections(to: "WHISPER is great"), "Wispr is great")
        XCTAssertEqual(corrections.applyingCorrections(to: "Zephyr Core ships"), "ZephyrCore ships")
    }

    func testWholeWordsOnly() {
        XCTAssertEqual(corrections.applyingCorrections(to: "whispered whispers whisper_x xwhisper whisper2"),
                       "whispered whispers whisper_x xwhisper whisper2")
        XCTAssertEqual(corrections.applyingCorrections(to: "zephyr cores"), "zephyr cores")
    }

    func testAdjacentPunctuationAndPossessives() {
        XCTAssertEqual(corrections.applyingCorrections(to: "(whisper), \"whisper\"! whisper."),
                       "(Wispr), \"Wispr\"! Wispr.")
        XCTAssertEqual(corrections.applyingCorrections(to: "zephyr core's roadmap"), "ZephyrCore's roadmap")
        XCTAssertEqual(corrections.applyingCorrections(to: "zephyr core’s roadmap"), "ZephyrCore’s roadmap")
        XCTAssertEqual(corrections.applyingCorrections(to: "whisper-based"), "Wispr-based")
    }

    func testPhraseToleratesWhitespaceRunsButNotOtherSeparators() {
        XCTAssertEqual(corrections.applyingCorrections(to: "zephyr  core\nis"), "ZephyrCore\nis")
        XCTAssertEqual(corrections.applyingCorrections(to: "zephyr-core"), "zephyr-core")
    }

    func testLongestMatchWinsOnOverlap() {
        let rules = DictationTextRules(corrections: [
            .init(term: "Core", misspellings: ["core"]),
            .init(term: "ZephyrCore", misspellings: ["zephyr core"]),
            .init(term: "CoreDB", misspellings: ["core db"]),
        ])
        XCTAssertEqual(rules.applyingCorrections(to: "zephyr core"), "ZephyrCore")
        XCTAssertEqual(rules.applyingCorrections(to: "the core db"), "the CoreDB")
        // A longer later match beats a shorter earlier one that overlaps it.
        let overlapping = DictationTextRules(corrections: [
            .init(term: "AB", misspellings: ["a b"]),
            .init(term: "BCD", misspellings: ["b c d"]),
        ])
        XCTAssertEqual(overlapping.applyingCorrections(to: "a b c d"), "a BCD")
    }

    func testReplacementsNeverCascade() {
        let rules = DictationTextRules(corrections: [
            .init(term: "beta", misspellings: ["alpha"]),
            .init(term: "gamma", misspellings: ["beta"]),
        ])
        XCTAssertEqual(rules.applyingCorrections(to: "alpha beta"), "beta gamma")
    }

    func testCorrectionLeavesSnippetTriggersForLaterExpansion() {
        let rules = DictationTextRules(corrections: [.init(term: "Wispr", misspellings: ["whisper"])],
                                       snippets: [snippet("whisper link", "https://example.com/w")])
        let corrected = rules.applyingCorrections(to: "see whisper link and whisper")
        XCTAssertEqual(corrected, "see whisper link and Wispr")
        XCTAssertEqual(rules.expandingSnippets(in: corrected), "see https://example.com/w and Wispr")
    }

    func testCJKIsUnchangedAndCJKRulesMatchInUnsegmentedText() {
        XCTAssertEqual(corrections.applyingCorrections(to: "我们用whisper写。"), "我们用Wispr写。")
        XCTAssertEqual(corrections.applyingCorrections(to: "你好，世界。"), "你好，世界。")
        let rules = DictationTextRules(corrections: [.init(term: "北京大学", misspellings: ["北经大学"])])
        XCTAssertEqual(rules.applyingCorrections(to: "我在北经大学读书。"), "我在北京大学读书。")
        let japanese = DictationTextRules(corrections: [.init(term: "ウィスパー", misspellings: ["うぃすぱー"])])
        XCTAssertEqual(japanese.applyingCorrections(to: "これはうぃすぱーです"), "これはウィスパーです")
    }

    // MARK: - Snippets

    func testWholeUtteranceSnippetBecomesExactlyTheExpansion() {
        let rules = DictationTextRules(snippets: [snippet("my calendar link", "https://cal.example/me")])
        XCTAssertEqual(rules.expandingSnippets(in: "My calendar link."), "https://cal.example/me")
        XCTAssertEqual(rules.expandingSnippets(in: "  my calendar link  "), "https://cal.example/me")
        XCTAssertEqual(rules.expandingSnippets(in: "\"My Calendar Link!\""), "https://cal.example/me")
    }

    func testSnippetInsideSentenceKeepsSurroundingText() {
        let rules = DictationTextRules(snippets: [snippet("my address", "1 Main St\nSpringfield")])
        XCTAssertEqual(rules.expandingSnippets(in: "Send it to my address, thanks."),
                       "Send it to 1 Main St\nSpringfield, thanks.")
        XCTAssertEqual(rules.expandingSnippets(in: "my addresses"), "my addresses")
    }

    func testEmptyExpansionRemovesTheTriggerCleanly() {
        let rules = DictationTextRules(snippets: [snippet("over and out", "")])
        XCTAssertEqual(rules.expandingSnippets(in: "That's all over and out."), "That's all.")
        XCTAssertEqual(rules.expandingSnippets(in: "over and out see you"), "see you")
        XCTAssertEqual(rules.expandingSnippets(in: "see over and out you"), "see you")
        XCTAssertEqual(rules.expandingSnippets(in: "Over and out."), "")
    }

    func testSnippetBeatsCorrectionOnEqualOverlap() {
        let rules = DictationTextRules(corrections: [.init(term: "Sig", misspellings: ["signoff"])],
                                       snippets: [snippet("signoff", "Best,\nAlex")])
        XCTAssertEqual(rules.applyingCorrections(to: "ok signoff"), "ok signoff")
        XCTAssertEqual(rules.expandingSnippets(in: "ok signoff"), "ok Best,\nAlex")
    }

    func testSnippetBeatsLongerPartiallyOverlappingCorrection() {
        let rules = DictationTextRules(corrections: [.init(term: "Contacts", misspellings: ["address book entry"])],
                                       snippets: [snippet("my email address", "alex@example.com")])
        let corrected = rules.applyingCorrections(to: "send my email address book entry")
        XCTAssertEqual(corrected, "send my email address book entry")
        XCTAssertEqual(rules.expandingSnippets(in: corrected), "send alex@example.com book entry")
    }

    func testExpansionIsNotRescannedForTriggers() {
        let rules = DictationTextRules(snippets: [snippet("ping", "ping pong"), snippet("pong", "ball")])
        XCTAssertEqual(rules.expandingSnippets(in: "say ping now"), "say ping pong now")
    }

    func testSnippetValidationTrimsAndEnforcesLimits() {
        let trimmed = snippet("  hi there \n", "\n Hello!  ").validated()
        XCTAssertEqual(trimmed?.trigger, "hi there")
        XCTAssertEqual(trimmed?.expansion, "Hello!")
        XCTAssertNotNil(snippet("x", "").validated())
        XCTAssertNil(snippet("   ", "text").validated())
        XCTAssertNil(snippet(String(repeating: "a", count: 61), "text").validated())
        XCTAssertNotNil(snippet(String(repeating: "a", count: 60), String(repeating: "b", count: 4_000)).validated())
        XCTAssertNil(snippet("ok", String(repeating: "b", count: 4_001)).validated())
    }

    func testSnippetCodableRoundTrip() throws {
        let original = [snippet("sig", "Best,\nAlex")]
        let decoded = try JSONDecoder().decode([DictationSnippet].self, from: JSONEncoder().encode(original))
        XCTAssertEqual(decoded, original)
    }
}
