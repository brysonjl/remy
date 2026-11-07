import Foundation
import CoreAudio
import XCTest
@testable import Dictate_Anywhere

@MainActor final class RemyCleanupProfileIntegrationTests: XCTestCase {
    private var directory: URL!
    private var restoreSettings: (() -> Void)!
    private var apps: [AppState] = []

    override func setUp() async throws {
        let settings = Settings.shared
        let engine = settings.engineChoice
        let model = settings.parakeetModelChoice
        let language = settings.selectedLanguage
        let mode = settings.transcriptPostProcessingMode
        let url = settings.openAICompatibleBaseURL, compatibleModel = settings.openAICompatibleModel
        let prompt = settings.openAICompatiblePostProcessingPrompt
        let vocabulary = settings.customVocabulary
        let fillerEnabled = settings.isFillerWordRemovalEnabled
        let fillers = settings.fillerWordsToRemove
        let history = settings.transcriptHistory
        let historyEnabled = settings.historyEnabled
        let historyPreference = UserDefaults.standard.object(forKey: "remyHistoryEnabled")
        let sound = settings.soundEffectsEnabled
        let mute = settings.muteSystemAudioDuringRecordingEnabled
        let boost = settings.boostMicrophoneVolumeEnabled
        let microphone = settings.selectedMicrophoneUID
        let preserve = settings.preserveCancelledSessions
        let autoStop = settings.autoStopAfterSpeechEndsEnabled
        let prewarm = settings.prewarmEnginesAtStartup
        let inputSwitch = settings.inputSourceAutoSwitchEnabled
        restoreSettings = {
            settings.engineChoice = engine
            settings.parakeetModelChoice = model
            settings.selectedLanguage = language
            settings.transcriptPostProcessingMode = mode
            settings.openAICompatibleBaseURL = url; settings.openAICompatibleModel = compatibleModel
            settings.openAICompatiblePostProcessingPrompt = prompt
            settings.customVocabulary = vocabulary
            settings.isFillerWordRemovalEnabled = fillerEnabled
            settings.fillerWordsToRemove = fillers
            settings.transcriptHistory = history
            settings.historyEnabled = historyEnabled
            if let historyPreference { UserDefaults.standard.set(historyPreference, forKey:"remyHistoryEnabled") }
            else { UserDefaults.standard.removeObject(forKey:"remyHistoryEnabled") }
            settings.soundEffectsEnabled = sound
            settings.muteSystemAudioDuringRecordingEnabled = mute
            settings.boostMicrophoneVolumeEnabled = boost
            settings.selectedMicrophoneUID = microphone
            settings.preserveCancelledSessions = preserve
            settings.autoStopAfterSpeechEndsEnabled = autoStop
            settings.prewarmEnginesAtStartup = prewarm
            settings.inputSourceAutoSwitchEnabled = inputSwitch
        }
        settings.engineChoice = .parakeet
        settings.parakeetModelChoice = .compactEnglish
        settings.selectedLanguage = .english
        settings.transcriptPostProcessingMode = .none
        settings.customVocabulary = []
        settings.isFillerWordRemovalEnabled = false
        settings.fillerWordsToRemove = []
        settings.transcriptHistory = []
        settings.historyEnabled = true
        settings.soundEffectsEnabled = false
        settings.muteSystemAudioDuringRecordingEnabled = false
        settings.boostMicrophoneVolumeEnabled = false
        settings.selectedMicrophoneUID = nil
        settings.preserveCancelledSessions = false
        settings.autoStopAfterSpeechEndsEnabled = false
        settings.prewarmEnginesAtStartup = false
        settings.inputSourceAutoSwitchEnabled = false
        await settings.flushTranscriptHistory()
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("RemyCleanupProfileIntegrationTests-\(UUID())")
    }

