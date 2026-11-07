import CoreAudio
import XCTest
@testable import Dictate_Anywhere

@MainActor
final class AppleSpeechExplicitInstallationTests: XCTestCase {
    private var restoreSettings: (() -> Void)!
    private var directory: URL!
    private var apps: [AppState] = []

    override func setUp() async throws {
        let settings = Settings.shared
        let engine = settings.engineChoice
        let language = settings.appleSpeechLanguage
        let migration = settings.legacyAppleSpeechMigrationPending
        let chosen = settings.userHasChosenEngine
        restoreSettings = {
            settings.engineChoice = engine
            settings.appleSpeechLanguage = language
            settings.legacyAppleSpeechMigrationPending = migration
            settings.userHasChosenEngine = chosen
        }
        settings.engineChoice = .appleSpeech
        settings.appleSpeechLanguage = .english
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("AppleSpeechInstallTests-\(UUID())")
    }

    override func tearDown() async throws {
        for app in apps { await app.shutdown() }
        apps = []
        restoreSettings()
        if FileManager.default.fileExists(atPath: directory.path) { try FileManager.default.removeItem(at: directory) }
    }

    private func app(engine: InstallationProbeEngine,
                     installer: @escaping (SupportedLanguage) async throws -> Void) -> AppState {
        let app = AppState(permissions: Permissions(statusProvider: { (true, false) }),
            recoveryStore: DictationRecoveryStore(directory: directory), engine: engine,
            contextCapture: { _ in nil },
            appleSpeechAssetSnapshot: { ([.english, .german], [.english]) },
            appleSpeechAssetInstaller: installer)
        apps.append(app)
        return app
    }

    func testOrdinaryPreparationNeverUsesExplicitInstaller() async {
        let engine = InstallationProbeEngine()
        var installCount = 0
        let app = app(engine: engine) { _ in installCount += 1 }
        await app.prepareActiveEngine()
        XCTAssertEqual(installCount, 0)
        XCTAssertFalse(app.isInstallingAppleSpeech)
    }

    func testExplicitInstallationIsSingleFlightAndBlocksCapture() async {
        let engine = InstallationProbeEngine()
        let entered = expectation(description: "explicit installer entered")
        var resume: CheckedContinuation<Void, Never>?
        var installedLanguages: [SupportedLanguage] = []
        let app = app(engine: engine) { language in
            installedLanguages.append(language)
            await withCheckedContinuation { continuation in
                resume = continuation
                entered.fulfill()
            }
        }
        let first = Task { await app.installSelectedAppleSpeechAssets() }
        await fulfillment(of: [entered], timeout: 2)
        XCTAssertTrue(app.isInstallingAppleSpeech)
        await app.installSelectedAppleSpeechAssets()
        await app.startDictation()
        XCTAssertEqual(engine.startCount, 0)
        XCTAssertEqual(installedLanguages, [.english])
        // A changed selection cannot be overwritten by the suspended installation.
        Settings.shared.appleSpeechLanguage = .german
        resume?.resume()
        await first.value
        XCTAssertEqual(Settings.shared.appleSpeechLanguage, .german)
        XCTAssertFalse(app.isInstallingAppleSpeech)
        XCTAssertEqual(app.status, .idle)
    }
}

@MainActor
private final class InstallationProbeEngine: TranscriptionEngine {
    var isReady = true
    var currentTranscript = ""
    var audioSamples: [Float] = []
    var recoveryCapture: RecoveryAudioCapture?
    var startCount = 0
    func prepare() async throws {}
    func levelSamples(count: Int) -> [Float] { [] }
    func startRecording(deviceID: AudioDeviceID?) async throws { startCount += 1 }
    func stopAudioCapture() async {}
    func stopRecording() async -> String { "" }
    func cancel() async {}
    func transcribeRecording(at url: URL) async throws -> String { "" }
}
