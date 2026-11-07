import XCTest
@testable import Dictate_Anywhere

final class RecognitionSessionConfigurationTests: XCTestCase {
    private func configuration(vocabularyEnabled: Bool = true, microphoneUID: String? = nil) -> RecognitionSessionConfiguration {
        RecognitionSessionConfiguration(modelChoice: .englishOnly,
            expectedLanguage: .english, nativeLanguageCode: "en-US", appleSpeechLanguage: .english,
            acousticVocabularyEnabled: vocabularyEnabled, vocabularyTerms: ["Ixway", "Core ML"],
            showTextPreview: true, microphoneUID: microphoneUID)
    }

    func testVocabularyDisabledExcludesTermsWithoutMutatingStoredSnapshot() {
        let snapshot = configuration(vocabularyEnabled: false)
        XCTAssertEqual(snapshot.vocabularyTerms, ["Ixway", "Core ML"])
        XCTAssertTrue(snapshot.activeVocabularyTerms.isEmpty)
        XCTAssertEqual(configuration().activeVocabularyTerms, ["Ixway", "Core ML"])
    }

    func testConfigurationDoesNotRetainMutableVocabularySource() {
        var terms = ["Original"]
        let snapshot = RecognitionSessionConfiguration(modelChoice: .englishOnly,
            expectedLanguage: .english, nativeLanguageCode: "en-US", appleSpeechLanguage: .english,
            acousticVocabularyEnabled: true, vocabularyTerms: terms,
            showTextPreview: false, microphoneUID: "selected-device")
        terms[0] = "Changed while recording"
        XCTAssertEqual(snapshot.activeVocabularyTerms, ["Original"])
        XCTAssertEqual(snapshot.microphoneUID, "selected-device")
        XCTAssertFalse(snapshot.showTextPreview)
    }

    func testAbsentMicrophoneSelectionRemainsAbsentInSnapshot() {
        XCTAssertNil(configuration().microphoneUID)
        XCTAssertEqual(configuration(microphoneUID: "explicit-device").microphoneUID, "explicit-device")
    }
}
