import Foundation
import CoreFoundation

/// Pure Dev-to-Remy preference projection. Call before Settings initialization;
/// source ownership, destination exclusivity and durable publication are caller
/// responsibilities. Never exports a credential or opens any persistence API.
nonisolated enum RemyMigrationPreferences {
    enum Failure: Error, Equatable {
        case malformedSchema
        case unsupportedSchema
        case invalidValue
        case payloadTooLarge
    }
    static let schemaKey = "remy.configurationSchemaVersion"
    static let maximumEncodedBytes = 80 * 1_024 * 1_024
    private enum Kind {
        case boolean, integer(maximum: UInt64), volume
        case string(maximumBytes: Int), strings(maximumCount: Int, maximumElementBytes: Int, maximumBytes: Int)
        case data(maximumBytes: Int)
    }
    private static let rules: [String: Kind] = {
        var result: [String: Kind] = [:]
        for key in ["holdToCancel", "preserveCancelledSessions", "isFillerWordRemovalEnabled",
                    "boostMicrophoneVolumeEnabled", "muteSystemAudioDuringRecordingEnabled",
                    "autoStopAfterSpeechEndsEnabled", "soundEffectsEnabled", "showTextPreview",
                    "prewarmEnginesAtStartup", "dictationContextAwarenessEnabled", "userHasChosenEngine", "aiPostProcessingEnabled",
                    "ollamaReasoningEnabled", "openRouterReasoningEnabled", "inputSourceAutoSwitchEnabled",
                    "remyHistoryEnabled", "autoVolumeEnabled"] { result[key] = .boolean }
        for key in ["engineChoice", "parakeetModelChoice", "selectedLanguage", "appleSpeechLanguage",
                    "hotkeyDisplayName", "hotkeyMode", "overlayThemeMode", "overlaySize",
                    "appAppearanceMode", "themeMode", "emailDictationWritingStyle", "workMessagingDictationWritingStyle",
                    "personalMessagingDictationWritingStyle", "otherDictationWritingStyle",
                    "transcriptPostProcessingMode", "s1MiniStyling", "s1MiniStructure", "s1MiniContextSetting",
                    "ollamaReasoningSetting", "remyDictationDestination", "remyDictationStartSound",
                    "remyDictationCompletionSound"] { result[key] = .string(maximumBytes: 1_024) }
        for key in ["ollamaModel", "openRouterModel", "openAICompatibleModel"] { result[key] = .string(maximumBytes: 512) }
        for key in ["ollamaBaseURL", "openAICompatibleBaseURL"] { result[key] = .string(maximumBytes: 2_048) }
        for key in ["aiPostProcessingPrompt", "ollamaPostProcessingPrompt", "openRouterPostProcessingPrompt",
                    "openAICompatiblePostProcessingPrompt"] { result[key] = .string(maximumBytes: 65_536) }
        result["selectedMicrophoneUID"] = .string(maximumBytes: 4_096)
        result["soundEffectsVolume"] = .volume
        result["hotkeyKeyCode"] = .integer(maximum: UInt64(UInt16.max))
        result["hotkeyModifiers"] = .integer(maximum: UInt64.max)
        result["customVocabulary"] = .strings(maximumCount: 10_000, maximumElementBytes: 4_096, maximumBytes: 4 * 1_024 * 1_024)
        result["fillerWordsToRemove"] = .strings(maximumCount: 1_000, maximumElementBytes: 256, maximumBytes: 256 * 1_024)
        for key in ["hotkeyBindings", "cancelShortcut", "s1MiniAppStyling", "extendedDictationWritingStyles",
                    "dictationWritingStyleLibrary"] { result[key] = .data(maximumBytes: 256 * 1_024) }
        for key in ["dictationAppRules", "inputSourceMappings"] { result[key] = .data(maximumBytes: 1_024 * 1_024) }
        for key in ["vocabularyMisspellings", "dictationSnippets"] { result[key] = .data(maximumBytes: 4 * 1_024 * 1_024) }
        result["transcriptHistory"] = .data(maximumBytes: 64 * 1_024 * 1_024)
        return result
    }()
    /// An explicit allowlist, not a prefix filter. In particular, the legacy
    /// OpenRouter environment-reference preference may itself hold a key literal.
    /// Login registration, remote context sharing, AssemblyAI and transient state are absent.
    /// App-awareness preference is preserved, including an explicit opt-out.
    static let migratableKeys: Set<String> = Set(rules.keys).union([schemaKey])

    static func sanitized(_ source: [String: Any]) throws -> [String: Any] {
        if let raw = source[schemaKey] {
            guard let number = integer(raw), let version = UInt64(number.stringValue) else { throw Failure.malformedSchema }
            guard version <= 1 else { throw Failure.unsupportedSchema }
        }
        var result: [String: Any] = [schemaKey: 1]
        for (key, kind) in rules {
            guard let raw = source[key] else { continue }
            switch kind {
            case .boolean:
                guard let number = raw as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else { throw Failure.invalidValue }
                result[key] = number.boolValue
            case .integer(let maximum):
                guard let number = integer(raw), let value = UInt64(number.stringValue), value <= maximum else { throw Failure.invalidValue }
                result[key] = NSNumber(value: value)
            case .volume:
                guard let number = raw as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
                      number.doubleValue.isFinite, (0...1).contains(number.doubleValue) else { throw Failure.invalidValue }
                result[key] = number.doubleValue
            case .string(let maximum):
                guard let value = raw as? String else { throw Failure.invalidValue }
                guard value.utf8.count <= maximum else { throw Failure.payloadTooLarge }
                if key == "engineChoice", !["parakeet", "appleSpeech"].contains(value) { throw Failure.invalidValue }
                result[key] = value
            case .strings(let maximumCount, let maximumElement, let maximumBytes):
                guard let values = raw as? [String] else { throw Failure.invalidValue }
                guard values.count <= maximumCount else { throw Failure.payloadTooLarge }
                var bytes = 0
                for value in values {
                    let count = value.utf8.count
                    guard count <= maximumElement, count <= maximumBytes - bytes else { throw Failure.payloadTooLarge }
                    bytes += count
                }
                result[key] = values
            case .data(let maximum):
                guard let value = raw as? Data else { throw Failure.invalidValue }
                guard value.count <= maximum else { throw Failure.payloadTooLarge }
                result[key] = value // Exact opaque bytes; no legacy history decoding/re-encoding.
            }
        }
        // Validate only the projected dictionary: unknown/secret-bearing source
        // values are never serialized, recursively inspected, or interpolated.
        _ = try binary(result)
        return result
    }

    static func encoded(_ source: [String: Any]) throws -> Data {
        try binary(sanitized(source))
    }
    private static func binary(_ value: [String: Any]) throws -> Data {
        let data: Data
        do { data = try PropertyListSerialization.data(fromPropertyList: value, format: .binary, options: 0) }
        catch { throw Failure.invalidValue }
        guard data.count <= maximumEncodedBytes else { throw Failure.payloadTooLarge }
        return data
    }
    private static func integer(_ raw: Any) -> NSNumber? {
        guard let number = raw as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
              ["c", "s", "i", "l", "q", "C", "S", "I", "L", "Q"].contains(String(cString: number.objCType)) else { return nil }
        return number
    }
}
