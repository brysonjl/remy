import XCTest
import CoreAudio
@testable import Dictate_Anywhere

@MainActor
final class DictationRecoveryTests: XCTestCase {
    private var directory: URL!
    private var store: DictationRecoveryStore!
    private var restoreSettings: (() -> Void)!
    private var apps: [AppState] = []

    override func setUp() async throws {
        let settings = Settings.shared
        let engine = settings.engineChoice
        let model = settings.parakeetModelChoice
        let language = settings.selectedLanguage
        let mode = settings.transcriptPostProcessingMode
        let history = settings.transcriptHistory
        let sound = settings.soundEffectsEnabled
        let mute = settings.muteSystemAudioDuringRecordingEnabled
        let boost = settings.boostMicrophoneVolumeEnabled
        let microphone = settings.selectedMicrophoneUID
        let preserve = settings.preserveCancelledSessions
        let prewarm = settings.prewarmEnginesAtStartup
        let autoSwitch = settings.inputSourceAutoSwitchEnabled
        let autoStop = settings.autoStopAfterSpeechEndsEnabled
        let fillers = settings.isFillerWordRemovalEnabled
        restoreSettings = {
            settings.engineChoice = engine
            settings.parakeetModelChoice = model
            settings.selectedLanguage = language
            settings.transcriptPostProcessingMode = mode
            settings.transcriptHistory = history
            settings.soundEffectsEnabled = sound
            settings.muteSystemAudioDuringRecordingEnabled = mute
            settings.boostMicrophoneVolumeEnabled = boost
            settings.selectedMicrophoneUID = microphone
            settings.preserveCancelledSessions = preserve
            settings.prewarmEnginesAtStartup = prewarm
            settings.inputSourceAutoSwitchEnabled = autoSwitch
            settings.autoStopAfterSpeechEndsEnabled = autoStop
            settings.isFillerWordRemovalEnabled = fillers
        }
        settings.engineChoice = .parakeet
        settings.parakeetModelChoice = .compactEnglish
        settings.selectedLanguage = .english
        settings.transcriptPostProcessingMode = .none
        settings.transcriptHistory = []
        settings.soundEffectsEnabled = false
        settings.muteSystemAudioDuringRecordingEnabled = false
        settings.boostMicrophoneVolumeEnabled = false
        settings.selectedMicrophoneUID = nil
        settings.preserveCancelledSessions = false
        settings.prewarmEnginesAtStartup = false
        settings.inputSourceAutoSwitchEnabled = false
        settings.autoStopAfterSpeechEndsEnabled = false
        settings.isFillerWordRemovalEnabled = false
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("dictation-recovery-test-\(UUID())")
        store = DictationRecoveryStore(directory: directory)
    }

    override func tearDown() async throws {
        for app in apps { await app.shutdown() }
        apps = []
        restoreSettings()
        await Settings.shared.flushTranscriptHistory()
        store = nil
        if FileManager.default.fileExists(atPath: directory.path) { try FileManager.default.removeItem(at: directory) }
    }

    private func app(engine: RecoveryTestEngine) -> AppState {
        let app = AppState(permissions: Permissions(statusProvider: { (true, false) }),
                           recoveryStore: store, engine: engine,
                           transcriptDelivery: { _ in .copiedOnly })
        app.permissions.micGranted = true
        apps.append(app)
        return app
    }

    func testCapturePreservesEveryChunkAcrossRelaunch() async throws {
        let capture = try store.beginCapture()
        let samples: [Float] = [0.25, -0.5, 0.125, 0.75]
        capture.append(Array(samples.prefix(2)))
        capture.append(Array(samples.suffix(2)))
        let preserved = try await store.preserve(capture, preview: "Partial preview", completedTranscript: nil)
        let entry = try XCTUnwrap(preserved)
        capture.append([0.99]) // A closed capture must never contaminate the saved session.
        _ = await capture.finish()
        let reader = try RecoveryAudioReader(url: capture.url)
        XCTAssertEqual(try reader.nextSamples(), samples)
        XCTAssertNil(try reader.nextSamples())
        let relaunched = DictationRecoveryStore(directory: directory)
        try relaunched.reload()
        XCTAssertEqual(relaunched.entries, [entry])
        XCTAssertEqual(entry.expiresAt.timeIntervalSince(entry.createdAt), 86_400)
    }

    func testReaderBoundsMemoryForLongRecordings() async throws {
        let capture = try store.beginCapture()
        capture.append(Array(repeating: 0.2, count: 320_003))
        _ = await capture.finish()
        let reader = try RecoveryAudioReader(url: capture.url)
        var total = 0
        while let samples = try reader.nextSamples() {
            XCTAssertLessThanOrEqual(samples.count, 160_000)
            XCTAssertTrue(samples.allSatisfy { $0 == 0.2 })
            total += samples.count
        }
        XCTAssertEqual(total, 320_003)
    }

