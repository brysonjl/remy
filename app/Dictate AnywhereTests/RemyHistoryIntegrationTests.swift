import Foundation
import CoreAudio
import XCTest
@testable import Dictate_Anywhere

@MainActor final class RemyHistoryIntegrationTests: XCTestCase {
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
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("RemyHistoryIntegrationTests-\(UUID())")
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
        engine: HistoryIntegrationEngine,
        history: RemyHistoryController,
        recoveryStore: DictationRecoveryStore? = nil,
        cleanup: @escaping (String, RemyDictationConfiguration) async throws -> String = { text, _ in text },
        delivery: @escaping (String) async -> TextInsertionResult = { _ in .copiedOnly },
        authorizedDelivery: ((String, RemyDeliveryAuthorization) async -> TextInsertionResult)? = nil,
        cleanupTimeout: Duration = .seconds(8),
        microphoneGranted: Bool = true,
        microphonePermissionRequester: (@MainActor @Sendable () async -> Bool)? = nil
    ) -> AppState {
        let permissions = Permissions(statusProvider: { (microphoneGranted, false) })
        let app = AppState(permissions: permissions, microphonePermissionRequester: microphonePermissionRequester,
            recoveryStore: recoveryStore ?? DictationRecoveryStore(directory: directory.appendingPathComponent("recovery")), historyController: history, engine: engine,
            transcriptDelivery: delivery, transcriptCleanup: cleanup,
            authorizedTranscriptDelivery: authorizedDelivery,
            contextCapture: { _ in nil }, cleanupTimeout: cleanupTimeout, cleanupModelPreparation: { _ in true })
        app.permissions.micGranted = microphoneGranted
        apps.append(app)
        return app
    }


    private func repository() throws -> RemyHistoryRepository {
        try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
        return try RemyHistoryRepository(url:directory.appendingPathComponent("history.sqlite"))
    }

    func testExplicitStoredHistoryDeletionClearsSnapshotsAndSurvivesReopen() async throws {
        let paths = RemyStoragePaths(applicationSupportDirectory: directory, bundleIdentifier: "test.remy.deletion")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try paths.prepareHistoryDirectory()
        let repository = try RemyHistoryRepository(url: paths.historyDatabase)
        let row = RemyHistorySession(id: UUID(), createdAt: Date(), rawText: "synthetic raw",
            selectedText: "synthetic final", engine: "synthetic", model: "fixture", language: "en",
            settingsVersion: 1, instructionVersion: 1)
        let committed = await repository.commitRaw(row, generation: 0)
        XCTAssertEqual(committed, .saved)
        let history = RemyHistoryController(repository: repository)
        _ = await history.prepare()
        let recovery = DictationRecoveryStore(directory: paths.recoveryDirectory)
        let application = app(engine: HistoryIntegrationEngine(), history: history, recoveryStore: recovery)
        let suite = "RemyDeletionTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(Data("synthetic legacy".utf8), forKey: "transcriptHistory")
        let snapshot = paths.historyDirectory.appendingPathComponent("history-before-v2-\(UUID()).sqlite")
        try Data("synthetic rollback".utf8).write(to: snapshot)
        await application.clearAllStoredHistory(paths: paths, defaults: defaults)
        XCTAssertNil(application.storedHistoryDeletionError)
        XCTAssertFalse(application.isDeletingStoredHistory)
        XCTAssertNil(defaults.object(forKey: "transcriptHistory"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: snapshot.path))
        let reopened = try RemyHistoryRepository(url: paths.historyDatabase)
        let rows = try await reopened.page()
        XCTAssertTrue(rows.isEmpty)
        XCTAssertTrue(FileManager.default.fileExists(atPath: paths.historyDatabase.path))

        // A suspicious backup link reports incomplete deletion even with no visible
        // history. The explicit action can be retried after repairing that file.
        let external = directory.appendingPathComponent("unrelated-sentinel")
        try Data("keep".utf8).write(to: external)
        try FileManager.default.createSymbolicLink(at: snapshot, withDestinationURL: external)
        let recoveryCapture = try recovery.beginCapture()
        recoveryCapture.append([Float](repeating: 0, count: 160))
        _ = try await recovery.preserve(recoveryCapture, preview: "synthetic recovery", completedTranscript: nil)
        XCTAssertEqual(recovery.entries.count, 1)
        await application.clearAllStoredHistory(paths: paths, defaults: defaults)
        XCTAssertNotNil(application.storedHistoryDeletionError)
        XCTAssertTrue(recovery.entries.isEmpty, "Removed recovery files must not leave stale rows after a backup failure")
        XCTAssertTrue(FileManager.default.fileExists(atPath: external.path))
        try FileManager.default.removeItem(at: snapshot)
        await application.clearAllStoredHistory(paths: paths, defaults: defaults)
        XCTAssertNil(application.storedHistoryDeletionError)
    }

    func testRawIsDurableBeforeCleanupAndSelectedIntentBeforeDelivery() async throws {
        Settings.shared.transcriptPostProcessingMode = .ollama
        let repository = try repository(), engine = HistoryIntegrationEngine()
        engine.finalText = "raw synthetic 🦾"
        let history = RemyHistoryController(repository:repository)
        var cleaned = 0, delivered = 0
        let app = app(engine:engine,history:history,cleanup: { input,_ in
            cleaned += 1
            let rows = try await repository.page()
            XCTAssertEqual(rows.count,1)
            XCTAssertEqual(rows.first?.rawText,engine.finalText)
            XCTAssertEqual(rows.first?.selectedText,engine.finalText)
            let attempts = try await repository.attempts(sessionID:try XCTUnwrap(rows.first?.id))
            XCTAssertTrue(attempts.isEmpty)
            XCTAssertEqual(input,engine.finalText)
            return "clean synthetic 🦾"
        },delivery: { text in
            delivered += 1
            do {
                let rows = try await repository.page()
                let row = try XCTUnwrap(rows.first)
                XCTAssertEqual(row.rawText,"raw synthetic 🦾")
                XCTAssertEqual(row.selectedText,text)
                XCTAssertEqual(row.selectionKind,.attempt)
                let attempts = try await repository.attempts(sessionID:row.id)
                let deliveries = try await repository.deliveries(sessionID:row.id)
                XCTAssertEqual(attempts.first?.output,text)
                XCTAssertEqual(deliveries.count,1)
                XCTAssertEqual(deliveries.first?.payload,text)
                XCTAssertEqual(deliveries.first?.outcome,.intent)
            } catch { XCTFail("Synthetic repository inspection failed") }
            return .copiedOnly
        })
        await app.startDictation(); XCTAssertEqual(app.status,.recording)
        await app.stopDictation()
        XCTAssertEqual(cleaned,1); XCTAssertEqual(delivered,1)
        XCTAssertFalse(app.historyNotSaved)
        XCTAssertEqual(app.historyEntries.first?.text,"clean synthetic 🦾")
    }

    func testUncertainPasteDispatchIsDurableAndNeverRetried() async throws {
        try await assertCopiedOnlyDeliveryOutcome(claimPaste:true,expected:.uncertain)
    }

    func testClipboardOnlyDeliveryIsRecordedAsCopiedWithoutPasteClaim() async throws {
        try await assertCopiedOnlyDeliveryOutcome(claimPaste:false,expected:.copied)
    }

    private func assertCopiedOnlyDeliveryOutcome(claimPaste: Bool,
                                                expected: RemyHistoryDelivery.Outcome) async throws {
        let repository = try repository(), engine = HistoryIntegrationEngine()
        let history = RemyHistoryController(repository:repository)
        var invocations = 0
        let app = app(engine:engine,history:history,authorizedDelivery: { _,authorization in
            invocations += 1
            XCTAssertTrue(authorization.consumeClipboard())
            authorization.recordCopied()
            if claimPaste { XCTAssertTrue(authorization.consumePaste()) }
            XCTAssertEqual(authorization.snapshot.pasteClaimed,claimPaste)
            return .copiedOnly
        })
        await app.startDictation()
        await app.stopDictation()
        let rows = try await repository.page()
        let id = try XCTUnwrap(rows.first?.id)
        let deliveries = try await repository.deliveries(sessionID:id)
        XCTAssertEqual(deliveries.count,1)
        XCTAssertEqual(deliveries.first?.outcome,expected)
        XCTAssertEqual(deliveries.first?.payload,engine.finalText)
        XCTAssertEqual(invocations,1)
        // Neither another stop nor refreshing history dispatches a second output operation.
        await app.stopDictation()
        _ = await history.refresh()
        XCTAssertEqual(invocations,1)
        let after = try await repository.deliveries(sessionID:id)
        XCTAssertEqual(after,deliveries)
    }

    func testCleanupFailurePersistsFailedAttemptAndRawFallback() async throws {
        Settings.shared.transcriptPostProcessingMode = .ollama
        let repository = try repository(), engine = HistoryIntegrationEngine()
        engine.finalText = "raw remains"
        let history = RemyHistoryController(repository:repository)
        var delivered: [String] = []
        let app = app(engine:engine,history:history,cleanup: { _,_ in throw URLError(.timedOut) },delivery: { text in delivered.append(text); return .copiedOnly })
        await app.startDictation(); await app.stopDictation()
        let persisted = try await repository.page()
        let row = try XCTUnwrap(persisted.first)
        let attempts = try await repository.attempts(sessionID:row.id)
        XCTAssertEqual(row.rawText,"raw remains")
        XCTAssertEqual(row.selectedText,"raw remains")
        XCTAssertEqual(row.selectionKind,.fallback)
        XCTAssertNil(row.selectedAttemptID)
        XCTAssertEqual(attempts.count,1)
        XCTAssertEqual(attempts.first?.status,.failed)
        XCTAssertEqual(attempts.first?.failureCategory,"timeout")
        XCTAssertNotNil(attempts.first?.startedAt)
        XCTAssertNotNil(attempts.first?.durationMilliseconds)
        XCTAssertNil(attempts.first?.output)
        XCTAssertEqual(delivered,["raw remains"])
        XCTAssertEqual(app.overlay.state, .cleanupFallback(reason: "timeout", delivery: .copied))
    }

    func testImmediateBudgetRejectionIsSavedAndWarnsAfterAutomaticPaste() async throws {
        Settings.shared.transcriptPostProcessingMode = .ollama
        let repository = try repository(), engine = HistoryIntegrationEngine()
        let history = RemyHistoryController(repository: repository)
        var delivered = 0
        let app = app(engine: engine, history: history, cleanup: { _,_ in
            throw RemoteCleanupInputPolicyError.singleRequestBudgetExceeded
        }, delivery: { text in
            delivered += 1
            XCTAssertEqual(text, engine.finalText)
            return .success
        })
        await app.startDictation(); await app.stopDictation()
        let rows = try await repository.page()
        let id = try XCTUnwrap(rows.first?.id)
        let attempts = try await repository.attempts(sessionID: id)
        XCTAssertEqual(attempts.count, 1)
        XCTAssertEqual(attempts.first?.failureCategory, "requestBudgetExceeded")
        XCTAssertEqual(rows.first?.selectionKind, .fallback)
        XCTAssertEqual(app.overlay.state, .cleanupFallback(reason: "requestBudgetExceeded", delivery: .inserted))
        XCTAssertEqual(delivered, 1)
    }

    func testWholeCleanupDeadlineRecordsTimeoutAndWarning() async throws {
        Settings.shared.transcriptPostProcessingMode = .ollama
        let repository = try repository(), engine = HistoryIntegrationEngine()
        let history = RemyHistoryController(repository: repository)
        var calls = 0
        let app = app(engine: engine, history: history, cleanup: { text,_ in calls += 1; return text },
            cleanupTimeout: .zero)
        await app.startDictation(); await app.stopDictation()
        let rows = try await repository.page()
        let attempts = try await repository.attempts(sessionID: XCTUnwrap(rows.first?.id))
        XCTAssertEqual(calls, 0)
        XCTAssertEqual(attempts.first?.failureCategory, "timeout")
        XCTAssertNotNil(attempts.first?.durationMilliseconds)
        XCTAssertEqual(app.overlay.state, .cleanupFallback(reason: "timeout", delivery: .copied))
    }

    func testCleanupAndClipboardFailureNeverSuggestsPastingOldClipboard() async throws {
        Settings.shared.transcriptPostProcessingMode = .ollama
        let repository = try repository(), engine = HistoryIntegrationEngine()
        let history = RemyHistoryController(repository: repository)
        let app = app(engine: engine, history: history, cleanup: { _,_ in throw URLError(.cannotConnectToHost) },
            authorizedDelivery: { _,_ in .failed })
        await app.startDictation(); await app.stopDictation()
        XCTAssertEqual(app.overlay.state, .cleanupFallback(reason: "transport", delivery: .failed))
        let rows = try await repository.page()
        XCTAssertEqual(rows.first?.selectedText, engine.finalText)
        let deliveries = try await repository.deliveries(sessionID: XCTUnwrap(rows.first?.id))
        XCTAssertEqual(deliveries.first?.outcome, .failed)
    }

    func testCancelledCleanupRecordsAttemptWithoutDeliveringLateResult() async throws {
        Settings.shared.transcriptPostProcessingMode = .ollama
        let repository = try repository(), engine = HistoryIntegrationEngine()
        let history = RemyHistoryController(repository: repository)
        let started = expectation(description: "cleanup suspended")
        let gate = HistoryIntegrationGate(started: started)
        defer { gate.resolve("release") }
        var delivered = 0
        let app = app(engine: engine, history: history, cleanup: { _,_ in await gate.wait() },
            delivery: { _ in delivered += 1; return .success })
        await app.startDictation()
        let stop = Task { await app.stopDictation() }
        await fulfillment(of: [started], timeout: 2)
        let cancelling = expectation(description: "cancel stopped capture")
        engine.onStopAudioCapture = { cancelling.fulfill() }
        let cancel = Task { await app.cancelDictation() }
        await fulfillment(of: [cancelling], timeout: 2)
        engine.onStopAudioCapture = nil
        gate.resolve("late cleanup")
        await cancel.value; await stop.value
        let rows = try await repository.page()
        let id = try XCTUnwrap(rows.first?.id)
        let attempts = try await repository.attempts(sessionID: id)
        XCTAssertEqual(attempts.count, 1)
        XCTAssertEqual(attempts.first?.status, .cancelled)
        XCTAssertEqual(attempts.first?.failureCategory, "cancelled")
        XCTAssertEqual(rows.first?.status, .interrupted)
        XCTAssertEqual(rows.first?.selectedText, engine.finalText)
        XCTAssertEqual(delivered, 0)
        let deliveries = try await repository.deliveries(sessionID: id)
        XCTAssertTrue(deliveries.isEmpty)
    }

    func testRepositoryOpenFailureRetainsMemoryAndBlocksCleanupAndDelivery() async {
        let history = RemyHistoryController(repository:nil,openingFailure:.storage(code:13))
        let engine = HistoryIntegrationEngine()
        engine.finalText = "recoverable raw"
        var cleaned = 0, delivered = 0
        let app = app(engine:engine,history:history,cleanup: { text,_ in cleaned += 1; return text },delivery: { _ in delivered += 1; return .copiedOnly })
        await app.startDictation(); await app.stopDictation()
        XCTAssertEqual(cleaned,0); XCTAssertEqual(delivered,0)
        XCTAssertTrue(app.historyNotSaved)
        XCTAssertEqual(app.historyEntries.first?.rawText,"recoverable raw")
        XCTAssertEqual(app.historyEntries.first?.text,"recoverable raw")
    }

    func testRawCommitFailureRetainsMemoryAndBlocksCleanupAndDelivery() async throws {
        _ = try repository()
        let readOnly = try RemyHistoryRepository(url:directory.appendingPathComponent("history.sqlite"),readOnly:true)
        let history = RemyHistoryController(repository:readOnly)
        let engine = HistoryIntegrationEngine()
        engine.finalText = "raw write failed"
        var cleaned = 0, delivered = 0
        let app = app(engine:engine,history:history,cleanup: { text,_ in cleaned += 1; return text },delivery: { _ in delivered += 1; return .copiedOnly })
        await app.startDictation(); await app.stopDictation()
        XCTAssertEqual(cleaned,0); XCTAssertEqual(delivered,0)
        XCTAssertTrue(app.historyNotSaved)
        XCTAssertEqual(app.historyEntries.first?.rawText,"raw write failed")
        let rows = try await readOnly.page()
        XCTAssertTrue(rows.isEmpty)
    }

    func testHistoryAndRecoveryMetadataFailureNeverDeletesExistingAudio() async throws {
        let history = RemyHistoryController(repository: nil, openingFailure: .storage(code: 13))
        let engine = HistoryIntegrationEngine()
        engine.finalText = "Raw words survive the failed save"
        let recovery = DictationRecoveryStore(directory: directory.appendingPathComponent("failed-recovery"),
            metadataWriter: { _, _ in throw CocoaError(.fileWriteOutOfSpace) })
        var delivered = 0
        let app = app(engine: engine, history: history, recoveryStore: recovery,
            delivery: { _ in delivered += 1; return .copiedOnly })
        await app.startDictation()
        let capture = try XCTUnwrap(engine.recoveryCapture)
        capture.append([0.125, -0.5, 0.75])
        await app.stopDictation()
        XCTAssertEqual(delivered, 0)
        XCTAssertTrue(app.historyNotSaved)
        XCTAssertEqual(app.historyEntries.first?.rawText, engine.finalText)
        XCTAssertTrue(FileManager.default.fileExists(atPath: capture.url.path))
        XCTAssertEqual(recovery.entries.first?.completedTranscript, engine.finalText)
        XCTAssertEqual(try RecoveryAudioReader(url: capture.url).nextSamples(), [0.125, -0.5, 0.75])
    }

    func testHistoryOffIsCapturedAndPreventsRecoveryAudioWithPreservationEnabled() async throws {
        let settings = Settings.shared
        settings.historyEnabled = false; settings.preserveCancelledSessions = true
        let repository = try repository(), engine = HistoryIntegrationEngine()
        let history = RemyHistoryController(repository:repository)
        var delivered = 0
        let app = app(engine:engine,history:history,delivery: { _ in delivered += 1; return .copiedOnly })
        await app.startDictation()
        XCTAssertNil(engine.recoveryCapture)
        settings.historyEnabled = true
        await app.stopDictation()
        let offRows = try await repository.page()
        XCTAssertTrue(offRows.isEmpty)
        XCTAssertTrue(app.historyEntries.isEmpty)
        XCTAssertTrue(app.recoveryStore.entries.isEmpty)
        let recovery = directory.appendingPathComponent("recovery")
        let files = (try? FileManager.default.contentsOfDirectory(atPath:recovery.path)) ?? []
        XCTAssertTrue(files.isEmpty)
        XCTAssertEqual(delivered,1)
        await app.startDictation()
        XCTAssertEqual(app.status,.recording)
        settings.historyEnabled = false
        await app.stopDictation()
        let enabledRows = try await repository.page()
        XCTAssertEqual(enabledRows.count,1)
        XCTAssertEqual(delivered,2)
    }

    func testCancelDuringHistoryContextPreparationCannotStartLateCapture() async throws {
        let repository = try repository()
        let started = expectation(description:"session generation lookup suspended")
        let gate = HistoryIntegrationGate(started:started)
        defer { gate.resolve("release") }
        var generationRequests = 0
        let history = RemyHistoryController(repository:repository,sessionGenerationLoader: {
            generationRequests += 1
            if generationRequests == 1 { _ = await gate.wait() }
            return try await repository.generation()
        })
        let engine = HistoryIntegrationEngine()
        var delivered = 0
        let app = app(engine:engine,history:history,delivery: { _ in delivered += 1; return .copiedOnly })
        let start = Task { await app.startDictation() }
        await fulfillment(of:[started],timeout:2)
        XCTAssertTrue(app.canCancelDictation)
        await app.cancelDictation()
        gate.resolve("release")
        await start.value
        XCTAssertEqual(engine.startCount,0)
        XCTAssertEqual(delivered,0)
        XCTAssertEqual(app.status,.idle)
        XCTAssertTrue(app.historyEntries.isEmpty)
        let rows = try await repository.page()
        XCTAssertTrue(rows.isEmpty)
        await app.startDictation()
        XCTAssertEqual(engine.startCount,1)
        XCTAssertEqual(app.status,.recording)
        await app.stopDictation()
        XCTAssertEqual(delivered,1)
    }

    func testDeleteDuringDelayedCleanupPreventsLateDelivery() async throws {
        try await delayedCleanupDeletion(clear:false)
    }
    func testClearDuringDelayedCleanupPreventsLateDelivery() async throws {
        try await delayedCleanupDeletion(clear:true)
    }
    func testClearAllStoredHistoryDuringDelayedCleanupPreventsLateDeliveryAndRecreation() async throws {
        try await delayedCleanupDeletion(clear:true, fullClear:true)
    }
    private func delayedCleanupDeletion(clear: Bool, fullClear: Bool = false) async throws {
        Settings.shared.transcriptPostProcessingMode = .ollama
        let repository = try repository(), engine = HistoryIntegrationEngine()
        let paths = RemyStoragePaths(applicationSupportDirectory: directory, bundleIdentifier: "test.remy.delayed-deletion")
        let suite = "RemyDelayedDeletionTests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let snapshot = paths.historyDirectory.appendingPathComponent("history-before-v2-\(UUID()).sqlite")
        if fullClear {
            try paths.prepareHistoryDirectory()
            try Data("synthetic rollback".utf8).write(to: snapshot)
            defaults.set(Data("synthetic legacy".utf8), forKey: "transcriptHistory")
        }
        let history = RemyHistoryController(repository:repository)
        let started = expectation(description:"synthetic cleanup suspended")
        let gate = HistoryIntegrationGate(started:started)
        defer { gate.resolve("late synthetic result") }
        var delivered = 0
        let app = app(engine:engine,history:history,
            recoveryStore: fullClear ? DictationRecoveryStore(directory: paths.recoveryDirectory) : nil,
            cleanup: { _,_ in await gate.wait() },delivery: { _ in delivered += 1; return .copiedOnly })
        await app.startDictation()
        let stop = Task { await app.stopDictation() }
        await fulfillment(of:[started],timeout:2)
        let rows = try await repository.page()
        let id = try XCTUnwrap(rows.first?.id)
        let cancellationStarted = expectation(description:"delete revoked session and stopped capture")
        engine.onStopAudioCapture = { cancellationStarted.fulfill() }
        let deletion = Task {
            if fullClear { await app.clearAllStoredHistory(paths: paths, defaults: defaults) }
            else if clear { await app.clearHistory() }
            else { await app.removeHistoryEntry(id:id) }
        }
        await fulfillment(of:[cancellationStarted],timeout:2)
        if fullClear {
            XCTAssertTrue(app.isDeletingStoredHistory)
            let count = engine.startCount
            await app.startDictation()
            XCTAssertEqual(engine.startCount, count, "A new recording must not start during deletion")
        }
        // Cancellation joins the deliberately noncooperative cleanup; release it only after
        // capture-stop proves revocation happened, then join both operations.
        engine.onStopAudioCapture = nil
        gate.resolve("late synthetic result")
        await deletion.value
        await stop.value
        XCTAssertEqual(delivered,0)
        let remaining = try await repository.page()
        XCTAssertTrue(remaining.isEmpty)
        XCTAssertTrue(app.historyEntries.isEmpty)
        if fullClear {
            XCTAssertNil(app.storedHistoryDeletionError)
            XCTAssertFalse(app.isDeletingStoredHistory)
            XCTAssertFalse(FileManager.default.fileExists(atPath: snapshot.path))
            XCTAssertNil(defaults.object(forKey: "transcriptHistory"))
            let reopened = try RemyHistoryRepository(url: directory.appendingPathComponent("history.sqlite"))
            let reopenedRows = try await reopened.page()
            XCTAssertTrue(reopenedRows.isEmpty)
        }
    }
}

@MainActor private final class HistoryIntegrationGate {
    let started: XCTestExpectation
    private var continuation: CheckedContinuation<String,Never>?
    private var value: String?
    init(started: XCTestExpectation) { self.started = started }
    func wait() async -> String {
        started.fulfill()
        if let value { return value }
        return await withCheckedContinuation { continuation = $0 }
    }
    func resolve(_ text: String) { value = text; continuation?.resume(returning:text); continuation = nil }
}

@MainActor
private final class HistoryIntegrationEngine: TranscriptionEngine {
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
