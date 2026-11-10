import XCTest
@testable import Dictate_Anywhere

final class TranscriptCleanupIntegrityTests: XCTestCase {
    func testNumbersMayBeFormattedButCannotDisappearOrBeDeduplicated() {
        XCTAssertTrue(TranscriptCleanupIntegrity.preservesNumericLiterals(from: "Record 1005. Invoice 4182, cost 12.50.", in: "Record 1,005. Invoice 4,182 costs 12.50."))
        XCTAssertTrue(TranscriptCleanupIntegrity.preservesNumericLiterals(from: "１４ October", in: "October 14"))
        XCTAssertFalse(TranscriptCleanupIntegrity.preservesNumericLiterals(from: "Record 1005. Keep 4182.", in: "Keep 4182."))
        XCTAssertFalse(TranscriptCleanupIntegrity.preservesNumericLiterals(from: "4182, then 4182", in: "4182"))
        XCTAssertFalse(TranscriptCleanupIntegrity.preservesNumericLiterals(from: "12.50", in: "12.05"))
        XCTAssertFalse(TranscriptCleanupIntegrity.preservesNumericLiterals(from: "1.005", in: "1005"))
        XCTAssertFalse(TranscriptCleanupIntegrity.preservesNumericLiterals(from: "12", in: "twelve"))
        XCTAssertTrue(TranscriptCleanupIntegrity.preservesNumericLiterals(from: "No digits.", in: "No digits."))
    }

    // MARK: - Known terms

    private func guarded(_ source: String, _ output: String, _ terms: [String] = ["Zephyrcore"]) -> String {
        TranscriptCleanupIntegrity.revertingUnsupportedTerms(source: source, output: output, terms: terms).text
    }

    /// Outputs observed from the on-device model with only "Zephyrcore" in the vocabulary.
    func testTermInsertedOverUnrelatedOrNoWordsIsReverted() {
        XCTAssertEqual(guarded("the petri dish results came back this morning",
                               "The Zephyrcore dish results came back this morning."),
                       "The petri dish results came back this morning.")
        XCTAssertEqual(guarded("our core values are honesty and speed", "our core values are honesty and Zephyrcore"),
                       "our core values are honesty and speed")
        XCTAssertEqual(guarded("the company is growing fast and we need to hire",
                               "the company is growing fast and we need to hire Zephyrcore"),
                       "the company is growing fast and we need to hire")
        XCTAssertEqual(guarded("we need to hire", "We need to hire Zephyrcore."), "We need to hire.")
        XCTAssertEqual(guarded("thanks", "Zephyrcore thanks"), "thanks")
    }

    func testSimilarSoundingCorrectionsAreKept() {
        XCTAssertEqual(guarded("zephyr core is launching next week", "Zephyrcore is launching next week."),
                       "Zephyrcore is launching next week.")
        XCTAssertEqual(guarded("I work at zephyrcor", "I work at Zephyrcore."), "I work at Zephyrcore.")
        XCTAssertEqual(guarded("I work at zephyr car", "I work at Zephyrcore."), "I work at Zephyrcore.")
        XCTAssertEqual(guarded("open cloud code now", "Open Claude Code now.", ["Claude Code"]), "Open Claude Code now.")
        XCTAssertEqual(guarded("uh we use whisper", "We use Wispr.", ["Wispr"]), "We use Wispr.")
    }

    func testSpokenTermsAndOtherCleanupAreUntouched() {
        XCTAssertEqual(guarded("uh Zephyrcore ships friday", "Zephyrcore ships Friday."), "Zephyrcore ships Friday.")
        XCTAssertEqual(guarded("reserve friday no saturday", "Reserve Saturday."), "Reserve Saturday.")
        XCTAssertEqual(guarded("anything", "Anything.", []), "Anything.")
        // Only the bad region is restored; the rest of the cleanup stays.
        XCTAssertEqual(guarded("uh so the petri dish is ready um and zephyr core ships",
                               "The Zephyrcore dish is ready, and Zephyrcore ships."),
                       "The petri dish is ready, and Zephyrcore ships.")
        let result = TranscriptCleanupIntegrity.revertingUnsupportedTerms(
            source: "honesty and speed", output: "Honesty and Zephyrcore.", terms: ["Zephyrcore"])
        XCTAssertEqual(result.reverted, 1)
    }

    func testMultiWordNamesFromTheScreenAreGuardedToo() {
        XCTAssertEqual(guarded("send it to ok wanda", "Send it to Joaquín Ortega.", ["Joaquín Ortega"]),
                       "Send it to ok wanda.")
        XCTAssertEqual(guarded("send it to joaquin ortega", "Send it to Joaquín Ortega.", ["Joaquín Ortega"]),
                       "Send it to Joaquín Ortega.")
    }
}