    func testFailedMetadataSaveKeepsAudioAndRecoversItAfterRestart() async throws {
        let failing = DictationRecoveryStore(directory: directory, metadataWriter: { _, _ in
            throw CocoaError(.fileWriteOutOfSpace)
        })
        let capture = try failing.beginCapture()
        let samples: [Float] = [0.125, -0.25, 0.5, -0.75]
        capture.append(samples)
        do {
            _ = try await failing.preserve(capture, preview: "synthetic preview", completedTranscript: "Exact completed words")
            XCTFail("Injected metadata failure was hidden")
        } catch {}
        XCTAssertTrue(FileManager.default.fileExists(atPath: capture.url.path))
        XCTAssertEqual(failing.entries.first?.completedTranscript, "Exact completed words")
        try failing.reload()
        XCTAssertEqual(failing.entries.first?.completedTranscript, "Exact completed words", "Reload must retain known unsaved text")
        let relaunched = DictationRecoveryStore(directory: directory)
        try relaunched.reload()
        XCTAssertEqual(relaunched.entries.count, 1)
        XCTAssertEqual(relaunched.entries.first?.id, capture.id)
        XCTAssertTrue(relaunched.entries.first?.hasAudio == true)
        XCTAssertNil(relaunched.entries.first?.completedTranscript, "Orphan audio does not prove an original final transcript")
        XCTAssertNotNil(relaunched.entries.first?.captureError)
        XCTAssertEqual(try RecoveryAudioReader(url: capture.url).nextSamples(), samples)
        let reloadedAgain = DictationRecoveryStore(directory: directory)
        try reloadedAgain.reload()
        XCTAssertEqual(reloadedAgain.entries, relaunched.entries)
    }

    func testInterruptedClosedAudioWithoutMetadataBecomesRecoverable() async throws {
        let capture = try store.beginCapture()
        capture.append([0.25, 0.5])
        _ = await capture.finish()
        try store.reload()
        XCTAssertTrue(store.entries.isEmpty, "This process's active recording must not be imported")
        let relaunched = DictationRecoveryStore(directory: directory)
        try relaunched.reload()
        XCTAssertEqual(relaunched.entries.first?.id, capture.id)
        XCTAssertTrue(relaunched.entries.first?.hasAudio == true)
    }

