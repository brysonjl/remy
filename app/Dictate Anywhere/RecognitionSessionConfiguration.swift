/// Recognition-only immutable values. Contains no credentials or mutable Settings
/// reference; a foreground session retains this through capture and finalization.
nonisolated struct RecognitionSessionConfiguration: Equatable {
    let modelChoice: ParakeetModelChoice
    let expectedLanguage: SupportedLanguage
    let nativeLanguageCode: String
    let appleSpeechLanguage: SupportedLanguage
    let acousticVocabularyEnabled: Bool
    let vocabularyTerms: [String]
    let showTextPreview: Bool
    let microphoneUID: String?

    var activeVocabularyTerms: [String] { acousticVocabularyEnabled ? vocabularyTerms : [] }

    @MainActor
    static func capture(settings: Settings) -> Self {
        Self(modelChoice: settings.parakeetModelChoice,
             expectedLanguage: settings.selectedLanguage,
             nativeLanguageCode: settings.selectedLanguage.nemotronLanguageCode,
             appleSpeechLanguage: settings.appleSpeechLanguage,
             acousticVocabularyEnabled: settings.fluidAudioVocabularyEnabled,
             vocabularyTerms: settings.customVocabulary,
             showTextPreview: settings.showTextPreview,
             microphoneUID: settings.selectedMicrophoneUID)
    }
}