    override func tearDown() async throws {
        for app in apps { await app.shutdown() }
        apps = []
        restoreSettings()
        await Settings.shared.flushTranscriptHistory()
        if let directory, FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.removeItem(at: directory)
        }
    }

    private func app(controller:CleanupProfileController,credentials:ProfileIntegrationCredentials,
                     history:RemyHistoryController? = nil,
                     cleanup:@escaping (String,RemyDictationConfiguration) async throws -> String = { text,_ in text }) -> AppState {
        let permissions = Permissions(statusProvider:{ (true,false) })
        let app = AppState(permissions:permissions,
            recoveryStore:DictationRecoveryStore(directory:directory.appendingPathComponent("recovery")),
            historyController:history,cleanupProfileController:controller,cleanupProfileCredentials:credentials,
            engine:ProfileIntegrationEngine(),transcriptDelivery:{ _ in .copiedOnly },transcriptCleanup:cleanup,
            contextCapture:{ _ in nil },cleanupModelPreparation:{ _ in true })
        app.permissions.micGranted = true
        apps.append(app)
        return app
    }
    private func profile(id:UUID = UUID(),revision:UInt64 = 1,promptVersion:Int = 1,prompt:String = "First synthetic instruction") -> CleanupProfile {
        .init(id:id,revision:revision,name:"Synthetic profile",adapter:.openAICompatible,
            baseURL:"https://example.test/v1",model:"synthetic model",prompt:prompt,promptVersion:promptVersion,
            timeoutSeconds:8,capabilities:.init(transport:.completeJSON,discovery:false,structuredOutput:false,thinkingControl:false),
            routePolicy:.ordinary,allowInsecureRemoteHTTP:false,credential:nil)
    }
    func testCaptureFreezesSelectedProfileAndPersistsItsProvenance() async throws {
        let repository = ProfileIntegrationRepository(), credentials = ProfileIntegrationCredentials()
        let controller = CleanupProfileController(repository:repository,credentials:credentials)
        let original = profile()
        try controller.save(profile:original,credentialValue:"synthetic fake-store key")
        Settings.shared.transcriptPostProcessingMode = .profile
        try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
        let historyRepository = try RemyHistoryRepository(url:directory.appendingPathComponent("history.sqlite"))
        let history = RemyHistoryController(repository:historyRepository)
        var captured: [CleanupProfile] = []
        let app = app(controller:controller,credentials:credentials,history:history,cleanup: { text,configuration in
            captured.append(try XCTUnwrap(configuration.cleanup.profile))
            return text
        })
        await app.startDictation()
        XCTAssertEqual(app.status,.recording)
        XCTAssertEqual(credentials.resolveCalls,1)
        let edited = profile(id:original.id,revision:2,promptVersion:2,prompt:"Next synthetic instruction")
        try controller.save(profile:edited,credentialValue:"next fake-store key")
        await app.stopDictation()
        XCTAssertEqual(captured.first?.id,original.id)
        XCTAssertEqual(captured.first?.revision,1)
        XCTAssertEqual(captured.first?.promptVersion,1)
        XCTAssertEqual(captured.first?.prompt,original.prompt)
        let rows = try await historyRepository.page()
        let row = try XCTUnwrap(rows.first)
        let attempts = try await historyRepository.attempts(sessionID:row.id)
        XCTAssertEqual(attempts.first?.profile?.profileID,original.id)
        XCTAssertEqual(attempts.first?.profile?.profileRevision,1)
        XCTAssertEqual(attempts.first?.instructionVersion,1)
        XCTAssertEqual(attempts.first?.prompt,original.prompt)
        await app.startDictation(); await app.stopDictation()
        XCTAssertEqual(captured.map(\.revision),[1,2])
        XCTAssertEqual(credentials.resolveCalls,2)
    }
    func testExplicitMigrationPreservesLegacySetupAndSwitchesOnlyAfterCommit() throws {
        try requireEmptyIsolatedCredential()
        configureLegacy()
        let repository = ProfileIntegrationRepository(), credentials = ProfileIntegrationCredentials()
        let controller = CleanupProfileController(repository:repository,credentials:credentials)
        try controller.load()
        let app = app(controller:controller,credentials:credentials)
        XCTAssertTrue(app.canSaveCurrentCleanupAsProfile)
        app.saveCurrentCleanupAsProfile()
        XCTAssertEqual(Settings.shared.transcriptPostProcessingMode,.profile)
        XCTAssertEqual(controller.selectedProfile?.baseURL,"https://example.test/v1")
        XCTAssertEqual(controller.selectedProfile?.model,"exact synthetic model")
        XCTAssertEqual(controller.selectedProfile?.prompt,"Exact legacy synthetic instruction")
        assertLegacySetupUnchanged()
        XCTAssertEqual(credentials.stageCalls,0)
        XCTAssertNil(app.cleanupProfileError)
    }
    func testFailedMigrationCommitPreservesLegacyModeAndSettings() throws {
        try requireEmptyIsolatedCredential()
        configureLegacy()
        let repository = ProfileIntegrationRepository(), credentials = ProfileIntegrationCredentials()
        repository.failCommit = true
        let controller = CleanupProfileController(repository:repository,credentials:credentials)
        try controller.load()
        let app = app(controller:controller,credentials:credentials)
        app.saveCurrentCleanupAsProfile()
        XCTAssertEqual(Settings.shared.transcriptPostProcessingMode,.openAICompatible)
        XCTAssertTrue(controller.collection.profiles.isEmpty)
        XCTAssertNotNil(app.cleanupProfileError)
        assertLegacySetupUnchanged()
        XCTAssertEqual(credentials.stageCalls,0)
    }
    private func requireEmptyIsolatedCredential() throws {
        guard Settings.shared.openAICompatibleAPIKey.isEmpty else {
            throw XCTSkip("Requires isolated test settings with no compatible credential; never modifies Keychain")
        }
    }
    private func configureLegacy() {
        Settings.shared.transcriptPostProcessingMode = .openAICompatible
        Settings.shared.openAICompatibleBaseURL = "https://example.test/v1"
        Settings.shared.openAICompatibleModel = "exact synthetic model"
        Settings.shared.openAICompatiblePostProcessingPrompt = "Exact legacy synthetic instruction"
    }
    private func assertLegacySetupUnchanged() {
        XCTAssertEqual(Settings.shared.openAICompatibleBaseURL,"https://example.test/v1")
        XCTAssertEqual(Settings.shared.openAICompatibleModel,"exact synthetic model")
        XCTAssertEqual(Settings.shared.openAICompatiblePostProcessingPrompt,"Exact legacy synthetic instruction")
        XCTAssertTrue(Settings.shared.openAICompatibleAPIKey.isEmpty)
    }
}

