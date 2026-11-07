import Foundation
import CoreAudio
import XCTest
@testable import Dictate_Anywhere

@MainActor
final class RemySessionIntegrationTests: XCTestCase {
    private var directory: URL!
    private var restoreSettings: (() -> Void)!
    private var apps: [AppState] = []

    override func setUp() async throws {
        let settings = Settings.shared
        let engine = settings.engineChoice
        let model = settings.parakeetModelChoice
        let language = settings.selectedLanguage
        let mode = settings.transcriptPostProcessingMode
        let vocabulary = settings.customVocabulary
        let fillerEnabled = settings.isFillerWordRemovalEnabled
        let fillers = settings.fillerWordsToRemove
        let history = settings.transcriptHistory
        let sound = settings.soundEffectsEnabled
        let mute = settings.muteSystemAudioDuringRecordingEnabled
        let boost = settings.boostMicrophoneVolumeEnabled
        let microphone = settings.selectedMicrophoneUID
        let preserve = settings.preserveCancelledSessions
        let autoStop = settings.autoStopAfterSpeechEndsEnabled
        let prewarm = settings.prewarmEnginesAtStartup
        let inputSwitch = settings.inputSourceAutoSwitchEnabled
        let destination = settings.dictationDestination
        restoreSettings = {
            settings.engineChoice = engine
            settings.parakeetModelChoice = model
            settings.selectedLanguage = language
            settings.transcriptPostProcessingMode = mode
            settings.customVocabulary = vocabulary
            settings.isFillerWordRemovalEnabled = fillerEnabled
            settings.fillerWordsToRemove = fillers
            settings.transcriptHistory = history
            settings.soundEffectsEnabled = sound
            settings.muteSystemAudioDuringRecordingEnabled = mute
            settings.boostMicrophoneVolumeEnabled = boost
            settings.selectedMicrophoneUID = microphone
            settings.preserveCancelledSessions = preserve
            settings.autoStopAfterSpeechEndsEnabled = autoStop
            settings.prewarmEnginesAtStartup = prewarm
            settings.inputSourceAutoSwitchEnabled = inputSwitch
            settings.dictationDestination = destination
        }
        settings.engineChoice = .parakeet
        settings.parakeetModelChoice = .compactEnglish
        settings.selectedLanguage = .english
        settings.transcriptPostProcessingMode = .none
        settings.customVocabulary = []
        settings.isFillerWordRemovalEnabled = false
        settings.fillerWordsToRemove = []
        settings.transcriptHistory = []
        settings.soundEffectsEnabled = false
        settings.muteSystemAudioDuringRecordingEnabled = false
        settings.boostMicrophoneVolumeEnabled = false
        settings.selectedMicrophoneUID = nil
        settings.preserveCancelledSessions = false
        settings.autoStopAfterSpeechEndsEnabled = false
        settings.prewarmEnginesAtStartup = false
        settings.inputSourceAutoSwitchEnabled = false
        settings.dictationDestination = .originalField
        await settings.flushTranscriptHistory()
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("RemySessionIntegrationTests-\(UUID())")
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

    private func app(
        engine: SessionIntegrationEngine,
        cleanup: @escaping (String, RemyDictationConfiguration) async throws -> String = { text, _ in text },
        delivery: @escaping (String) async -> TextInsertionResult = { _ in .copiedOnly },
        authorizedDelivery: ((String, RemyDeliveryAuthorization) async -> TextInsertionResult)? = nil,
        destinationResolution: AppState.DestinationResolution? = nil,
        targetCapture: ((NSRunningApplication?) async -> RemyInsertionTarget)? = nil,
        cleanupTimeout: Duration = .seconds(8),
        microphoneGranted: Bool = true,
        microphonePermissionRequester: (@MainActor @Sendable () async -> Bool)? = nil
    ) -> AppState {
        let permissions = Permissions(statusProvider: { (microphoneGranted, false) })
        let app = AppState(permissions: permissions, microphonePermissionRequester: microphonePermissionRequester,
            recoveryStore: DictationRecoveryStore(directory: directory), engine: engine,
            transcriptDelivery: delivery, transcriptCleanup: cleanup,
            authorizedTranscriptDelivery: authorizedDelivery,
            destinationResolution: destinationResolution,
            contextCapture: { _ in nil }, insertionTargetCapture: targetCapture,
            cleanupTimeout: cleanupTimeout,
            cleanupModelPreparation: { _ in true })
        app.permissions.micGranted = microphoneGranted
        apps.append(app)
        return app
    }

    func testMidRecordingChangesApplyOnlyToNextSession() async {
        let settings = Settings.shared
        settings.customVocabulary = ["OriginalTerm"]
        settings.isFillerWordRemovalEnabled = true
        settings.fillerWordsToRemove = ["um"]
        let engine = SessionIntegrationEngine()
        engine.finalText = "um original uh"
        var configurations: [RemyDictationConfiguration] = []
        var cleanupInputs: [String] = []
        var delivered: [String] = []
        var destinations: [RemyDictationDestination] = []
        let app = app(engine: engine, cleanup: { text, configuration in
            configurations.append(configuration)
            cleanupInputs.append(text)
            return text
        }, delivery: { text in delivered.append(text); return .copiedOnly }, destinationResolution: { policy, _, _ in
            destinations.append(policy)
            return .unavailable
        })
        await app.startDictation()
        XCTAssertEqual(app.status, .recording)
        settings.customVocabulary = ["NextTerm"]
        settings.fillerWordsToRemove = ["uh"]
        settings.transcriptPostProcessingMode = .fluidAudioVocabulary
        settings.dictationDestination = .currentField
        await app.stopDictation()
        await app.startDictation()
        XCTAssertEqual(app.status, .recording)
        await app.stopDictation()
        XCTAssertEqual(configurations.count, 2)
        guard configurations.count == 2 else { return }
        XCTAssertEqual(configurations[0].capture.vocabulary, ["OriginalTerm"])
        XCTAssertEqual(configurations[0].capture.fillerRules.words, ["um"])
        XCTAssertEqual(configurations[0].cleanup.mode, .none)
        XCTAssertEqual(configurations[0].capture.destination, .originalField)
        XCTAssertEqual(configurations[1].capture.vocabulary, ["NextTerm"])
        XCTAssertEqual(configurations[1].capture.fillerRules.words, ["uh"])
        XCTAssertEqual(configurations[1].cleanup.mode, .fluidAudioVocabulary)
        XCTAssertEqual(configurations[1].capture.destination, .currentField)
        XCTAssertNotEqual(configurations[0].credentialReference, configurations[1].credentialReference)
        XCTAssertEqual(cleanupInputs, ["original uh", "um original"])
        XCTAssertEqual(delivered, cleanupInputs)
        XCTAssertEqual(destinations, [.originalField, .currentField])
        XCTAssertEqual(settings.transcriptHistory.map(\.text), cleanupInputs)
        XCTAssertEqual(engine.recognitionConfigurations.map(\.vocabularyTerms), [["OriginalTerm"], ["NextTerm"]])
    }

    func testCancelDuringDestinationResolutionPreventsLateFocusAndDeliveryInBothModes() async {
        for policy in RemyDictationDestination.allCases {
            Settings.shared.dictationDestination = policy
            let engine = SessionIntegrationEngine()
            let gate = IntegrationResultGate<Bool>(started: expectation(description: "destination suspended"))
            var focusEffects = 0
            var deliveries = 0
            var authorization: RemyDeliveryAuthorization?
            let app = app(engine: engine, delivery: { _ in deliveries += 1; return .copiedOnly },
                          destinationResolution: { capturedPolicy, _, lease in
                XCTAssertEqual(capturedPolicy, policy)
                authorization = lease
                _ = await gate.wait()
                _ = lease.performFocusMutation { focusEffects += 1; return true }
                return .unavailable
            })
            await app.startDictation()
            let stop = Task { await app.stopDictation() }
            await fulfillment(of: [gate.started], timeout: 2)
            XCTAssertTrue(app.canCancelDictation)
            let cancel = Task { await app.cancelDictation() }
            await waitUntil { authorization?.isRevoked == true }
            gate.resolve(true)
            await stop.value
            await cancel.value
            XCTAssertEqual(focusEffects, 0)
            XCTAssertEqual(deliveries, 0)
            XCTAssertEqual(app.status, .idle)
        }
    }

    func testDuplicateStopFinalizesAndDeliversExactlyOnce() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<String>(started: expectation(description: "recognition started"))
        engine.finalize = { await gate.wait() }
        var delivered = 0
        let app = app(engine: engine, delivery: { _ in delivered += 1; return .copiedOnly })
        await app.startDictation()
        let firstStop = Task { await app.stopDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        await app.stopDictation()
        XCTAssertEqual(engine.finalizationCount, 1)
        gate.resolve("Synthetic final")
        await firstStop.value
        await app.stopDictation()
        XCTAssertEqual(engine.finalizationCount, 1)
        XCTAssertEqual(delivered, 1)
        XCTAssertEqual(Settings.shared.transcriptHistory.count, 1)
        XCTAssertEqual(app.status, .idle)
    }

    func testCancelBeforeRecognitionDoesNotFinalizeAndNextSessionStillWorks() async {
        let engine = SessionIntegrationEngine()
        var cleaned = 0
        var delivered = 0
        let app = app(engine: engine, cleanup: { text, _ in cleaned += 1; return text },
                      delivery: { _ in delivered += 1; return .copiedOnly })
        await app.startDictation()
        await app.cancelDictation()
        XCTAssertEqual(engine.finalizationCount, 0)
        XCTAssertEqual(cleaned, 0)
        XCTAssertEqual(delivered, 0)
        XCTAssertTrue(Settings.shared.transcriptHistory.isEmpty)
        XCTAssertEqual(app.status, .idle)
        await app.startDictation()
        await app.stopDictation()
        XCTAssertEqual(engine.finalizationCount, 1)
        XCTAssertEqual(cleaned, 1)
        XCTAssertEqual(delivered, 1)
    }

    func testCancelDuringNoncooperativeRecognitionDropsLateFinal() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<String>(started: expectation(description: "recognition suspended"))
        engine.finalize = { await gate.wait() }
        var cleaned = 0
        var delivered = 0
        let app = app(engine: engine, cleanup: { text, _ in cleaned += 1; return text },
                      delivery: { _ in delivered += 1; return .copiedOnly })
        await app.startDictation()
        let stop = Task { await app.stopDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        let cancel = Task { await app.cancelDictation() }
        await waitUntil { !app.canCancelDictation }
        gate.resolve("Late synthetic final")
        await stop.value
        await cancel.value
        XCTAssertEqual(engine.finalizationCount, 1)
        XCTAssertEqual(cleaned, 0)
        XCTAssertEqual(delivered, 0)
        XCTAssertTrue(Settings.shared.transcriptHistory.isEmpty)
        XCTAssertEqual(app.status, .idle)
    }

    func testCancelDuringNoncooperativeCleanupDropsLateOutputAndHistory() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<String>(started: expectation(description: "cleanup suspended"))
        var delivered = 0
        let app = app(engine: engine, cleanup: { _, _ in await gate.wait() },
                      delivery: { _ in delivered += 1; return .copiedOnly })
        await app.startDictation()
        let stop = Task { await app.stopDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        let cancel = Task { await app.cancelDictation() }
        await waitUntil { !app.canCancelDictation }
        gate.resolve("Late synthetic cleanup")
        await stop.value
        await cancel.value
        XCTAssertEqual(delivered, 0)
        XCTAssertTrue(Settings.shared.transcriptHistory.isEmpty)
        XCTAssertEqual(app.status, .idle)
    }

    func testCancelCannotUndoAlreadyDispatchedFakeDeliveryOrDuplicateCompletion() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<TextInsertionResult>(started: expectation(description: "fake delivery dispatched"))
        var delivered = 0
        let app = app(engine: engine, delivery: { _ in delivered += 1; return await gate.wait() })
        await app.startDictation()
        let stop = Task { await app.stopDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        XCTAssertFalse(app.canCancelDictation)
        await app.cancelDictation()
        gate.resolve(.copiedOnly)
        await stop.value
        await app.stopDictation()
        XCTAssertEqual(delivered, 1)
        XCTAssertEqual(engine.finalizationCount, 1)
        XCTAssertEqual(engine.cancelCount, 0)
        XCTAssertEqual(Settings.shared.transcriptHistory.count, 1)
        XCTAssertEqual(app.status, .idle)
    }

    func testCancelWhileDeliveryQueuedRevokesLeaseBeforeAnyFakeEffect() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<Bool>(started: expectation(description: "delivery queued"))
        var lease: RemyDeliveryAuthorization?
        var clipboardEffects = 0
        var pasteEffects = 0
        let app = app(engine: engine, authorizedDelivery: { _, authorization in
            lease = authorization
            _ = await gate.wait()
            if authorization.consumeClipboard() {
                _ = authorization.performClipboardMutation { clipboardEffects += 1; return true }
            }
            if authorization.consumePaste() { pasteEffects += 1 }
            return .copiedOnly
        })
        await app.startDictation()
        let stop = Task { await app.stopDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        let cancel = Task { await app.cancelDictation() }
        await waitUntil { lease?.isRevoked == true }
        gate.resolve(true)
        await stop.value
        await cancel.value
        XCTAssertEqual(clipboardEffects, 0)
        XCTAssertEqual(pasteEffects, 0)
        XCTAssertEqual(lease?.snapshot, .init(copied: false, pasteClaimed: false))
        XCTAssertEqual(app.status, .idle)
    }

    func testCancelAfterFakeCopyRetainsCopyButBlocksQueuedPaste() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<Bool>(started: expectation(description: "fake copy completed"))
        var lease: RemyDeliveryAuthorization?
        var clipboardEffects = 0
        var pasteEffects = 0
        let app = app(engine: engine, authorizedDelivery: { _, authorization in
            lease = authorization
            if authorization.consumeClipboard() {
                _ = authorization.performClipboardMutation { clipboardEffects += 1; return true }
            }
            _ = await gate.wait()
            if authorization.consumePaste() { pasteEffects += 1 }
            return .copiedOnly
        })
        await app.startDictation()
        let stop = Task { await app.stopDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        XCTAssertEqual(clipboardEffects, 1)
        let cancel = Task { await app.cancelDictation() }
        await waitUntil { lease?.isRevoked == true }
        gate.resolve(true)
        await stop.value
        await cancel.value
        XCTAssertEqual(clipboardEffects, 1)
        XCTAssertEqual(pasteEffects, 0)
        XCTAssertEqual(lease?.snapshot, .init(copied: true, pasteClaimed: false))
        XCTAssertEqual(app.status, .idle)
    }

    func testDuplicateStartDuringPermissionRequestDoesNotRequestTwice() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<Bool>(started: expectation(description: "permission request suspended"))
        defer { gate.resolve(false) }
        var requests = 0
        var delivered = 0
        let app = app(engine: engine, delivery: { _ in delivered += 1; return .copiedOnly },
            microphoneGranted: false, microphonePermissionRequester: {
                requests += 1
                return await gate.wait()
            })
        let first = Task { await app.startDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        var duplicateReturned = false
        let duplicate = Task {
            await app.startDictation()
            duplicateReturned = true
        }
        // On a regression both callers could wait for permission. Bound this
        // check, then release every waiter before joining either task.
        await waitUntil { duplicateReturned }
        XCTAssertEqual(requests, 1)
        gate.resolve(false)
        await duplicate.value
        await first.value
        XCTAssertEqual(requests, 1)
        XCTAssertEqual(engine.startCount, 0)
        XCTAssertEqual(engine.finalizationCount, 0)
        XCTAssertEqual(delivered, 0)
        XCTAssertTrue(Settings.shared.transcriptHistory.isEmpty)
        XCTAssertEqual(app.status, .idle)
    }

    func testCancelPendingPermissionRevokesStartAndIgnoresLateGrant() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<Bool>(started: expectation(description: "permission awaiting user"))
        defer { gate.resolve(true) }
        var requests = 0
        var delivered = 0
        let app = app(engine: engine, delivery: { _ in delivered += 1; return .copiedOnly },
            microphoneGranted: false, microphonePermissionRequester: {
                requests += 1
                return await gate.wait()
            })
        let starting = Task { await app.startDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        XCTAssertTrue(app.canCancelDictation)
        await app.cancelDictation()
        XCTAssertFalse(app.canCancelDictation)
        gate.resolve(true)
        await starting.value
        XCTAssertEqual(requests, 1)
        XCTAssertFalse(app.permissions.micGranted, "An obsolete request must not apply its late permission result")
        XCTAssertEqual(engine.startCount, 0)
        XCTAssertEqual(engine.finalizationCount, 0)
        XCTAssertEqual(delivered, 0)
        XCTAssertTrue(Settings.shared.transcriptHistory.isEmpty)
        XCTAssertEqual(app.status, .idle)
    }

    func testSleepRevokesLateCleanupBlocksStartsAndWakeRequiresExplicitStart() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<String>(started: expectation(description: "cleanup awaiting sleep"))
        defer { gate.resolve("Late synthetic cleanup") }
        var cleanupCalls = 0
        var delivered: [String] = []
        let app = app(engine: engine, cleanup: { text, _ in
            cleanupCalls += 1
            if cleanupCalls == 1 { return await gate.wait() }
            return text
        }, delivery: { delivered.append($0); return .copiedOnly })
        await app.startDictation()
        let stop = Task { await app.stopDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        let sleep = Task { await app.handleSystemWillSleep() }
        await waitUntil { !app.canCancelDictation }
        gate.resolve("Late synthetic cleanup")
        await stop.value
        await sleep.value
        XCTAssertEqual(app.status, .idle)
        XCTAssertTrue(delivered.isEmpty)
        XCTAssertTrue(Settings.shared.transcriptHistory.isEmpty)
        await app.startDictation()
        XCTAssertEqual(engine.startCount, 1, "An idle sleeping app must reject capture")
        app.handleSystemDidWake()
        XCTAssertEqual(engine.startCount, 1, "Wake must not automatically resume capture")
        XCTAssertEqual(app.status, .idle)
        await app.startDictation()
        XCTAssertEqual(engine.startCount, 2)
        await app.stopDictation()
        XCTAssertEqual(delivered, [engine.finalText])
        XCTAssertEqual(Settings.shared.transcriptHistory.map(\.text), [engine.finalText])
    }

    func testSleepRevokesQueuedDeliveryBeforeFakeEffects() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<Bool>(started: expectation(description: "delivery awaiting sleep"))
        defer { gate.resolve(true) }
        var lease: RemyDeliveryAuthorization?
        var clipboardEffects = 0
        var pasteEffects = 0
        let app = app(engine: engine, authorizedDelivery: { _, authorization in
            lease = authorization
            _ = await gate.wait()
            if authorization.consumeClipboard() {
                _ = authorization.performClipboardMutation { clipboardEffects += 1; return true }
            }
            if authorization.consumePaste() { pasteEffects += 1 }
            return .copiedOnly
        })
        await app.startDictation()
        let stop = Task { await app.stopDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        let sleep = Task { await app.handleSystemWillSleep() }
        await waitUntil { lease?.isRevoked == true }
        gate.resolve(true)
        await stop.value
        await sleep.value
        XCTAssertEqual(clipboardEffects, 0)
        XCTAssertEqual(pasteEffects, 0)
        XCTAssertEqual(lease?.snapshot, .init(copied: false, pasteClaimed: false))
        await app.startDictation()
        XCTAssertEqual(engine.startCount, 1)
        app.handleSystemDidWake()
        XCTAssertEqual(engine.startCount, 1)
        await app.startDictation()
        await app.stopDictation()
        XCTAssertEqual(engine.startCount, 2)
        XCTAssertEqual(clipboardEffects, 1)
        XCTAssertEqual(pasteEffects, 1)
        XCTAssertEqual(app.status, .idle)
    }

    func testSleepRevokesPendingPermissionAndWakeDoesNotResurrectGrant() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<Bool>(started: expectation(description: "permission awaiting sleep"))
        defer { gate.resolve(true) }
        var requests = 0
        var delivered = 0
        let app = app(engine: engine, delivery: { _ in delivered += 1; return .copiedOnly },
            microphoneGranted: false, microphonePermissionRequester: {
                requests += 1
                return await gate.wait()
            })
        let start = Task { await app.startDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        await app.handleSystemWillSleep()
        await app.startDictation()
        XCTAssertEqual(requests, 1, "Sleeping must not initiate another permission gesture")
        gate.resolve(true)
        await start.value
        XCTAssertFalse(app.permissions.micGranted)
        XCTAssertEqual(engine.startCount, 0)
        XCTAssertEqual(delivered, 0)
        app.handleSystemDidWake()
        XCTAssertEqual(requests, 1)
        XCTAssertEqual(engine.startCount, 0)
        XCTAssertEqual(app.status, .idle)
        // A new explicit gesture can ask again. Its grant still must not
        // become a recording gesture; only the subsequent start records.
        await app.startDictation()
        XCTAssertEqual(requests, 2)
        XCTAssertTrue(app.permissions.micGranted)
        XCTAssertEqual(engine.startCount, 0)
        await app.startDictation()
        XCTAssertEqual(engine.startCount, 1)
        await app.stopDictation()
        XCTAssertEqual(delivered, 1)
    }

    func testUseRawCommitsOnceBeforeNoncooperativeWorkerDrains() async {
        Settings.shared.transcriptPostProcessingMode = .openAICompatible
        let engine = SessionIntegrationEngine()
        engine.finalText = "Keep  these words.\nDo not change 15000."
        let gate = IntegrationResultGate<String>(started: expectation(description: "provider suspended"))
        defer { gate.resolve("Late unwanted output") }
        var delivered: [String] = []
        let app = app(engine: engine, cleanup: { _, _ in await gate.wait() },
            delivery: { delivered.append($0); return .copiedOnly })
        await app.startDictation()
        let finished = expectation(description: "raw decision finished without provider join")
        let stop = Task { await app.stopDictation(); finished.fulfill() }
        await fulfillment(of: [gate.started], timeout: 2)
        XCTAssertTrue(app.canUseRawCleanup)
        app.useRawCleanupNow()
        app.useRawCleanupNow()
        await fulfillment(of: [finished], timeout: 2)
        XCTAssertEqual(delivered, [engine.finalText])
        XCTAssertEqual(Settings.shared.transcriptHistory.map(\.text), [engine.finalText])
        XCTAssertTrue(app.isCleanupDraining)
        XCTAssertFalse(app.canUseRawCleanup)
        await app.startDictation()
        XCTAssertEqual(engine.startCount, 1, "A draining shared runtime must not be reused")
        gate.resolve("Late unwanted output")
        await stop.value
        await waitUntil { !app.isCleanupDraining }
        XCTAssertEqual(delivered, [engine.finalText])
        await app.startDictation()
        XCTAssertEqual(engine.startCount, 2)
        await app.cancelDictation()
    }

    func testCleanupTimeoutSelectsRawWithoutWaitingForProvider() async {
        Settings.shared.transcriptPostProcessingMode = .openAICompatible
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<String>(started: expectation(description: "timed provider suspended"))
        defer { gate.resolve("Late unwanted output") }
        var delivered: [String] = []
        let app = app(engine: engine, cleanup: { _, _ in await gate.wait() },
            delivery: { delivered.append($0); return .copiedOnly }, cleanupTimeout: .milliseconds(50))
        await app.startDictation()
        let finished = expectation(description: "timeout selected raw")
        let stop = Task { await app.stopDictation(); finished.fulfill() }
        await fulfillment(of: [gate.started, finished], timeout: 2)
        XCTAssertEqual(delivered, [engine.finalText])
        XCTAssertEqual(app.status, .idle)
        XCTAssertTrue(app.isCleanupDraining)
        gate.resolve("Late unwanted output")
        await stop.value
        await waitUntil { !app.isCleanupDraining }
        XCTAssertEqual(delivered, [engine.finalText])
    }

    func testCancelledTargetCaptureCannotReplaceNextSessionDestination() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<RemyInsertionTarget>(started: expectation(description: "target capture suspended"))
        let first = RemyInsertionTarget(terminal: false, check: { _ in true })
        let second = RemyInsertionTarget(terminal: false, check: { _ in false })
        defer { gate.resolve(first) }
        var captures = 0
        let app = app(engine: engine, targetCapture: { _ in
            captures += 1
            if captures == 1 { return await gate.wait() }
            return second
        })
        let oldStart = Task { await app.startDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        XCTAssertEqual(engine.startCount, 0)
        await app.cancelDictation()
        await app.startDictation()
        XCTAssertEqual(app.status, .recording)
        XCTAssertTrue(app.insertionTargetSnapshot === second)
        gate.resolve(first)
        await oldStart.value
        XCTAssertEqual(engine.startCount, 1)
        XCTAssertTrue(app.insertionTargetSnapshot === second)
        await app.cancelDictation()
        XCTAssertTrue(app.insertionTargetSnapshot === RemyInsertionTarget.unavailable)
    }

    func testDestinationPreferenceFreezesBeforeInitialTargetCaptureSuspends() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<RemyInsertionTarget>(started: expectation(description: "initial target suspended"))
        defer { gate.resolve(.unavailable) }
        var resolvedPolicy: RemyDictationDestination?
        let app = app(engine: engine, destinationResolution: { policy, _, _ in
            resolvedPolicy = policy
            return .unavailable
        }, targetCapture: { _ in await gate.wait() })
        let start = Task { await app.startDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        Settings.shared.dictationDestination = .currentField
        gate.resolve(.unavailable)
        await start.value
        await app.stopDictation()
        XCTAssertEqual(resolvedPolicy, .originalField)
        XCTAssertEqual(Settings.shared.dictationDestination, .currentField)
    }

    func testTargetSnapshotIsFrozenThroughCleanupAndResetAfterDelivery() async {
        let engine = SessionIntegrationEngine()
        let proof = RemyInsertionTarget(terminal: false, check: { _ in true })
        let cleanupGate = IntegrationResultGate<String>(started: expectation(description: "cleanup suspended after target capture"))
        defer { cleanupGate.resolve("Synthetic cleaned transcript") }
        var captures = 0
        let app = app(engine: engine, cleanup: { _, _ in await cleanupGate.wait() }, targetCapture: { _ in
            captures += 1
            return proof
        })
        await app.startDictation()
        XCTAssertTrue(app.insertionTargetSnapshot === proof)
        let stop = Task { await app.stopDictation() }
        await fulfillment(of: [cleanupGate.started], timeout: 2)
        XCTAssertTrue(app.insertionTargetSnapshot === proof)
        XCTAssertEqual(captures, 1, "Preparation and delivery must not recapture a newly focused field")
        cleanupGate.resolve("Synthetic cleaned transcript")
        await stop.value
        XCTAssertEqual(captures, 1)
        XCTAssertTrue(app.insertionTargetSnapshot === RemyInsertionTarget.unavailable)
    }

    func testSleepRevokesSuspendedDestinationCapture() async {
        let engine = SessionIntegrationEngine()
        let gate = IntegrationResultGate<RemyInsertionTarget>(started: expectation(description: "target before sleep"))
        defer { gate.resolve(.unavailable) }
        let app = app(engine: engine, targetCapture: { _ in await gate.wait() })
        let start = Task { await app.startDictation() }
        await fulfillment(of: [gate.started], timeout: 2)
        await app.handleSystemWillSleep()
        gate.resolve(RemyInsertionTarget(terminal: false, check: { _ in true }))
        await start.value
        XCTAssertEqual(engine.startCount, 0)
        XCTAssertTrue(app.insertionTargetSnapshot === RemyInsertionTarget.unavailable)
        app.handleSystemDidWake()
    }

    private func waitUntil(_ condition: () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        while !condition(), ContinuousClock.now < deadline { try? await Task.sleep(for: .milliseconds(5)) }
        XCTAssertTrue(condition(), "Expected session transition did not occur", file: file, line: line)
    }
}

@MainActor
private final class IntegrationResultGate<Value> {
    let started: XCTestExpectation
    private var continuations: [CheckedContinuation<Value, Never>] = []
    private var resolved: Value?
    init(started: XCTestExpectation) { self.started = started }
    func wait() async -> Value {
        if let resolved { return resolved }
        return await withCheckedContinuation {
            continuations.append($0)
            started.fulfill()
        }
    }
    func resolve(_ value: Value) {
        resolved = value
        let pending = continuations
        continuations = []
        pending.forEach { $0.resume(returning: value) }
    }
}

@MainActor
private final class SessionIntegrationEngine: TranscriptionEngine {
    var recoveryCapture: RecoveryAudioCapture?
    var isReady = true
    var currentTranscript = ""
    var audioSamples: [Float] = []
    var finalText = "Synthetic final transcript"
    var finalize: (() async -> String)?
    private(set) var startCount = 0
    private(set) var finalizationCount = 0
    private(set) var cancelCount = 0
    private(set) var recognitionConfigurations: [RecognitionSessionConfiguration] = []
    func levelSamples(count: Int) -> [Float] { [] }
    func prepare() async throws { isReady = true }
    func startRecording(deviceID: AudioDeviceID?) async throws { startCount += 1 }
    func stopAudioCapture() async {}
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
