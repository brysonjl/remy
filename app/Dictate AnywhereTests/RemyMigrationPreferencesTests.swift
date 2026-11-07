import Foundation
import XCTest
@testable import Dictate_Anywhere

final class RemyMigrationPreferencesTests: XCTestCase {
    private func reject(_ source: [String: Any], as failure: RemyMigrationPreferences.Failure,
                        file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertThrowsError(try RemyMigrationPreferences.sanitized(source), file: file, line: line) {
            XCTAssertEqual($0 as? RemyMigrationPreferences.Failure, failure, file: file, line: line)
        }
    }
    func testUnknownCredentialsLoginAndUnsupportedRoutesAreExcludedWithoutInspectingValues() throws {
        let excluded = ["openRouterAPIKey", "openAICompatibleAPIKey", "assemblyAIAPIKey",
                        "openRouterAPIKeyEnvironmentVariable", "launchAtLogin", "assemblyAIRegion",
                        "assemblyAIInstruction", "shareDictationContextWithRemoteProviders",
                        "pendingVocabularyModeRestore", "legacyAppleSpeechMigrationPending", "unknownFutureSetting"]
        var source: [String: Any] = [:]
        for key in excluded { source[key] = NSObject() } // Not a plist value; must never be inspected/serialized.
        let sanitized = try RemyMigrationPreferences.sanitized(source)
        XCTAssertEqual(Set(sanitized.keys), [RemyMigrationPreferences.schemaKey])
        for key in excluded { XCTAssertFalse(RemyMigrationPreferences.migratableKeys.contains(key)) }
        let marker = "synthetic-credential-not-to-export"
        let bytes = try RemyMigrationPreferences.encoded(["openAICompatibleAPIKey": marker,
            "openRouterAPIKeyEnvironmentVariable": marker])
        XCTAssertNil(bytes.range(of: Data(marker.utf8)))
    }
    func testAppAwarenessOptOutMigratesWhileRemoteSharingDoesNot() throws {
        for enabled in [false, true] {
            let output = try RemyMigrationPreferences.sanitized([
                "dictationContextAwarenessEnabled": enabled,
                "shareDictationContextWithRemoteProviders": true])
            XCTAssertEqual(output["dictationContextAwarenessEnabled"] as? Bool, enabled)
            XCTAssertNil(output["shareDictationContextWithRemoteProviders"])
        }
    }
    func testSchemaPreflightAcceptsAbsentZeroAndOneAndNormalizesToCurrent() throws {
        for source: [String: Any] in [[:], [RemyMigrationPreferences.schemaKey: 0],
                                     [RemyMigrationPreferences.schemaKey: 1]] {
            let output = try RemyMigrationPreferences.sanitized(source)
            XCTAssertEqual(output[RemyMigrationPreferences.schemaKey] as? Int, 1)
        }
        reject([RemyMigrationPreferences.schemaKey: 2], as: .unsupportedSchema)
        reject([RemyMigrationPreferences.schemaKey: UInt64.max], as: .unsupportedSchema)
        for invalid: Any in [true, "1", -1, NSNumber(value: 1.5), NSNull(), [1]] {
            reject([RemyMigrationPreferences.schemaKey: invalid], as: .malformedSchema)
        }
    }
    func testHistoryOpaqueBytesArePreservedExactlyAndNeverCappedToFiftyEntries() throws {
        let history = try JSONSerialization.data(withJSONObject: (0..<75).map {
            ["id": UUID().uuidString, "text": "原文 👩🏽‍💻 \($0)", "createdAt": $0] as [String: Any]
        }, options: [.prettyPrinted])
        let opaque = Data([0x00, 0xff, 0x80, 0x0a])
        for data in [history, opaque] {
            let output = try RemyMigrationPreferences.sanitized(["transcriptHistory": data])
            XCTAssertEqual(output["transcriptHistory"] as? Data, data)
            let encoded = try RemyMigrationPreferences.encoded(["transcriptHistory": data])
            let decoded = try XCTUnwrap(PropertyListSerialization.propertyList(from: encoded, options: [], format: nil) as? [String: Any])
            XCTAssertEqual(decoded["transcriptHistory"] as? Data, data)
        }
    }
    func testProviderProfileModePromptsVocabularyDestinationAndSoundChoicesRemainExact() throws {
        let source: [String: Any] = ["engineChoice": "parakeet", "parakeetModelChoice": "englishOnly",
            "selectedLanguage": "english", "appleSpeechLanguage": "english", "transcriptPostProcessingMode": "profile",
            "openAICompatibleBaseURL": "https://ollama.com/v1", "openAICompatibleModel": "fixture-model:cloud",
            "openAICompatiblePostProcessingPrompt": "Preserve quoted words: oh wait. 原文 🦾\nDo not summarize.",
            "ollamaModel": "fixture-local:4b", "openRouterModel": "fixture/provider",
            "ollamaReasoningEnabled": false, "openRouterReasoningEnabled": true,
            "customVocabulary": ["Cafe\u{301}", "NextGen", "原文"], "fillerWordsToRemove": ["um", "呃"],
            "remyDictationDestination": "currentField", "remyDictationStartSound": "none",
            "remyDictationCompletionSound": "custom:00000000-0000-0000-0000-000000000001.wav",
            "soundEffectsVolume": 0.35, "remyHistoryEnabled": false]
        let output = try RemyMigrationPreferences.sanitized(source)
        for key in source.keys {
            // Values here are synthetic fixtures only; no production domains accessed.
            XCTAssertTrue(NSDictionary(dictionary: [key: source[key]!]).isEqual(to: [key: output[key]!]))
        }
    }
    func testBoolAndIntegerTypesCannotBeConfusedAndVolumeMustBeFiniteInRange() throws {
        reject(["holdToCancel": 1], as: .invalidValue)
        reject(["hotkeyKeyCode": true], as: .invalidValue)
        reject(["hotkeyModifiers": 1.5], as: .invalidValue)
        reject(["hotkeyKeyCode": -1], as: .invalidValue)
        reject(["hotkeyKeyCode": 65_536], as: .invalidValue)
        for invalid: Any in [true, Double.nan, Double.infinity, -0.1, 1.1, "0.5"] {
            reject(["soundEffectsVolume": invalid], as: .invalidValue)
        }
        let output = try RemyMigrationPreferences.sanitized(["holdToCancel": true, "hotkeyKeyCode": 61,
            "hotkeyModifiers": UInt64(1 << 19), "soundEffectsVolume": 0.5])
        XCTAssertEqual(output["holdToCancel"] as? Bool, true)
        XCTAssertEqual((output["hotkeyKeyCode"] as? NSNumber)?.intValue, 61)
    }
    func testKnownPayloadsAreTypedAndBounded() {
        reject(["openAICompatiblePostProcessingPrompt": String(repeating: "x", count: 65_537)], as: .payloadTooLarge)
        reject(["openAICompatibleModel": String(repeating: "x", count: 513)], as: .payloadTooLarge)
        reject(["customVocabulary": Array(repeating: "word", count: 10_001)], as: .payloadTooLarge)
        reject(["customVocabulary": ["valid", 7]], as: .invalidValue)
        reject(["transcriptHistory": "not Data"], as: .invalidValue)
        reject(["engineChoice": "assemblyAI"], as: .invalidValue)
        reject(["hotkeyBindings": Data(count: 256 * 1_024 + 1)], as: .payloadTooLarge)
        reject(["inputSourceMappings": URL(fileURLWithPath: "/synthetic-only")], as: .invalidValue)
    }
    func testHotkeyAndMappingDataIsNotDecodedOrRewritten() throws {
        let bytes = Data("synthetic opaque future-compatible bytes".utf8)
        let keys = ["hotkeyBindings", "cancelShortcut", "inputSourceMappings", "dictationAppRules", "s1MiniAppStyling", "extendedDictationWritingStyles"]
        let output = try RemyMigrationPreferences.sanitized(Dictionary(uniqueKeysWithValues: keys.map { ($0, bytes) }))
        for key in keys { XCTAssertEqual(output[key] as? Data, bytes) }
    }
    func testBinaryEncodingAndNormalizedProjectionAreIdempotent() throws {
        let original: [String: Any] = ["holdToCancel": true, "hotkeyModifiers": 1 << 19,
            "soundEffectsVolume": 0.25, "customVocabulary": ["Unicode 原文"], "unknown": NSObject()]
        let first = try RemyMigrationPreferences.sanitized(original)
        let second = try RemyMigrationPreferences.sanitized(first)
        XCTAssertTrue(NSDictionary(dictionary: first).isEqual(to: second))
        let encoded = try RemyMigrationPreferences.encoded(original)
        XCTAssertTrue(encoded.starts(with: Data("bplist00".utf8)))
        var format = PropertyListSerialization.PropertyListFormat.xml
        let decoded = try XCTUnwrap(PropertyListSerialization.propertyList(from: encoded, options: [], format: &format) as? [String: Any])
        XCTAssertEqual(format, .binary)
        let normalized = try RemyMigrationPreferences.sanitized(decoded)
        XCTAssertTrue(NSDictionary(dictionary: normalized).isEqual(to: first))
        XCTAssertTrue(Set(first.keys).isSubset(of: RemyMigrationPreferences.migratableKeys))
    }
}