    func testRecoveryWriterRejectsOversizedQueueWithExplicitFailure() async throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let capture = try RecoveryAudioCapture(id: UUID(), url: directory.appendingPathComponent("bounded.caf"), maximumQueuedSamples: 4)
        capture.append([0.1, 0.2, 0.3, 0.4, 0.5])
        let result = await capture.finish()
        XCTAssertEqual(result.sampleCount, 0)
        XCTAssertNotNil(result.error)
        XCTAssertNotNil(capture.failureMessage)
        capture.append([0.9])
        let closed = await capture.finish()
        XCTAssertEqual(closed.sampleCount, 0)
    }

    func testNormalShutdownDrainsSafetyRecordingEvenWhenCancelRetentionIsOff() async throws {
        Settings.shared.preserveCancelledSessions = false
        let engine = RecoveryTestEngine()
        let app = app(engine: engine)
        await app.startDictation()
        let capture = try XCTUnwrap(engine.recoveryCapture)
        let samples = (0..<32_003).map { Float($0 % 47) / 100 }
        capture.append(samples)
        app.currentTranscript = "Available preview before quit"
        await app.shutdown()
        XCTAssertEqual(store.entries.count, 1)
        XCTAssertTrue(store.entries.first?.hasAudio == true)
        XCTAssertNil(store.entries.first?.completedTranscript)
        let reader = try RecoveryAudioReader(url: capture.url)
        var recovered: [Float] = []
        while let chunk = try reader.nextSamples() { recovered.append(contentsOf: chunk) }
        XCTAssertEqual(recovered.count, samples.count)
        XCTAssertTrue(recovered.elementsEqual(samples), "Shutdown recovery must preserve every accepted sample, including the final short read")
        XCTAssertTrue(recovered.suffix(3).elementsEqual(samples.suffix(3)))
        XCTAssertNil(try reader.nextSamples())
        let relaunched = DictationRecoveryStore(directory: directory)
        try relaunched.reload()
        XCTAssertEqual(relaunched.entries, store.entries)
    }

    func testExpiryDeletesAudioAndMetadataAt24Hours() async throws {
        let capture = try store.beginCapture()
        capture.append([0.5])
        let now = Date()
        _ = try await store.preserve(capture, preview: "test", completedTranscript: nil, now: now)
        try store.reload(now: now.addingTimeInterval(86_399))
        XCTAssertEqual(store.entries.count, 1)
        try store.reload(now: now.addingTimeInterval(86_400))
        XCTAssertTrue(store.entries.isEmpty)
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: directory.path).isEmpty)
    }

    func testEmptyCancellationIsDiscarded() async throws {
        let capture = try store.beginCapture()
        let entry = try await store.preserve(capture, preview: "", completedTranscript: nil)
        XCTAssertNil(entry)
        XCTAssertFalse(FileManager.default.fileExists(atPath: capture.url.path))
    }

    func testCompletedDictationDiscardDeletesTemporaryAudio() async throws {
        let capture = try store.beginCapture()
        capture.append([0.1, 0.2])
        try await store.discard(capture)
        XCTAssertTrue(store.entries.isEmpty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: capture.url.path))
    }

    func testClearAllRemovesSavedSessionsWithoutDeletingActiveRecording() async throws {
        let first = try store.beginCapture()
        first.append([0.5])
        _ = try await store.preserve(first, preview: "first", completedTranscript: nil)
        let active = try store.beginCapture()
        try store.removeAll()
        XCTAssertTrue(store.entries.isEmpty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: first.url.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: active.url.path))
        try await store.discard(active)
    }

    func testRecoveredTextGoesToHistoryAndAudioIsRemoved() async throws {
        let previousHistory = Settings.shared.transcriptHistory
        defer { Settings.shared.transcriptHistory = previousHistory }
        let capture = try store.beginCapture()
        capture.append([0.5])
        let preserved = try await store.preserve(capture, preview: "incomplete", completedTranscript: nil)
        let entry = try XCTUnwrap(preserved)
        let engine = RecoveryTestEngine()
        let app = app(engine: engine)
        await app.recoverCancelledDictation(entry)
        XCTAssertEqual(Settings.shared.transcriptHistory.last?.text, "Recovered speech.")
        XCTAssertTrue(engine.didRecover)
        XCTAssertTrue(store.entries.isEmpty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: capture.url.path))
        XCTAssertEqual(app.status, .idle)
    }

    func testFailedRecoveryKeepsOriginalAudioForRetry() async throws {
        let capture = try store.beginCapture()
        capture.append([0.5])
        let preserved = try await store.preserve(capture, preview: "incomplete", completedTranscript: nil)
        let entry = try XCTUnwrap(preserved)
        let engine = RecoveryTestEngine()
        engine.recoveryFails = true
        let app = app(engine: engine)
        await app.recoverCancelledDictation(entry)
        XCTAssertEqual(store.entries, [entry])
        XCTAssertTrue(FileManager.default.fileExists(atPath: capture.url.path))
        XCTAssertNotNil(store.errorMessage)
        XCTAssertEqual(app.status, .idle)
    }

    func testCompletedRecognitionCanBeRecoveredWithoutRerunningModel() async throws {
        let history = Settings.shared.transcriptHistory
        defer { Settings.shared.transcriptHistory = history }
        let capture = try store.beginCapture()
        let preserved = try await store.preserve(capture, preview: "preview", completedTranscript: "Finished speech.")
        let entry = try XCTUnwrap(preserved)
        let engine = RecoveryTestEngine()
        let app = app(engine: engine)
        await app.recoverCancelledDictation(entry)
        XCTAssertFalse(engine.didRecover)
        XCTAssertEqual(Settings.shared.transcriptHistory.last?.text, "Finished speech.")
    }

    func testCancellationPreservesAudioWhenEnabledAndNoneWhenDisabled() async throws {
        let previous = Settings.shared.preserveCancelledSessions
        defer { Settings.shared.preserveCancelledSessions = previous }
        for enabled in [false, true] {
            Settings.shared.preserveCancelledSessions = enabled
            let engine = RecoveryTestEngine()
            let app = app(engine: engine)
            await app.startDictation()
            XCTAssertEqual(app.status, .recording)
            XCTAssertNotNil(engine.recoveryCapture, "Safety capture is independent of intentional-cancel retention")
            engine.recoveryCapture?.append([0.25])
            app.currentTranscript = "A partial preview"
            await app.cancelDictation()
            XCTAssertTrue(engine.didCancel)
            XCTAssertEqual(store.entries.count, enabled ? 1 : 0)
            XCTAssertEqual(app.status, .idle)
            XCTAssertEqual(app.currentTranscript, "")
        }
    }

    func testLocalFailureRecoveryRemainsAvailableWithCancellationPreservationDisabled() async throws {
        let previous = Settings.shared.preserveCancelledSessions
        let sound = Settings.shared.soundEffectsEnabled
        defer { Settings.shared.preserveCancelledSessions = previous; Settings.shared.soundEffectsEnabled = sound }
        Settings.shared.preserveCancelledSessions = false
        Settings.shared.soundEffectsEnabled = false
        let engine = RecoveryTestEngine()
        engine.lastTranscriptionError = "Synthetic provider failure"
        let app = app(engine: engine)
        await app.startDictation()
        XCTAssertEqual(app.status, .recording)
        engine.recoveryCapture?.append([0.5, 0.25])
        await app.stopDictation()
        XCTAssertEqual(store.entries.count, 1)
        XCTAssertTrue(store.entries.first?.hasAudio == true)
        XCTAssertNil(store.entries.first?.completedTranscript)
        XCTAssertEqual(app.status, .idle)
    }

    func testFinalAudioFailureAfterPollingStopsPreservesAudioAndCompletedTextWithoutDelivery() async throws {
        let engine = RecoveryTestEngine()
        var deliveries = 0
        let app = AppState(permissions: Permissions(statusProvider: { (true, false) }),
                           recoveryStore: store, engine: engine,
                           transcriptDelivery: { _ in deliveries += 1; return .copiedOnly })
        app.permissions.micGranted = true
        apps.append(app)
        await app.startDictation()
        let capture = try XCTUnwrap(engine.recoveryCapture)
        capture.append([0.5, 0.25])
        engine.onCaptureStop = { capture.append(Array(repeating: 0.1, count: 16_000 * 30 + 1)) }
        await app.stopDictation()
        XCTAssertEqual(deliveries, 0)
        XCTAssertEqual(app.status, .idle)
        let entry = try XCTUnwrap(store.entries.first)
        XCTAssertTrue(entry.hasAudio)
        XCTAssertNotNil(entry.captureError)
        XCTAssertEqual(entry.completedTranscript, "Finished speech.")
        let reader = try RecoveryAudioReader(url: capture.url)
        XCTAssertEqual(try reader.nextSamples(), [0.5, 0.25])
    }

    func testCancelDuringProcessingNeverDeliversLateResultAndKeepsRecovery() async throws {
        let previousHistory = Settings.shared.transcriptHistory
        let previousPreserve = Settings.shared.preserveCancelledSessions
        let previousSound = Settings.shared.soundEffectsEnabled
        defer {
            Settings.shared.transcriptHistory = previousHistory
            Settings.shared.preserveCancelledSessions = previousPreserve
            Settings.shared.soundEffectsEnabled = previousSound
        }
        Settings.shared.preserveCancelledSessions = true
        Settings.shared.soundEffectsEnabled = false
        let engine = RecoveryTestEngine()
        let began = expectation(description: "recognition began")
        engine.onStop = { began.fulfill() }
        let app = app(engine: engine)
        await app.startDictation()
        XCTAssertEqual(app.status, .recording)
        engine.recoveryCapture?.append([0.5])
        let finishing = Task { await app.stopDictation() }
        await fulfillment(of: [began], timeout: 1)
        let cancelling = Task { await app.cancelDictation() }
        for _ in 0..<100 where app.canCancelDictation { await Task.yield() }
        XCTAssertFalse(app.canCancelDictation)
        engine.finishPendingStop("Late recognition result")
        await finishing.value
        await cancelling.value
        XCTAssertEqual(Settings.shared.transcriptHistory, previousHistory)
        XCTAssertEqual(app.lastTranscript, "")
        XCTAssertEqual(app.status, .idle)
        XCTAssertTrue(engine.didCancel)
        XCTAssertFalse(engine.cancelOverlappedStop)
        XCTAssertEqual(store.entries.first?.completedTranscript, "Late recognition result")
        XCTAssertTrue(store.entries.first?.hasAudio == true)
    }
}