@MainActor private final class ProfileIntegrationRepository: CleanupProfileRepository {
    var value = CleanupProfileCollection.empty
    var failCommit = false
    func load() throws -> CleanupProfileCollection { value }
    func commit(_ collection:CleanupProfileCollection,expectedRevision:UInt64) throws {
        guard !failCommit else { throw CleanupProfileFileRepository.RepositoryError.io }
        guard expectedRevision == value.revision else { throw CleanupProfileFileRepository.RepositoryError.conflict }
        value = collection
    }
}
@MainActor private final class ProfileIntegrationCredentials: CleanupProfileCredentialWriter {
    var values: [CleanupCredentialReference:String] = [:]
    var resolveCalls = 0, stageCalls = 0
    func set(_ value:String,for reference:CleanupCredentialReference,endpoint:CleanupEndpointIdentity) throws { values[reference] = value }
    func delete(_ reference:CleanupCredentialReference,for endpoint:CleanupEndpointIdentity) throws { values.removeValue(forKey:reference) }
    func resolve(_ reference:CleanupCredentialReference,for endpoint:CleanupEndpointIdentity) throws -> String? {
        resolveCalls += 1; return values[reference]
    }
    func stageLegacyBinding(from source:CleanupLegacyCredentialSource,to destination:CleanupCredentialReference) throws { stageCalls += 1 }
}

@MainActor
private final class ProfileIntegrationEngine: TranscriptionEngine {
    var recoveryCapture: RecoveryAudioCapture?
    var isReady = true
    var currentTranscript = ""
    var audioSamples: [Float] = []
    var finalText = "Synthetic final transcript"
    var finalize: (() async -> String)?
    var onStopAudioCapture: (() -> Void)?
    private(set) var startCount = 0
    private(set) var finalizationCount = 0
    private(set) var cancelCount = 0
    private(set) var recognitionConfigurations: [RecognitionSessionConfiguration] = []
    func levelSamples(count: Int) -> [Float] { [] }
    func prepare() async throws { isReady = true }
    func startRecording(deviceID: AudioDeviceID?) async throws { startCount += 1 }
    func stopAudioCapture() async { onStopAudioCapture?() }
    func stopRecording() async -> String {
        finalizationCount += 1
        return await finalize?() ?? finalText
    }
    func cancel() async { cancelCount += 1 }
    func transcribeRecording(at url: URL) async throws -> String { finalText }
    func setSessionConfiguration(_ configuration: RecognitionSessionConfiguration?) {
        if let configuration { recognitionConfigurations.append(configuration) }
    }
}
