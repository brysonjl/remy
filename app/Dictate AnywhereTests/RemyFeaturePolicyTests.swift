import XCTest
@testable import Dictate_Anywhere

final class RemyFeaturePolicyTests: XCTestCase {
    func testSupportedSpeechChoicesAreLocalAndUnique() {
        XCTAssertEqual(RemyFeaturePolicy.supportedEngineChoices, [.parakeet, .appleSpeech])
        XCTAssertEqual(Set(RemyFeaturePolicy.supportedEngineChoices.map(\.rawValue)).count, 2)
        XCTAssertFalse(RemyFeaturePolicy.allowsCloudSpeechRecognition)
    }

    func testEveryStoredEngineChoiceResolvesToSupportedLocalRoute() {
        for choice in TranscriptionEngineChoice.allCases {
            let effective = RemyFeaturePolicy.effectiveEngineChoice(choice)
            XCTAssertTrue(RemyFeaturePolicy.supportedEngineChoices.contains(effective))
            XCTAssertEqual(RemyFeaturePolicy.effectiveEngineChoice(effective), effective)
        }
        XCTAssertEqual(RemyFeaturePolicy.effectiveEngineChoice(.parakeet), .parakeet)
        XCTAssertEqual(RemyFeaturePolicy.effectiveEngineChoice(.appleSpeech), .appleSpeech)
        XCTAssertEqual(RemyFeaturePolicy.effectiveEngineChoice(.assemblyAI), .parakeet)
    }

    func testContextGatheringAndExplicitRemoteSharingAreSupported() {
        XCTAssertTrue(RemyFeaturePolicy.allowsDictationContextGathering)
        XCTAssertTrue(RemyFeaturePolicy.allowsRemoteDictationContextSharing)
    }
}