@MainActor
private final class RecoveryTestEngine: TranscriptionEngine {
    var onCaptureStop: (() -> Void)?
    func stopAudioCapture() async { onCaptureStop?() }
    var recoveryCapture: RecoveryAudioCapture?
    var isReady = true
    var currentTranscript = ""
    var audioSamples: [Float] = []
    var lastTranscriptionError: String?
    var didRecover = false
    var recoveryFails = false
    var didCancel = false
    var cancelOverlappedStop = false
    var onStop: (() -> Void)?
    private var pendingStop: CheckedContinuation<String, Never>?
    func levelSamples(count: Int) -> [Float] { [] }
    func prepare() async throws {}
    func startRecording(deviceID: AudioDeviceID?) async throws {}
    func stopRecording() async -> String {
        guard onStop != nil else { return "Finished speech." }
        return await withCheckedContinuation { continuation in
            pendingStop = continuation
            onStop?()
        }
    }
    func finishPendingStop(_ text: String) {
        pendingStop?.resume(returning: text)
        pendingStop = nil
    }
    func cancel() async {
        cancelOverlappedStop = pendingStop != nil
        didCancel = true
    }
    func transcribeRecording(at url: URL) async throws -> String {
        didRecover = true
        if recoveryFails { throw TranscriptionError.engineNotReady }
        return "Recovered speech."
    }
}
