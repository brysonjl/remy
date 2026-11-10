//
//  AppState.swift
//  Dictate Anywhere
//
//  Central observable state. Owns all services and orchestrates dictation flow.
//

import Foundation
import AppKit
import CoreAudio
import AVFoundation
import IOKit
import os
import FoundationModels

@Observable
@MainActor
final class AppState {
    // MARK: - Dictation Status

    enum DictationStatus: Equatable {
        case idle
        case recording
        case processing
        case error(String)

        var diagnosticLabel: String {
            switch self {
            case .idle: "idle"
            case .recording: "recording"
            case .processing: "processing"
            case .error: "error"
            }
        }
    }

    private let logger = Logger(
        subsystem: RemyDiagnostics.subsystem,
        category: "AppState"
    )

    var status: DictationStatus = .idle {
        didSet { updateCancellationAvailability() }
    }
    var currentTranscript = ""
    var lastTranscript = ""
    var selectedPage: SidebarPage = .models
    var selectedAttentionIssueID: AttentionIssue.ID?
    var ollamaDeletingModel: String?
    var ollamaModelActionError: String?
    var ollamaModelActionsRevision = 0
    var enginePreparationError: String?
    var recordingStartError: String?

    /// Static accessor for AppDelegate menu bar (avoids circular dependency)
    nonisolated(unsafe) static var lastTranscriptForMenuBar = ""

    // MARK: - Services

    let permissions: Permissions
    let settings = Settings.shared
    let hotkeyService = HotkeyService()
    let audioMonitor = AudioMonitor()
    let volumeController = VolumeController()
    let textInserter: TextInserter
    let overlay = OverlayWindow()
    let audioDeviceManager = AudioDeviceManager()
    let parakeetEngine = ParakeetEngine()
    let appleSpeechEngine = AppleSpeechEngine()
    let assemblyAIEngine = AssemblyAIEngine()
    let s1MiniModelManager = S1MiniModelManager()
    let inputSourceMonitor = InputSourceMonitor()
    let transforms = TransformStore.shared
    let transformRunner: TransformRunner
    let recoveryStore: DictationRecoveryStore
    private(set) var history: RemyHistoryController?
    private(set) var cleanupProfiles: CleanupProfileController?
    private let cleanupProfileCredentials: (any CleanupProfileCredentialStore)?
    private(set) var cleanupProfileError: String?
    private(set) var isPreparingHistory = false
    private(set) var isDeletingStoredHistory = false
    private(set) var storedHistoryDeletionError: String?
    private var sessionHistoryContext: RemyHistorySessionContext?
    var historyEntries: [TranscriptHistoryEntry] {
        guard let history else { return settings.transcriptHistory }
        return history.entries.reversed().map {
            TranscriptHistoryEntry(id: $0.id, text: $0.selectedText, createdAt: $0.createdAt,
                rawText: $0.continuationPrefix?.isEmpty == false ? $0.rawChunk : $0.rawText)
        }
    }
    var historyNotSaved: Bool { history?.notSaved != nil }
    private var recoveryCapture: RecoveryAudioCapture?
    private var preserveSessionOnCancellation = false
    private var completedRecognitionTranscript: String?
    private var recordingSafetyFailure: String?
    private var processingTask: Task<Void, Never>?
    private var processingOperationID: UUID?
    private var isCancelling = false
    private var isDeliveringTranscript = false
    typealias SessionCoordinator = RemySessionCoordinator<RemyDictationConfiguration>
    private let sessions = SessionCoordinator()
    private var sessionToken: SessionCoordinator.Token?
    private var startingRequestID: UUID?
    private var sessionCleanupRuntime: RemyCleanupRuntime?
    private var deliveryAuthorization: RemyDeliveryAuthorization?
    private let authorizedTranscriptDeliveryOverride: ((String, RemyDeliveryAuthorization) async -> TextInsertionResult)?
    typealias DestinationResolution = (RemyDictationDestination, RemyDestinationRouter.Destination, RemyDeliveryAuthorization) async -> RemyDestinationRouter.Destination
    private let destinationResolutionOverride: DestinationResolution?
    private let transcriptCleanupOverride: ((String, RemyDictationConfiguration) async throws -> String)?
    private typealias CleanupAttempt = RemyCleanupAttempt<SessionCoordinator.Token>
    private var cleanupAttempts: [SessionCoordinator.Token: CleanupAttempt] = [:]
    private let cleanupTimeout: Duration
    var isCleanupDraining: Bool { cleanupAttempts.values.contains { $0.isDraining } }
    var canUseRawCleanup: Bool {
        guard status == .processing, let sessionToken else { return false }
        return cleanupAttempts[sessionToken]?.outcome == nil && cleanupAttempts[sessionToken] != nil
    }
    func useRawCleanupNow() {
        guard let sessionToken else { return }
        cleanupAttempts[sessionToken]?.useRawNow()
    }
    private(set) var recoveringEntryID: UUID?
    private(set) var continuingEntryID: UUID?
    private var transcriptPrefix = ""
    private var previousRecordingDuration: TimeInterval = 0
    private var continuationTargetBundleIdentifier: String?
    private let engineOverride: TranscriptionEngine?
    private let transcriptDeliveryOverride: ((String) async -> TextInsertionResult)?
    private let inputSourceIDOverride: (() -> String?)?
    private let profileModelAvailableOverride: ((ParakeetModelChoice) -> Bool)?
    private let appleSpeechAssetSnapshot: (() async -> (supported: [SupportedLanguage], installed: [SupportedLanguage]))?
    private let appleSpeechAssetInstaller: ((SupportedLanguage) async throws -> Void)?
    private var appleSpeechInstallationTask: Task<Void, Never>?
    private(set) var isInstallingAppleSpeech = false

    var canCancelDictation: Bool {
        (status == .recording || status == .processing || startingRequestID != nil)
            && !isCancelling && !isDeliveringTranscript
            && deliveryAuthorization?.snapshot.pasteClaimed != true && recoveringEntryID == nil
    }

    var canStopDictation: Bool { status == .recording && !isTransitioning }

    private func updateCancellationAvailability() {
        hotkeyService.isCancellationEnabled = canCancelDictation || transformRunner.canCancel
    }
    var appleSpeechSupportedLanguages: [SupportedLanguage] = []
    var appleSpeechInstalledLanguages: [SupportedLanguage] = []
    var appleSpeechUnsupportedSelection = false

    /// Whether the app is transitioning between states (simple guard)
    private var isTransitioning = false

    /// Set when a hold-to-record key-up arrives during a transition (race condition guard)
    private var pendingHoldRelease = false

    private(set) var isHoldToRecordKeyDown = false

    /// True while prepareActiveEngine is running (suppresses transient "not ready" warnings)
    var isPreparingEngine = false

    /// Audio level polling loop
    private var audioLevelTask: Task<Void, Never>?

    /// App that was frontmost when dictation started (used as paste target)
    private var insertionTargetApp: NSRunningApplication? {
        didSet { if insertionTargetApp == nil { insertionTargetSnapshot = .unavailable } }
    }
    private(set) var insertionTargetSnapshot: RemyInsertionTarget = .unavailable
    private let insertionTargetCaptureOverride: ((NSRunningApplication?) async -> RemyInsertionTarget)?
    private var sessionDictationContext: DictationContext?
    private var contextCaptureID: UUID?
    private var contextApplicationTask: Task<Void, Never>?
    private weak var contextCaptureEngine: (any TranscriptionEngine)?
    private let contextCaptureOverride: (@Sendable (pid_t?) async -> DictationContext?)?
    /// Names read from the active window for the current session only.
    /// Memory only: never persisted, logged, or added to the configuration.
    private var sessionScreenNames: [String] = []
    private var screenNamesTask: Task<Void, Never>?
    private let screenNamesCaptureOverride: (@Sendable (DictationContext) async -> [String])?

    /// Engine pinned for the active dictation session (start -> stop/cancel).
    private var sessionEngine: TranscriptionEngine?
    private var sessionHotkeyMode: HotkeyMode?
    private var activeRecordingStartupID: UUID?
    private var recordingStartTask: Task<Void, Error>?
    private var recordingInputDeviceID: AudioDeviceID?

    var automaticMicrophoneTitle: String {
        // Only annotate Automatic when it describes the current selection.
        if let uid = settings.selectedMicrophoneUID,
           audioDeviceManager.availableInputDevices.contains(where: { $0.uid == uid }) {
            return "Automatic"
        }
        let deviceID = status == .recording ? recordingInputDeviceID : MicrophoneHelper.effectiveDeviceID()
        guard let deviceID, let name = AudioDeviceManager.deviceName(for: deviceID) else {
            return "Automatic"
        }
        return "Automatic (\(name))"
    }
    private var startupTask: Task<Void, Never>?
    /// The startup sequence refreshes these assets before resolving input-source
    /// profiles. Preparation during that sequence can reuse the same snapshot.
    private var appleSpeechAssetsRefreshedDuringStartup = false
    private struct EnginePreparationKey: Equatable {
        let engine: TranscriptionEngineChoice
        let model: ParakeetModelChoice
        let language: SupportedLanguage
        let appleSpeechLanguage: SupportedLanguage
        let prewarmModel: Bool
    }

    private struct EnginePreparation {
        let id: UUID
        let key: EnginePreparationKey
        let selectionOperationID: UUID
        let task: Task<Void, Never>
    }

    private var enginePreparation: EnginePreparation?
    private var selectionOperationID = UUID()
    private var appleSpeechIdlePreparation: Task<Void, Never>?
    private var appleSpeechIdlePreparationID: UUID?
    private var recordingCleanupPreparation: Task<Void, Never>?
    struct CleanupPreparationKey: Equatable {
        let engine: TranscriptionEngineChoice
        let model: ParakeetModelChoice
        let mode: TranscriptPostProcessingMode
        let language: SupportedLanguage
        let enabled: Bool
        let installed: Bool
        let runtimeRevision: Int
        let speechReady: Bool
        let idle: Bool
        let prompt: String
        let vocabulary: [String]
        let providerURL: String
        let providerModel: String
        let credentialsRevision: Int
        let context: DictationPostProcessingContext?
    }

    private struct CleanupPreparation {
        let id: UUID
        let key: CleanupPreparationKey
        let task: Task<Bool, Never>
    }

    /// Model preparation outlives a settings view; shutdown must join blocking
    /// inference before releasing the engine underneath it.
    private var cleanupPreparation: CleanupPreparation?
    private var cleanupRuntimeReleaseInProgress = false
    private var cleanupRuntimeReleaseWaiters: [CheckedContinuation<Void, Never>] = []
    private var preparedCleanupKey: CleanupPreparationKey?
    var isPreparingCleanupEngine = false
    private var cleanupPreparationFailure: (key: CleanupPreparationKey, message: String)?
    private let cleanupModelPreparation: ((TranscriptPostProcessingMode) async -> Bool)?
    private var hasStarted = false
    private var isShuttingDown = false
    private var isSystemSleeping = false
    private var activationGeneration = UUID()
    @ObservationIgnored private var systemSleepObserver: NSObjectProtocol?
    @ObservationIgnored private var systemWakeObserver: NSObjectProtocol?
    private var presentedBlockingAttentionIssues = Set<AttentionIssue.ID>()

    /// Serializes profile applies; a change arriving mid-apply queues behind it.
    private var inputSourceApplyTask: Task<Void, Never>?

    private func beginSelectionOperation() -> UUID {
        let id = UUID()
        selectionOperationID = id
        return id
    }

    private func ownsSelectionOperation(_ id: UUID) -> Bool {
        selectionOperationID == id && !isShuttingDown
    }

    /// Optional test hook for suspending the first microphone permission request.
    private let microphonePermissionRequester: (@MainActor @Sendable () async -> Bool)?

    // MARK: - Cleanup profiles

    var canSaveCurrentCleanupAsProfile: Bool {
        cleanupProfiles?.collection.profiles.isEmpty == true
            && [.openAICompatible, .ollama].contains(settings.transcriptPostProcessingMode)
    }

    /// Explicit conversion inside the app. No caller receives a credential;
    /// legacy settings and credential entries remain available after the commit.
    func saveCurrentCleanupAsProfile() {
        guard let cleanupProfiles, canSaveCurrentCleanupAsProfile else { return }
        do {
            let compatible = settings.transcriptPostProcessingMode == .openAICompatible
            let endpoint = try CleanupEndpointIdentity(baseURL: compatible ? settings.openAICompatibleBaseURL : settings.ollamaBaseURL)
            let snapshot = CleanupLegacyProfileSnapshot(
                migrationID: compatible ? "remy-compatible-profile-v1" : "remy-ollama-profile-v1",
                profileID: UUID(uuidString: compatible ? "0D24457B-AE2A-4207-9B2D-C3D9451C5281" : "78E390B5-3867-4833-A6D2-5EC980AEF52B")!,
                name: compatible ? (URLComponents(string: endpoint.canonicalURL)?.host == "ollama.com" ? "Ollama Cloud" : "My compatible server") : "My Ollama server",
                adapter: compatible ? .openAICompatible : .ollama,
                baseURL: compatible ? settings.openAICompatibleBaseURL : settings.ollamaBaseURL,
                model: compatible ? settings.openAICompatibleModel : settings.ollamaModel,
                prompt: compatible ? settings.openAICompatiblePostProcessingPrompt : settings.ollamaPostProcessingPrompt,
                promptVersion: 1, timeoutSeconds: compatible ? 60 : 120,
                capabilities: .init(transport: .completeJSON, discovery: false, structuredOutput: false, thinkingControl: false),
                credentialSource: compatible && !settings.openAICompatibleAPIKey.isEmpty
                    ? .init(identifier: "openai-compatible", expectedEndpoint: endpoint) : nil)
            try cleanupProfiles.migrate(snapshot: snapshot)
            guard cleanupProfiles.selectedProfile?.id == snapshot.profileID else {
                cleanupProfileError = "Existing profiles were kept. Choose the profile you want to use."
                return
            }
            settings.transcriptPostProcessingMode = .profile
            cleanupProfileError = nil
        } catch {
            cleanupProfileError = "The current setup could not be saved as a profile. Its original settings and key have been kept."
        }
    }

    /// The explicit UI action sends synthetic text only; it has no history,
    /// clipboard, destination or automatic-paste authority.
    func testCleanupProfile(_ profile: CleanupProfile) async -> String? {
        guard status == .idle, !isShuttingDown,
              cleanupProfiles?.collection.profiles.contains(profile) == true else { return "unavailable" }
        let configuration = RemyDictationConfiguration(capture: .init(),
            cleanup: .init(mode: .profile, profile: profile))
        let runtime = RemyCleanupRuntime.makeProfile(configuration: configuration, credentials: cleanupProfileCredentials)
        do {
            let answer = try await runtime.process(text: "Remy connection check. Keep the final blue word.")
            return answer.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "empty" : nil
        } catch { return "failed" }
    }

    // MARK: - Active Engine

    var activeEngine: TranscriptionEngine {
        if let engineOverride { return engineOverride }
        switch RemyFeaturePolicy.effectiveEngineChoice(settings.engineChoice) {
        case .parakeet:
            return parakeetEngine
        case .appleSpeech:
            return AppleSpeechEngine.isSupported ? appleSpeechEngine : parakeetEngine
        case .assemblyAI:
            return parakeetEngine
        }
    }

    var availableEngineChoices: [TranscriptionEngineChoice] {
        RemyFeaturePolicy.supportedEngineChoices
    }

    // MARK: - Initialization

    init(
        permissions: Permissions? = nil,
        microphonePermissionRequester: (@MainActor @Sendable () async -> Bool)? = nil,
        recoveryStore: DictationRecoveryStore? = nil,
        historyController: RemyHistoryController? = nil,
        cleanupProfileController: CleanupProfileController? = nil,
        cleanupProfileCredentials: (any CleanupProfileCredentialStore)? = nil,
        engine: TranscriptionEngine? = nil,
        transcriptDelivery: ((String) async -> TextInsertionResult)? = nil,
        transcriptCleanup: ((String, RemyDictationConfiguration) async throws -> String)? = nil,
        authorizedTranscriptDelivery: ((String, RemyDeliveryAuthorization) async -> TextInsertionResult)? = nil,
        destinationResolution: DestinationResolution? = nil,
        contextCapture: (@Sendable (pid_t?) async -> DictationContext?)? = nil,
        screenNamesCapture: (@Sendable (DictationContext) async -> [String])? = nil,
        insertionTargetCapture: ((NSRunningApplication?) async -> RemyInsertionTarget)? = nil,
        cleanupTimeout: Duration = .seconds(8),
        inputSourceID: (() -> String?)? = nil,
        profileModelAvailable: ((ParakeetModelChoice) -> Bool)? = nil,
        appleSpeechAssetSnapshot: (() async -> (supported: [SupportedLanguage], installed: [SupportedLanguage]))? = nil,
        appleSpeechAssetInstaller: ((SupportedLanguage) async throws -> Void)? = nil,
        cleanupModelPreparation: ((TranscriptPostProcessingMode) async -> Bool)? = nil,
        transformRunner: TransformRunner? = nil
    ) {
        self.permissions = permissions ?? Permissions()
        self.transformRunner = transformRunner ?? TransformRunner()
        self.textInserter = TextInserter(permissions: self.permissions)
        self.microphonePermissionRequester = microphonePermissionRequester
        self.recoveryStore = recoveryStore ?? DictationRecoveryStore()
        self.history = historyController ?? (AppDelegate.isRunningTests ? nil : RemyHistoryBootstrap.makeController())
        self.isPreparingHistory = !AppDelegate.isRunningTests
        if let cleanupProfileController {
            self.cleanupProfiles = cleanupProfileController
            self.cleanupProfileCredentials = cleanupProfileCredentials
        } else if !AppDelegate.isRunningTests {
            let credentials = CleanupProfileKeychainStore()
            self.cleanupProfileCredentials = credentials
            do {
                let repository = try CleanupProfileFileRepository(rootURL: RemyStoragePaths.current.cleanupProfilesDirectory)
                let controller = CleanupProfileController(repository: repository, credentials: credentials)
                try controller.load()
                self.cleanupProfiles = controller
            } catch {
                self.cleanupProfiles = nil
                self.cleanupProfileError = "Cleanup profiles could not be loaded. Existing provider settings have been kept."
            }
        } else {
            self.cleanupProfiles = nil
            self.cleanupProfileCredentials = nil
        }
        self.engineOverride = engine
        self.transcriptDeliveryOverride = transcriptDelivery
        self.transcriptCleanupOverride = transcriptCleanup
        self.authorizedTranscriptDeliveryOverride = authorizedTranscriptDelivery
        self.destinationResolutionOverride = destinationResolution
        self.contextCaptureOverride = contextCapture
        self.screenNamesCaptureOverride = screenNamesCapture
        self.insertionTargetCaptureOverride = insertionTargetCapture
        self.cleanupTimeout = cleanupTimeout
        self.inputSourceIDOverride = inputSourceID
        self.profileModelAvailableOverride = profileModelAvailable
        self.appleSpeechAssetSnapshot = appleSpeechAssetSnapshot
        self.appleSpeechAssetInstaller = appleSpeechAssetInstaller
        self.cleanupModelPreparation = cleanupModelPreparation
        setupHotkeyCallbacks()
        setupPermissionCallbacks()
        setupInputSourceCallbacks()
        setupTransformRunner(usesLiveDependencies: transformRunner == nil && !AppDelegate.isRunningTests)
    }

    // MARK: - Hotkey Callbacks

    private func setupHotkeyCallbacks() {
        hotkeyService.onKeyDown = { [weak self] binding in
            guard let self else { return }
            let generation = self.activationGeneration
            Task { @MainActor [weak self] in
                guard let self, !self.isSystemSleeping, self.activationGeneration == generation else { return }
                switch binding.mode {
                case .holdToRecord:
                    self.isHoldToRecordKeyDown = true
                    await self.startDictation(mode: binding.mode)
                case .handsFreeToggle:
                    if self.status == .recording {
                        await self.stopDictation()
                    } else {
                        await self.startDictation(mode: binding.mode)
                    }
                }
            }
        }

        hotkeyService.onKeyUp = { [weak self] binding in
            guard let self else { return }
            let generation = self.activationGeneration
            Task { @MainActor [weak self] in
                guard let self, !self.isSystemSleeping, self.activationGeneration == generation else { return }
                guard binding.mode == .holdToRecord else { return }
                self.isHoldToRecordKeyDown = false
                if self.status == .recording, !self.isTransitioning {
                    await self.stopDictation()
                } else if self.isTransitioning {
                    // Key released while startDictation() is still running;
                    // startDictation will check this flag after its transition.
                    self.pendingHoldRelease = true
                }
            }
        }

        hotkeyService.onTransform = { [weak self] id in
            guard let self else { return }
            let generation = self.activationGeneration
            Task { @MainActor [weak self] in
                guard let self, !self.isSystemSleeping, self.activationGeneration == generation else { return }
                await self.runTransform(id: id)
            }
        }

        hotkeyService.onCancel = { [weak self] in
            guard let self else { return }
            let generation = self.activationGeneration
            Task { @MainActor [weak self] in
                guard let self, !self.isSystemSleeping, self.activationGeneration == generation else { return }
                if self.transformRunner.canCancel {
                    self.transformRunner.cancel()
                    return
                }
                await self.cancelDictation()
            }
        }
        hotkeyService.onCancelProgress = { [weak self] progress in
            self?.overlay.setCancellationProgress(progress)
        }
    }

    // MARK: - Transforms

    private func setupTransformRunner(usesLiveDependencies: Bool) {
        if usesLiveDependencies {
            transformRunner.dependencies = .live(settings: settings, permissions: permissions,
                textInserter: textInserter, profile: { [weak self] in self?.cleanupProfiles?.selectedProfile },
                credentials: cleanupProfileCredentials)
        }
        transformRunner.onPhaseChange = { [weak self] in self?.updateCancellationAvailability() }
        transformRunner.onEvent = { [weak self] event in self?.overlay.present(event) }
    }

    /// Any dictation activity blocks a transform; a busy transform blocks dictation.
    var isDictationBusy: Bool {
        if status == .recording || status == .processing { return true }
        return isTransitioning || startingRequestID != nil || isCleanupDraining
            || recoveringEntryID != nil || continuingEntryID != nil
    }

    func runTransform(id: UUID) async {
        guard !isShuttingDown, !isSystemSleeping,
              let transform = transforms.transform(id: id), transform.isEnabled else { return }
        await transformRunner.run(transform.instruction, dictationBusy: isDictationBusy)
    }

    private func setupPermissionCallbacks() {
        permissions.onAccessibilityPermissionChanged = { [weak self] granted in
            Task { @MainActor [weak self] in
                self?.handleAccessibilityPermissionChanged(granted)
            }
        }
    }

    var attentionIssues: [AttentionIssue] {
        AttentionIssue.pending(
            permissionsChecked: permissions.hasChecked,
            microphoneGranted: permissions.micGranted,
            microphoneCanPrompt: permissions.canPromptForMicrophone,
            accessibilityGranted: permissions.accessibilityGranted,
            engineChoice: settings.engineChoice,
            speechSetupNeeded: speechSetupNeeded,
            automationDenied: permissions.automationDenied,
            speechPreparationFailed: enginePreparationError != nil,
            recoveryError: recoveryStore.errorMessage,
            recordingError: recordingStartError,
            legacyAppleSpeechMigrationPending: settings.legacyAppleSpeechMigrationPending,
            appleSpeechUnsupportedSelection: appleSpeechUnsupportedSelection,
            appleSpeechRequiresMacOS26: !AppleSpeechEngine.isOperatingSystemSupported,
            cleanupProblems: cleanupAttentionProblems
        )
    }

    private var cleanupAttentionProblems: [AttentionIssue.CleanupProblem] {
        guard settings.engineChoice != .assemblyAI else { return [] }
        switch settings.transcriptPostProcessingMode {
        case .none:
            return []
        case .fluidAudioVocabulary:
            return settings.engineChoice != .parakeet || !settings.parakeetModelChoice.supportsFluidAudioVocabulary
                ? [.fluidAudioVocabularyUnavailable] : []
        case .appleIntelligence:
            guard #available(macOS 26, *) else { return [.appleIntelligenceRequiresMacOS26] }
            switch AIPostProcessingService.availability {
            case .available: return []
            case .unavailable(.deviceNotEligible): return [.appleIntelligenceDeviceIneligible]
            case .unavailable(.appleIntelligenceNotEnabled): return [.appleIntelligenceNotEnabled]
            case .unavailable(.modelNotReady): return []
            case .unavailable(_): return [.appleIntelligenceUnavailable]
            }
        case .s1Mini:
            var problems: [AttentionIssue.CleanupProblem] = []
            let language = settings.engineChoice == .appleSpeech
                ? settings.appleSpeechLanguage : settings.selectedLanguage
            if language != .english { problems.append(.s1MiniLanguageUnsupported) }
            if !s1MiniModelManager.isModelDownloaded && !s1MiniModelManager.isBusy {
                problems.append(.s1MiniNotDownloaded)
            }
            return problems
        case .ollama:
            return settings.ollamaModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                ? [.ollamaModelMissing] : []
        case .openRouter:
            var problems: [AttentionIssue.CleanupProblem] = []
            let keyStatus = OpenRouterPostProcessingService.apiKeyStatus(
                apiKey: settings.openRouterAPIKey,
                apiKeyEnvironmentVariable: settings.openRouterAPIKeyEnvironmentVariable
            )
            if case .missing = keyStatus.source { problems.append(.openRouterKeyMissing) }
            if settings.openRouterModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                problems.append(.openRouterModelMissing)
            }
            return problems
        case .openAICompatible:
            return settings.openAICompatibleModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                ? [.openAICompatibleModelMissing] : []
        case .profile:
            return cleanupProfiles?.selectedProfile == nil ? [.savedProfileMissing] : []
        }
    }

    func reportUnsupportedAppleSpeechSelection() {
        appleSpeechUnsupportedSelection = true
        selectedAttentionIssueID = .appleSpeechUnsupported
    }

    private var speechSetupNeeded: Bool {
        guard !activeEngine.isReady, !isPreparingEngine else { return false }
        if enginePreparationError != nil { return true }
        switch settings.engineChoice {
        case .parakeet:
            return !parakeetEngine.isDownloading && !parakeetEngine.isModelDownloaded
        case .appleSpeech:
            return !appleSpeechInstalledLanguages.contains(settings.appleSpeechLanguage)
        case .assemblyAI:
            return !assemblyAIEngine.isReady
        }
    }

    func resolveAttentionIssue(_ id: AttentionIssue.ID) {
        switch id {
        case .microphone:
            Task {
                await permissions.resolve(.microphone)
                rearmResolvedBlockingAttentionIssues()
            }
        case .accessibility:
            Task { await permissions.resolve(.accessibility) }
        case .speechSetup:
            selectedPage = .models
        case .recovery:
            recoveryStore.errorMessage = nil
        case .recordingFailed:
            recordingStartError = nil
        case .appleSpeechUnsupported:
            appleSpeechUnsupportedSelection = false
        case .cleanup(let problem):
            if problem == .fluidAudioVocabularyUnavailable {
                selectedPage = .models
            } else if problem == .appleIntelligenceNotEnabled,
               let url = URL(string: "x-apple.systempreferences:com.apple.preference.AppleIntelligence") {
                NSWorkspace.shared.open(url)
            } else {
                selectedPage = .aiPostProcessing
            }
        case .automation:
            Task { await permissions.resolve(.automation) }
        }
    }

    func refreshPermissionsAfterActivation() async {
        await permissions.refresh()
        rearmResolvedBlockingAttentionIssues()
    }

    func updateAssemblyAIAPIKey(_ apiKey: String) {
        guard RemyFeaturePolicy.allowsCloudSpeechRecognition else { return }
        settings.assemblyAIAPIKey = apiKey
        // Entry and Clear may happen before the next view update or readiness check.
        rearmResolvedBlockingAttentionIssues()
    }

    private func rearmResolvedBlockingAttentionIssues() {
        if permissions.micGranted { presentedBlockingAttentionIssues.remove(.microphone) }
        if permissions.accessibilityGranted { presentedBlockingAttentionIssues.remove(.accessibility) }
        // Preparation temporarily hides setup warnings. Only actual readiness
        // resolves the blocker; a failed retry must not request another window.
        if activeEngine.isReady { presentedBlockingAttentionIssues.remove(.speechSetup) }
    }

    private func presentBlockingAttentionIssueOnce(_ id: AttentionIssue.ID) {
        guard presentedBlockingAttentionIssues.insert(id).inserted else { return }
        selectedAttentionIssueID = id
        NotificationCenter.default.post(name: .requestShowMainWindow, object: nil)
    }

    private func setupInputSourceCallbacks() {
        inputSourceMonitor.onSelectedInputSourceChanged = { [weak self] inputSourceID in
            self?.enqueueInputSourceProfileApply(for: inputSourceID)
        }
    }

    func start() {
        guard !hasStarted, !isShuttingDown else { return }
        hasStarted = true
        observeSystemSleep()
        do { try recoveryStore.reload() } catch { recoveryStore.errorMessage = error.localizedDescription }

        Task { [weak self] in
            await self?.s1MiniModelManager.refreshInstallationState()
        }
        startupTask = Task { [weak self] in
            await self?.runStartupSequence()
        }
    }

    private func observeSystemSleep() {
        let center = NSWorkspace.shared.notificationCenter
        systemSleepObserver = center.addObserver(forName: NSWorkspace.willSleepNotification,
            object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor [weak self] in await self?.handleSystemWillSleep() }
        }
        systemWakeObserver = center.addObserver(forName: NSWorkspace.didWakeNotification,
            object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.handleSystemDidWake() }
        }
    }

    func handleSystemWillSleep() async {
        guard !isShuttingDown else { return }
        isSystemSleeping = true
        activationGeneration = UUID()
        appleSpeechInstallationTask?.cancel()
        hotkeyService.suspendForSystemSleep()
        isHoldToRecordKeyDown = false
        pendingHoldRelease = false
        transformRunner.cancel()
        await cancelDictation(reason: .inputUnavailable)
    }

    func handleSystemDidWake() {
        guard !isShuttingDown else { return }
        isSystemSleeping = false
        hotkeyService.resumeAfterSystemWake()
    }

    /// Stops process-lifetime services before AppKit tears down the process.
    func shutdown() async {
        let trace = PerfTrace.begin("app.shutdown")
        defer { trace.end() }
        guard !isShuttingDown else { return }
        isShuttingDown = true
        let workspaceCenter = NSWorkspace.shared.notificationCenter
        if let systemSleepObserver { workspaceCenter.removeObserver(systemSleepObserver) }
        if let systemWakeObserver { workspaceCenter.removeObserver(systemWakeObserver) }
        systemSleepObserver = nil
        systemWakeObserver = nil
        startingRequestID = nil
        if let sessionToken { sessions.revoke(sessionToken, reason: .shutdown) }
        deliveryAuthorization?.revoke()
        transformRunner.cancel()
        invalidateContextCapture()
        startupTask?.cancel()
        startupTask = nil
        appleSpeechInstallationTask?.cancel()
        await appleSpeechInstallationTask?.value
        appleSpeechInstallationTask = nil
        isInstallingAppleSpeech = false
        appleSpeechIdlePreparation?.cancel()
        await appleSpeechIdlePreparation?.value
        appleSpeechIdlePreparation = nil
        appleSpeechIdlePreparationID = nil
        inputSourceApplyTask?.cancel()
        inputSourceApplyTask = nil
        activeRecordingStartupID = nil
        let recordingStartTask = recordingStartTask
        recordingStartTask?.cancel()
        processingTask?.cancel()
        for attempt in cleanupAttempts.values { attempt.cancel() }
        stopAudioLevelPolling()
        inputSourceMonitor.stopMonitoring()
        hotkeyService.stopMonitoring()
        permissions.stopPolling()

        // A cancelled finish task must unwind before its engine can be unloaded.
        await processingTask?.value
        await joinCleanupAttempts()
        processingTask = nil
        processingOperationID = nil

        // A prewarm load is a blocking C call that ignores cancellation;
        // wait for it rather than tearing down underneath it.
        cleanupPreparation?.task.cancel()
        _ = await cleanupPreparation?.task.value
        cleanupPreparation = nil
        isPreparingCleanupEngine = false
        preparedCleanupKey = nil

        recordingCleanupPreparation?.cancel()
        await recordingCleanupPreparation?.value
        recordingCleanupPreparation = nil
        if #available(macOS 26, *) { await AIPostProcessingService.discardPreparedSession() }

        // A startup load can outlive its cancelled caller. Join it before
        // cancelling the engine so a late load cannot restore stale state.
        enginePreparation?.task.cancel()
        await enginePreparation?.task.value
        enginePreparation = nil

        let engine = sessionEngine ?? activeEngine
        await engine.cancel()
        if let recordingStartTask {
            _ = try? await recordingStartTask.value
        }
        self.recordingStartTask = nil
        if engine !== parakeetEngine { await parakeetEngine.cancel() }
        if engine !== appleSpeechEngine { await appleSpeechEngine.cancel() }
        await appleSpeechEngine.invalidatePreparedSession()

        // A normal quit must drain accepted recovery writes and close the CAF
        // before process exit, including the last partial audio buffer.
        if let capture = recoveryCapture {
            do {
                _ = try await recoveryStore.preserve(capture, preview: currentTranscript,
                    completedTranscript: completedRecognitionTranscript,
                    transcriptPrefix: transcriptPrefix.isEmpty ? nil : transcriptPrefix,
                    previousDuration: previousRecordingDuration,
                    targetBundleIdentifier: insertionTargetApp?.bundleIdentifier ?? continuationTargetBundleIdentifier)
            } catch {
                recoveryStore.errorMessage = "Recovery details could not be saved before quitting. Existing audio was kept."
            }
        }
        recoveryCapture = nil
        completedRecognitionTranscript = nil
        engine.recoveryCapture = nil
        engine.setSessionContextualVocabulary([])
        engine.setSessionDictationContext(nil)
        clearEndOfUtteranceHandler(for: engine)
        sessionEngine = nil
        engine.setSessionConfiguration(nil)
        await parakeetEngine.unloadModelResources()
        sessionCleanupRuntime = nil
        sessionHotkeyMode = nil
        continuingEntryID = nil
        recoveringEntryID = nil
        transcriptPrefix = ""
        previousRecordingDuration = 0
        continuationTargetBundleIdentifier = nil
        insertionTargetApp = nil
        sessionDictationContext = nil
        currentTranscript = ""
        isDeliveringTranscript = false
        isCancelling = false
        isTransitioning = false
        status = .idle
        await settings.flushTranscriptHistory()
        volumeController.restoreMicrophoneVolume()
        volumeController.restoreAfterRecording()
        overlay.hide(afterDelay: 0)
    }

    private func runStartupSequence() async {
        let trace = PerfTrace.begin("app.startup")
        defer { trace.end() }
        if let history {
            let result = await history.prepare(importLegacyData: RemyHistoryBootstrap.legacy?.data)
            if case .notSaved(let failure) = result {
                // A failed migration/open must never authorize later database
                // writes. Keep memory history usable and suspend auto-paste.
                let memory = RemyHistoryController(repository: nil, openingFailure: failure)
                await memory.prepare(importLegacyData: RemyHistoryBootstrap.legacy?.data)
                self.history = memory
            }
        }
        isPreparingHistory = false
        guard !isShuttingDown else { return }
        // Compilation uses the paste worker queue and is independent of the
        // model toggle. Await it before enabling the dictation hotkey.
        let pasteScriptReady = await textInserter.prewarmPasteScript()
        if !pasteScriptReady {
            logger.warning("Startup paste script compilation failed; paste will retry on first use")
        }
        guard !isShuttingDown else { return }
        await PerfTrace.measure("app.permissionCheck") { await permissions.refresh() }
        guard !isShuttingDown else { return }
        updateAccessibilityIntegration(granted: permissions.accessibilityGranted, promptIfNeeded: true)
        await PerfTrace.measure("app.appleSpeechAssetRefresh") {
            await refreshAppleSpeechAssetState()
        }
        guard !isShuttingDown else { return }
        appleSpeechAssetsRefreshedDuringStartup = true
        defer { appleSpeechAssetsRefreshedDuringStartup = false }
        let startupInputSourceID = inputSourceIDOverride?() ?? inputSourceMonitor.currentInputSourceID()
        if settings.engineChoice != .assemblyAI,
           settings.inputSourceAutoSwitchEnabled,
           let startupInputSourceID {
            await PerfTrace.measure("app.inputSourceApply") {
                await enqueueInputSourceProfileApply(
                    for: startupInputSourceID,
                    prewarmModel: false
                ).value
            }
        }
        guard !isShuttingDown else { return }
        await prepareSelectedEngines(prewarmModel: settings.prewarmEnginesAtStartup)
        guard !isShuttingDown else { return }
        inputSourceMonitor.startMonitoring()
        // A source can change during model loading. Reconcile it once after
        // installing the observer so a change during startup is not missed.
        let currentInputSourceID = inputSourceIDOverride?() ?? inputSourceMonitor.currentInputSourceID()
        if settings.engineChoice != .assemblyAI,
           settings.inputSourceAutoSwitchEnabled,
           let currentInputSourceID,
           currentInputSourceID != startupInputSourceID {
            await PerfTrace.measure("app.inputSourceApply") {
                await enqueueInputSourceProfileApply(
                    for: currentInputSourceID,
                    prewarmModel: settings.prewarmEnginesAtStartup
                ).value
            }
        }
        guard !isShuttingDown else { return }
        appleSpeechAssetsRefreshedDuringStartup = false
    }

    var cleanupPreparationKey: CleanupPreparationKey {
        CleanupPreparationKey(
            engine: settings.engineChoice, model: settings.parakeetModelChoice,
            mode: settings.transcriptPostProcessingMode,
            language: settings.engineChoice == .appleSpeech
                ? settings.appleSpeechLanguage : settings.selectedLanguage,
            enabled: settings.prewarmEnginesAtStartup,
            installed: s1MiniModelManager.isModelDownloaded,
            runtimeRevision: s1MiniModelManager.runtimeRevision,
            speechReady: activeEngine.isReady, idle: status == .idle,
            prompt: settings.transcriptPostProcessingMode == .appleIntelligence ? settings.aiPostProcessingPrompt : "",
            vocabulary: [.appleIntelligence, .fluidAudioVocabulary].contains(settings.transcriptPostProcessingMode)
                ? settings.customVocabulary : [],
            providerURL: settings.transcriptPostProcessingMode == .ollama ? settings.ollamaBaseURL
                : (settings.transcriptPostProcessingMode == .openAICompatible ? settings.openAICompatibleBaseURL : ""),
            providerModel: settings.transcriptPostProcessingMode == .ollama ? settings.ollamaModel
                : (settings.transcriptPostProcessingMode == .openRouter ? settings.openRouterModel
                    : (settings.transcriptPostProcessingMode == .openAICompatible ? settings.openAICompatibleModel : "")),
            credentialsRevision: settings.transcriptPostProcessingMode == .openAICompatible
                ? settings.openAICompatibleCredentialsRevision
                : (settings.transcriptPostProcessingMode == .openRouter ? settings.openRouterCredentialsRevision : 0),
            context: settings.transcriptPostProcessingMode == .appleIntelligence
                ? postProcessingContext(includeCapturedText: true) : nil
        )
    }

    var isCleanupEnginePrepared: Bool {
        guard let prepared = preparedCleanupKey else { return false }
        return cleanupConfigurationMatches(prepared, cleanupPreparationKey)
    }

    private func cleanupConfigurationMatches(_ prepared: CleanupPreparationKey, _ current: CleanupPreparationKey) -> Bool {
        // Becoming busy does not evict resident models. Scheduling eligibility
        // is part of the task ID, but must not make Ready flicker during use.
        return prepared.engine == current.engine && prepared.model == current.model
            && prepared.mode == current.mode && prepared.language == current.language
            && prepared.installed == current.installed
            && prepared.runtimeRevision == current.runtimeRevision
            && prepared.prompt == current.prompt && prepared.vocabulary == current.vocabulary
            && prepared.providerURL == current.providerURL && prepared.providerModel == current.providerModel
            && prepared.credentialsRevision == current.credentialsRevision && prepared.context == current.context
    }

    var cleanupReadiness: ModelReadiness {
        let mode = settings.transcriptPostProcessingMode
        if mode == .profile {
            guard let profile = cleanupProfiles?.selectedProfile else {
                return cleanupProfileError.map(ModelReadiness.failed) ?? .needsSetup
            }
            if profile.routePolicy == .strictLocalOnly { return .unavailable("This profile's execution location has not been verified.") }
            return .configured
        }
        if mode == .s1Mini {
            if cleanupPreparationKey.language != .english { return .unavailable("S1-mini supports English cleanup.") }
            if s1MiniModelManager.isDownloading { return .downloading(s1MiniModelManager.downloadProgress) }
            if s1MiniModelManager.isVerifying { return .verifying }
            if s1MiniModelManager.isDeleting { return .deleting }
        }
        if mode == .fluidAudioVocabulary {
            guard settings.engineChoice == .parakeet else { return .unavailable("Requires a supported Parakeet speech model.") }
            return parakeetEngine.vocabularyReadiness
        }
        if mode == .appleIntelligence {
            guard #available(macOS 26, *) else { return .unavailable("Requires macOS 26 or later.") }
            let readiness = Self.appleCleanupReadiness(availability: AIPostProcessingService.availability,
                                                       isPrepared: isCleanupEnginePrepared)
            if readiness == .available || readiness.isReady {
                if isPreparingCleanupEngine { return .preparing }
                if let failure = cleanupPreparationFailure, cleanupConfigurationMatches(failure.key, cleanupPreparationKey) {
                    return .failed(failure.message)
                }
            }
            return readiness
        }
        if mode == .openRouter, !OpenRouterPostProcessingService.apiKeyStatus(apiKey: settings.openRouterAPIKey,
            apiKeyEnvironmentVariable: settings.openRouterAPIKeyEnvironmentVariable).isConfigured { return .needsSetup }
        if isPreparingCleanupEngine { return .preparing }
        if isCleanupEnginePrepared, ![.ollama, .openRouter, .openAICompatible].contains(mode) { return .ready }
        if let failure = cleanupPreparationFailure, cleanupConfigurationMatches(failure.key, cleanupPreparationKey) {
            return .failed(failure.message)
        }
        switch mode {
        case .s1Mini:
            return s1MiniModelManager.isModelDownloaded ? .downloaded : .notDownloaded
        case .ollama, .openRouter, .openAICompatible:
            return cleanupPreparationKey.providerModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                ? .needsSetup : .configured
        default: return .available
        }
    }

    @available(macOS 26, *)
    nonisolated static func appleCleanupReadiness(availability: SystemLanguageModel.Availability,
                                                isPrepared: Bool) -> ModelReadiness {
        switch availability {
        case .available: return isPrepared ? .ready : .available
        case .unavailable(.deviceNotEligible): return .unavailable("Apple Intelligence is unavailable on this Mac.")
        case .unavailable(.appleIntelligenceNotEnabled): return .unavailable("Enable Apple Intelligence in System Settings.")
        case .unavailable(.modelNotReady): return .preparing
        case .unavailable(_): return .unavailable("Apple Intelligence is temporarily unavailable.")
        }
    }

    var canPrepareCleanupEngine: Bool {
        guard status == .idle, !isPreparingCleanupEngine, !isShuttingDown,
              settings.transcriptPostProcessingMode != .profile,
              settings.engineChoice != .assemblyAI else { return false }
        switch cleanupReadiness {
        case .notDownloaded, .needsSetup, .unavailable, .ready, .downloading, .verifying, .preparing, .deleting: return false
        default: return true
        }
    }

    private func recordCleanupPreparation(_ ready: Bool, key: CleanupPreparationKey) {
        guard !isShuttingDown, !isCancelling,
              cleanupConfigurationMatches(key, cleanupPreparationKey) else { return }
        if ready {
            preparedCleanupKey = key
            cleanupPreparationFailure = nil
        } else {
            cleanupPreparationFailure = (key, "Could not prepare \(key.mode.displayName). Check its setup and try again.")
        }
    }

    /// First-use inference can prepare S1 even when automatic preparation is off.
    /// Only the runtime's retained, successfully exercised model earns Ready;
    /// returning the original transcript without inference does not.
    func recordS1MiniRuntimeReadiness(_ ready: Bool, for key: CleanupPreparationKey) {
        guard !isShuttingDown, key.mode == .s1Mini,
              cleanupConfigurationMatches(key, cleanupPreparationKey) else { return }
        if ready {
            guard !Task.isCancelled else { return }
            recordCleanupPreparation(true, key: key)
        } else {
            preparedCleanupKey = nil
        }
    }

    /// Also called after settings/download changes. Join a matching request;
    /// if preferences changed during loading, prepare the new choice afterward.
    /// Failure retains lazy first-use loading. Tests inject a bounded operation.
    func prepareCleanupEngineIfNeeded(force: Bool = false) async {
        await prepareCleanupEngineIfNeeded(force: force, allowRecording: false)
    }

    private func prepareCleanupEngineIfNeeded(force: Bool, allowRecording: Bool) async {
        await waitForCleanupRuntimeRelease()
        guard !isCleanupDraining else { return }
        guard !AppDelegate.isRunningTests || cleanupModelPreparation != nil else { return }
        guard !Task.isCancelled, !isShuttingDown, !isCancelling else { return }
        let previouslyPreparedKey = preparedCleanupKey ?? cleanupPreparation?.key
        let requestedMode = cleanupPreparationKey.mode
        while let inFlight = cleanupPreparation {
            if inFlight.key.mode != requestedMode { inFlight.task.cancel() }
            let ready = await inFlight.task.value
            if cleanupPreparation?.id == inFlight.id {
                cleanupPreparation = nil
                isPreparingCleanupEngine = false
                if !inFlight.task.isCancelled { recordCleanupPreparation(ready, key: inFlight.key) }
            }
            guard !Task.isCancelled, !isShuttingDown, !isCancelling else { return }
            if cleanupConfigurationMatches(inFlight.key, cleanupPreparationKey) || isCleanupEnginePrepared { return }
        }
        await releaseDeselectedCleanupRuntime(previouslyPreparedKey: previouslyPreparedKey)
        // Releasing a runtime suspends. A second settings callback may have
        // started the newly selected preparation while this caller waited.
        // Join that owner before reserving another backend load.
        while let inFlight = cleanupPreparation {
            let ready = await inFlight.task.value
            if cleanupPreparation?.id == inFlight.id {
                cleanupPreparation = nil
                isPreparingCleanupEngine = false
                if !inFlight.task.isCancelled { recordCleanupPreparation(ready, key: inFlight.key) }
            }
            guard !Task.isCancelled, !isShuttingDown, !isCancelling else { return }
            if cleanupConfigurationMatches(inFlight.key, cleanupPreparationKey) || isCleanupEnginePrepared { return }
        }
        var key = cleanupPreparationKey
        if force, settings.transcriptPostProcessingMode == .fluidAudioVocabulary,
           settings.engineChoice == .parakeet, !parakeetEngine.isReady {
            let operationID = selectionOperationID
            await prepareActiveEngine()
            guard ownsSelectionOperation(operationID),
                  cleanupConfigurationMatches(key, cleanupPreparationKey) else { return }
            key = cleanupPreparationKey
            if cleanupPreparation != nil {
                await prepareCleanupEngineIfNeeded(force: force, allowRecording: allowRecording)
                return
            }
        }
        if key.mode == .s1Mini, isCleanupEnginePrepared { return }
        let canPrepareWhileRecording = allowRecording && status == .recording
            && [.appleIntelligence, .ollama, .openRouter, .openAICompatible].contains(key.mode)
        guard !Task.isCancelled, !isShuttingDown, !isCancelling, key.enabled || force,
              key.idle || canPrepareWhileRecording, key.engine != .assemblyAI else { return }
        switch key.mode {
        case .s1Mini:
            guard S1MiniPrewarmPolicy.shouldPrewarm(
                mode: key.mode, language: key.language, prewarmEnabled: key.enabled || force
            ),
                  key.installed || cleanupModelPreparation != nil else { return }
        case .fluidAudioVocabulary:
            guard key.engine == .parakeet, key.model.supportsFluidAudioVocabulary,
                  key.speechReady else { return }
        case .appleIntelligence:
            guard #available(macOS 26, *) else { return }
            guard AIPostProcessingService.availability == .available || cleanupModelPreparation != nil else { return }
        case .ollama, .openRouter, .openAICompatible:
            guard !key.providerModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        default: return
        }
        let id = UUID()
        let portableAPIKey = settings.openAICompatibleAPIKey
        preparedCleanupKey = nil
        cleanupPreparationFailure = nil
        isPreparingCleanupEngine = true
        let task = Task(priority: .utility) { [weak self] in
            guard let self, !self.isShuttingDown, !Task.isCancelled else { return false }
            // Reserve the owner before this actor hop; concurrent callers must
            // join it rather than create another task while discard suspends.
            if key.mode != .appleIntelligence, #available(macOS 26, *) {
                await AIPostProcessingService.discardPreparedSession()
            }
            guard !self.isShuttingDown, !Task.isCancelled else { return false }
            if let prepare = self.cleanupModelPreparation { return await prepare(key.mode) }
            if key.mode == .appleIntelligence, #available(macOS 26, *) {
                return await AIPostProcessingService.prewarm(prompt: key.prompt, vocabulary: key.vocabulary, context: key.context)
            }
            if key.mode == .ollama {
                return await OllamaPostProcessingService.prewarm(baseURL: key.providerURL, model: key.providerModel, refresh: force)
            }
            if key.mode == .openRouter {
                return await OpenRouterPostProcessingService.prewarm(model: key.providerModel, refresh: force)
            }
            if key.mode == .openAICompatible {
                return await OpenAICompatiblePostProcessingService.prewarm(
                    baseURL: key.providerURL, model: key.providerModel, apiKey: portableAPIKey, refresh: force)
            }
            if key.mode == .fluidAudioVocabulary {
                return await self.parakeetEngine.prepareVocabularyIfNeeded()
            }
            guard let url = try? await self.s1MiniModelManager.validatedModelURL(),
                  !self.isShuttingDown, !Task.isCancelled else { return false }
            return await S1MiniPostProcessingService.prewarm(modelURL: url)
        }
        cleanupPreparation = CleanupPreparation(id: id, key: key, task: task)
        let ready = await task.value
        if cleanupPreparation?.id == id {
            cleanupPreparation = nil
            isPreparingCleanupEngine = false
            if !task.isCancelled { recordCleanupPreparation(ready, key: key) }
        }
        if !Task.isCancelled, !isShuttingDown, !isCancelling,
           !cleanupConfigurationMatches(key, cleanupPreparationKey), !isCleanupEnginePrepared {
            await prepareCleanupEngineIfNeeded(force: force, allowRecording: allowRecording)
        }
    }

    /// Join an obsolete preparation before freeing its runtime. Re-read the
    /// selected settings after the join so a rapid switch back keeps the model.
    private func releaseDeselectedCleanupRuntime(previouslyPreparedKey: CleanupPreparationKey?) async {
        await waitForCleanupRuntimeRelease()
        guard !isShuttingDown, status == .idle, !isCleanupDraining else { return }
        cleanupRuntimeReleaseInProgress = true
        defer { finishCleanupRuntimeRelease() }
        if let inFlight = cleanupPreparation,
           !cleanupConfigurationMatches(inFlight.key, cleanupPreparationKey) {
            inFlight.task.cancel()
            _ = await inFlight.task.value
            if cleanupPreparation?.id == inFlight.id {
                cleanupPreparation = nil
                isPreparingCleanupEngine = false
            }
        }

        let key = cleanupPreparationKey
        guard key.idle else { return }
        let s1IsInactive = key.mode != .s1Mini || key.engine == .assemblyAI
        if s1IsInactive {
            await S1MiniPostProcessingService.unload()
            if preparedCleanupKey?.mode == .s1Mini { preparedCleanupKey = nil }
        }
        let previous = previouslyPreparedKey ?? preparedCleanupKey
        guard let previous else {
            if (key.mode != .appleIntelligence || key.engine == .assemblyAI), #available(macOS 26, *) {
                await AIPostProcessingService.discardPreparedSession()
            }
            return
        }

        let previousUsedVocabulary = previous.mode == .fluidAudioVocabulary
            && !previous.vocabulary.isEmpty
        let vocabularyIsNoLongerNeeded = key.mode != .fluidAudioVocabulary
            || key.engine != .parakeet || !key.model.supportsFluidAudioVocabulary
            || key.vocabulary.isEmpty
        if previousUsedVocabulary && vocabularyIsNoLongerNeeded {
            await parakeetEngine.releaseVocabularyModels()
        }
        if (previous.mode == .s1Mini && s1IsInactive)
            || (previousUsedVocabulary && vocabularyIsNoLongerNeeded)
            || key.engine == .assemblyAI || previous.mode != key.mode {
            preparedCleanupKey = nil
        }
        if (key.mode != .appleIntelligence || key.engine == .assemblyAI), #available(macOS 26, *) {
            await AIPostProcessingService.discardPreparedSession()
        }
    }

    /// Preparation and release share process-wide model services. Keep a
    /// newer selection from warming a runtime while an older selection is
    /// still unloading it across an actor suspension.
    private func waitForCleanupRuntimeRelease() async {
        while cleanupRuntimeReleaseInProgress {
            await withCheckedContinuation { cleanupRuntimeReleaseWaiters.append($0) }
        }
    }

    private func finishCleanupRuntimeRelease() {
        cleanupRuntimeReleaseInProgress = false
        let waiters = cleanupRuntimeReleaseWaiters
        cleanupRuntimeReleaseWaiters.removeAll(keepingCapacity: true)
        waiters.forEach { $0.resume() }
    }

    private func handleAccessibilityPermissionChanged(_ granted: Bool) {
        rearmResolvedBlockingAttentionIssues()
        updateAccessibilityIntegration(granted: granted, promptIfNeeded: false)
    }

    private func updateAccessibilityIntegration(granted: Bool, promptIfNeeded: Bool) {
        guard !isShuttingDown else { return }
        if granted {
            permissions.stopPolling()
            if (settings.hasHotkey || settings.cancelShortcut.hasBinding) && !hotkeyService.isMonitoring {
                hotkeyService.startMonitoring()
            }
        } else {
            hotkeyService.stopMonitoring()
            if promptIfNeeded {
                permissions.promptForAccessibility()
            }
            permissions.startPolling()
        }
    }

    // MARK: - Engine Lifecycle

    /// Resolve configuration first, then overlap independent speech and cleanup
    /// loads. Vocabulary preparation needs the selected speech weights resident.
    func prepareSelectedEngines(prewarmModel: Bool = true, selectionOperationID requestedOperationID: UUID? = nil) async {
        let operationID = requestedOperationID ?? selectionOperationID
        guard !isShuttingDown, !Task.isCancelled, ownsSelectionOperation(operationID) else { return }
        // Configuration may select an available Apple Speech language. Resolve
        // that fallback before cleanup evaluates its language/model eligibility.
        await prepareActiveEngine(prewarmModel: false, resolvedConfiguration: nil, selectionOperationID: operationID)
        guard prewarmModel, !isShuttingDown, !Task.isCancelled,
              ownsSelectionOperation(operationID) else { return }
        let resolvedConfiguration = enginePreparationKey(prewarmModel: true)
        if settings.transcriptPostProcessingMode == .fluidAudioVocabulary {
            await prepareActiveEngine(
                prewarmModel: true,
                resolvedConfiguration: resolvedConfiguration,
                selectionOperationID: operationID
            )
            guard ownsSelectionOperation(operationID) else { return }
            if !Task.isCancelled { await prepareCleanupEngineIfNeeded() }
            return
        }
        async let speech: Void = prepareActiveEngine(
            prewarmModel: true,
            resolvedConfiguration: resolvedConfiguration,
            selectionOperationID: operationID
        )
        async let cleanup: Void = prepareCleanupEngineIfNeeded()
        _ = await (speech, cleanup)
    }

    /// Warm the next one-shot analyzer after this dictation completes. The
    /// engine coalesces this with a racing recording startup and uses its own
    /// generation guard so selection changes cannot install a stale session.
    private func prepareAppleSpeechForNextDictation() {
        guard !isShuttingDown, status == .idle, settings.engineChoice == .appleSpeech,
              AppleSpeechEngine.isSupported else { return }
        appleSpeechIdlePreparation?.cancel()
        let id = UUID()
        appleSpeechIdlePreparationID = id
        appleSpeechIdlePreparation = Task { [weak self] in
            guard let self, !Task.isCancelled, self.appleSpeechIdlePreparationID == id,
                  !self.isShuttingDown, self.status == .idle,
                  self.settings.engineChoice == .appleSpeech else { return }
            do { try await self.appleSpeechEngine.prepare() }
            catch { }
            if self.appleSpeechIdlePreparationID == id {
                self.appleSpeechIdlePreparationID = nil
                self.appleSpeechIdlePreparation = nil
            }
        }
    }

    func prepareActiveEngine(prewarmModel: Bool = true) async {
        let operationID = selectionOperationID
        await prepareActiveEngine(prewarmModel: prewarmModel, resolvedConfiguration: nil, selectionOperationID: operationID)
    }

    /// Called only by the explicit Speech Model download action, never hotkeys
    /// or automatic engine preparation. Freeze the language for this request.
    func installSelectedAppleSpeechAssets() async {
        guard !isShuttingDown, !isSystemSleeping, status == .idle,
              startingRequestID == nil, !isTransitioning, !isPreparingEngine,
              appleSpeechInstallationTask == nil, settings.engineChoice == .appleSpeech else { return }
        let language = settings.appleSpeechLanguage
        let operationID = beginSelectionOperation()
        isInstallingAppleSpeech = true
        enginePreparationError = nil
        let installer = appleSpeechAssetInstaller
        let task = Task { @MainActor [weak self] in
            guard let self else { return }
            do {
                if let installer { try await installer(language) }
                else { try await AppleSpeechEngine.installAssets(for: language) }
                try Task.checkCancellation()
                guard self.ownsSelectionOperation(operationID), self.settings.engineChoice == .appleSpeech,
                      self.settings.appleSpeechLanguage == language else { return }
                await self.refreshAppleSpeechAssetState()
                guard !Task.isCancelled, self.ownsSelectionOperation(operationID) else { return }
                await self.prepareActiveEngine()
            } catch is CancellationError {
                return
            } catch {
                guard self.ownsSelectionOperation(operationID), !Task.isCancelled else { return }
                self.enginePreparationError = "Apple Speech could not be downloaded. Check the connection and try again."
            }
        }
        appleSpeechInstallationTask = task
        await task.value
        appleSpeechInstallationTask = nil
        isInstallingAppleSpeech = false
    }

    private func prepareActiveEngine(
        prewarmModel: Bool,
        resolvedConfiguration: EnginePreparationKey?,
        selectionOperationID: UUID
    ) async {
        defer { rearmResolvedBlockingAttentionIssues() }
        guard ownsSelectionOperation(selectionOperationID) else { return }
        while let inFlight = enginePreparation {
            await inFlight.task.value
            guard ownsSelectionOperation(selectionOperationID) else { return }
            if enginePreparation?.id == inFlight.id {
                enginePreparation = nil
            }
            if inFlight.key == enginePreparationKey(prewarmModel: prewarmModel),
               inFlight.selectionOperationID == selectionOperationID {
                return
            }
        }
        guard !isShuttingDown, ownsSelectionOperation(selectionOperationID) else { return }
        let id = UUID()
        let task = Task { [weak self] in
            guard let self else { return }
            await self.performPrepareActiveEngine(
                prewarmModel: prewarmModel,
                resolvedConfiguration: resolvedConfiguration,
                selectionOperationID: selectionOperationID,
                preparationID: id
            )
        }
        enginePreparation = EnginePreparation(
            id: id, key: enginePreparationKey(prewarmModel: prewarmModel),
            selectionOperationID: selectionOperationID, task: task
        )
        await task.value
        if enginePreparation?.id == id {
            enginePreparation = nil
        }
    }

    private func enginePreparationKey(prewarmModel: Bool) -> EnginePreparationKey {
        EnginePreparationKey(
            engine: settings.engineChoice,
            model: settings.parakeetModelChoice,
            language: settings.selectedLanguage,
            appleSpeechLanguage: settings.appleSpeechLanguage,
            prewarmModel: prewarmModel
        )
    }

    private func performPrepareActiveEngine(
        prewarmModel: Bool,
        resolvedConfiguration: EnginePreparationKey?,
        selectionOperationID: UUID,
        preparationID: UUID
    ) async {
        let trace = PerfTrace.begin("stt.prepare")
        defer { trace.end() }
        defer {
            if enginePreparation?.id == preparationID { isPreparingEngine = false }
        }
        guard !isShuttingDown, ownsSelectionOperation(selectionOperationID) else { return }
        logger.info("prepareActiveEngine: called, engineChoice=\(String(describing: self.settings.engineChoice), privacy: .public), status=\(self.status.diagnosticLabel, privacy: .public)")
        if case .recording = status { return }
        if case .processing = status { return }
        if case .error = status { status = .idle }

        if resolvedConfiguration != enginePreparationKey(prewarmModel: prewarmModel) {
            await resolveActiveEngineConfiguration()
        }
        guard !isShuttingDown, ownsSelectionOperation(selectionOperationID) else { return }

        let targetEngine = activeEngine
        let ready = targetEngine.isReady
        logger.info("prepareActiveEngine: activeEngine.isReady=\(ready, privacy: .public), willCallPrepare=\(!ready && prewarmModel, privacy: .public)")
        if !ready, prewarmModel {
            // Set synchronously so the UI sees it before any await yields
            isPreparingEngine = true
            enginePreparationError = nil
            do {
                try await targetEngine.prepare()
                guard ownsSelectionOperation(selectionOperationID) else { return }
                guard !isShuttingDown else {
                    await targetEngine.cancel()
                    return
                }
            } catch {
                guard ownsSelectionOperation(selectionOperationID) else { return }
                logger.error("prepareActiveEngine: prepare() failed on first attempt: \(RemyDiagnostics.errorCategory(error), privacy: .public)")
                try? await Task.sleep(for: .seconds(1))
                guard !isShuttingDown, ownsSelectionOperation(selectionOperationID) else { return }
                do {
                    try await targetEngine.prepare()
                    guard ownsSelectionOperation(selectionOperationID) else { return }
                    guard !isShuttingDown else {
                        await targetEngine.cancel()
                        return
                    }
                } catch {
                    guard ownsSelectionOperation(selectionOperationID) else { return }
                    logger.error("prepareActiveEngine: prepare() failed on retry: \(RemyDiagnostics.errorCategory(error), privacy: .public)")
                    enginePreparationError = error.localizedDescription
                }
            }
            logger.info("prepareActiveEngine: prepare() completed, isReady=\(targetEngine.isReady, privacy: .public)")
        }
        // Recheck OS-managed availability without granting installation authority.
        if settings.engineChoice == .appleSpeech, !ready, prewarmModel,
           ownsSelectionOperation(selectionOperationID) {
            appleSpeechInstalledLanguages = await AppleSpeechEngine.installedLanguages()
            guard !isShuttingDown, ownsSelectionOperation(selectionOperationID) else { return }
        }
    }

    private func resolveActiveEngineConfiguration() async {
        switch settings.engineChoice {
        case .parakeet:
            // Auto-default: if the user hasn't explicitly chosen an engine and
            // a speech model is downloaded, ensure FluidAudio is selected.
            await parakeetEngine.recheckAllModelsOnDisk()
            guard !isShuttingDown else { return }
            await parakeetEngine.handleSelectedModelChange()
            guard !isShuttingDown else { return }
            let hasSpeechModel = parakeetEngine.checkAnyModelOnDisk()
            if !settings.userHasChosenEngine, hasSpeechModel {
                settings.engineChoice = .parakeet
            }
            if hasSpeechModel {
                settings.legacyAppleSpeechMigrationPending = false
            }
        case .appleSpeech:
            if !appleSpeechAssetsRefreshedDuringStartup {
                await refreshAppleSpeechAssetState()
            }
            guard !isShuttingDown else { return }
            if !appleSpeechSupportedLanguages.contains(settings.appleSpeechLanguage),
               let fallback = appleSpeechSupportedLanguages.first {
                settings.appleSpeechLanguage = fallback
            }
            settings.legacyAppleSpeechMigrationPending = false
        case .assemblyAI:
            settings.legacyAppleSpeechMigrationPending = false
        }

    }

    func handleParakeetModelSelectionChange(
        userInitiated: Bool,
        prewarmModel: Bool = true,
        selectionOperationID requestedOperationID: UUID? = nil
    ) async {
        let trace = PerfTrace.begin("stt.modelSwitch")
        defer { trace.end() }
        guard status == .idle else { return }
        let operationID = requestedOperationID ?? beginSelectionOperation()
        guard ownsSelectionOperation(operationID) else { return }
        appleSpeechUnsupportedSelection = false
        settings.engineChoice = .parakeet
        settings.userHasChosenEngine = userInitiated
        await parakeetEngine.handleSelectedModelChange()
        guard ownsSelectionOperation(operationID), settings.engineChoice == .parakeet else { return }
        await prepareSelectedEngines(prewarmModel: prewarmModel, selectionOperationID: operationID)
    }

    func handleEngineSelectionChange(
        _ choice: TranscriptionEngineChoice,
        prewarmModel: Bool = true,
        selectionOperationID requestedOperationID: UUID? = nil
    ) async {
        let trace = PerfTrace.begin("stt.modelSwitch")
        defer { trace.end() }
        guard status == .idle else { return }
        guard availableEngineChoices.contains(choice) else { return }
        guard choice != .appleSpeech || AppleSpeechEngine.isSupported else { return }

        let operationID = requestedOperationID ?? beginSelectionOperation()
        guard ownsSelectionOperation(operationID) else { return }
        appleSpeechUnsupportedSelection = false

        let previousChoice = settings.engineChoice
        let previousCleanupKey = preparedCleanupKey ?? cleanupPreparation?.key
        enginePreparationError = nil
        settings.engineChoice = choice
        settings.userHasChosenEngine = true
        if !selectedPage.isVisible(for: choice) {
            selectedPage = .models
        }
        if choice != .appleSpeech {
            appleSpeechIdlePreparation?.cancel()
            appleSpeechIdlePreparation = nil
            appleSpeechIdlePreparationID = nil
            await appleSpeechEngine.invalidatePreparedSession()
            guard ownsSelectionOperation(operationID), settings.engineChoice == choice else { return }
        }
        if previousChoice == .parakeet, choice != .parakeet {
            await parakeetEngine.unloadDeselectedModel()
            guard ownsSelectionOperation(operationID), settings.engineChoice == choice else { return }
        }
        await releaseDeselectedCleanupRuntime(previouslyPreparedKey: previousCleanupKey)
        guard ownsSelectionOperation(operationID), settings.engineChoice == choice else { return }
        await prepareSelectedEngines(prewarmModel: prewarmModel, selectionOperationID: operationID)
    }

    func handleAppleSpeechLanguageChange(
        _ language: SupportedLanguage,
        prewarmModel: Bool = true,
        selectionOperationID requestedOperationID: UUID? = nil
    ) async {
        let trace = PerfTrace.begin("stt.modelSwitch")
        defer { trace.end() }
        guard status == .idle, settings.engineChoice == .appleSpeech else { return }
        guard appleSpeechSupportedLanguages.contains(language) else { return }
        let operationID = requestedOperationID ?? beginSelectionOperation()
        guard ownsSelectionOperation(operationID) else { return }
        settings.appleSpeechLanguage = language
        appleSpeechIdlePreparation?.cancel()
        appleSpeechIdlePreparation = nil
        appleSpeechIdlePreparationID = nil
        await appleSpeechEngine.invalidatePreparedSession()
        guard ownsSelectionOperation(operationID), settings.engineChoice == .appleSpeech,
              settings.appleSpeechLanguage == language else { return }
        await prepareSelectedEngines(prewarmModel: prewarmModel, selectionOperationID: operationID)
    }

    /// Refreshes both the supportable and the installed Apple Speech language
    /// sets. Installed state drives the input-source mapping UI, so it must be
    /// fresh even while FluidAudio is the active engine.
    func refreshAppleSpeechAssetState() async {
        if let appleSpeechAssetSnapshot {
            let snapshot = await appleSpeechAssetSnapshot()
            appleSpeechSupportedLanguages = snapshot.supported
            appleSpeechInstalledLanguages = snapshot.installed
            return
        }
        appleSpeechSupportedLanguages = await AppleSpeechEngine.supportedLanguages()
        appleSpeechInstalledLanguages = await AppleSpeechEngine.installedLanguages()
    }

    // MARK: - Input Source Auto-Switch

    /// Serializes calls to `applyInputSourceProfile` so overlapping input
    /// source changes apply in order. Internal (not private) so callers
    /// outside AppState — e.g. the startup sequence and settings UI — also
    /// go through the queue instead of calling `applyInputSourceProfile`
    /// directly.
    @discardableResult
    func enqueueInputSourceProfileApply(
        for inputSourceID: String,
        showLoadingOverlay: Bool = false,
        prewarmModel: Bool = true
    ) -> Task<Void, Never> {
        let previous = inputSourceApplyTask
        let task = Task { [weak self] in
            await previous?.value
            await self?.applyInputSourceProfile(
                for: inputSourceID,
                showLoadingOverlay: showLoadingOverlay,
                prewarmModel: prewarmModel
            )
        }
        inputSourceApplyTask = task
        return task
    }

    func applyInputSourceProfile(
        for inputSourceID: String,
        showLoadingOverlay: Bool = false,
        prewarmModel: Bool = true
    ) async {
        guard !isShuttingDown else { return }
        guard settings.engineChoice != .assemblyAI else { return }
        let priorSelectionOperationID = selectionOperationID
        // Looked up (and, for Apple Speech, awaited) before the idle guard so
        // no suspension point lands between the guard and the settings
        // writes below — an in-flight recording-start guard check must never
        // race a suspended apply.
        let mapping = settings.mapping(forInputSourceID: inputSourceID)
        let installedAppleSpeechLanguages = mapping?.engine == .appleSpeech
            ? await AppleSpeechEngine.installedLanguages()
            : []
        guard !isShuttingDown, ownsSelectionOperation(priorSelectionOperationID) else { return }
        guard status == .idle else { return }
        if mapping?.engine == .appleSpeech {
            appleSpeechInstalledLanguages = installedAppleSpeechLanguages
        }
        let resolution = InputSourceProfileResolver.resolve(
            mapping: mapping,
            enabled: settings.inputSourceAutoSwitchEnabled,
            currentEngine: settings.engineChoice,
            currentParakeetModel: settings.parakeetModelChoice,
            currentFluidAudioLanguage: settings.selectedLanguage,
            currentAppleSpeechLanguage: settings.appleSpeechLanguage,
            appleSpeechSupported: AppleSpeechEngine.isSupported,
            // Availability = on disk AND runnable by this process (FluidAudio
            // hard-fails Nemotron multilingual under Rosetta/x86_64).
            isModelDownloaded: { [self] in
                profileModelAvailableOverride?($0)
                    ?? (parakeetEngine.checkModelOnDisk(for: $0) && $0.isAvailableOnThisMac)
            },
            isAppleSpeechAssetInstalled: { installedAppleSpeechLanguages.contains($0) }
        )

        var targetEngine = "n/a"
        var targetModel = "n/a"
        if case .fullApply = resolution, let mapping {
            targetEngine = mapping.engine.rawValue
            targetModel = mapping.parakeetModel?.rawValue ?? "n/a"
        }
        logger.info(
            "applyInputSourceProfile: targetEngine=\(targetEngine, privacy: .public), targetModel=\(targetModel, privacy: .public)"
        )

        switch resolution {
        case .none, .inactive:
            return

        case .noChange:
            // A source mapped to the already-active vocab-capable model must
            // still trigger the restore (e.g. a prior switch away stripped
            // `.fluidAudioVocabulary` and this source maps back to it).
            settings.restoreVocabularyModeAfterAutoSwitchIfPending()
            if prewarmModel, ownsSelectionOperation(priorSelectionOperationID) {
                await prepareCleanupEngineIfNeeded()
            }
            return

        case .languageOnly(let language):
            guard let mapping else { return }
            let operationID = beginSelectionOperation()
            switch mapping.engine {
            case .parakeet:
                // Read at recording start; no engine reload needed.
                settings.selectedLanguage = language
            case .appleSpeech:
                if showLoadingOverlay { overlay.show(state: .preparingModel(name: "Apple Speech")) }
                await handleAppleSpeechLanguageChange(
                    language,
                    prewarmModel: prewarmModel,
                    selectionOperationID: operationID
                )
                if showLoadingOverlay { overlay.hide(afterDelay: 0) }
            case .assemblyAI:
                return
            }
            guard ownsSelectionOperation(operationID) else { return }
            settings.restoreVocabularyModeAfterAutoSwitchIfPending()

            if prewarmModel { await prepareCleanupEngineIfNeeded() }

        case .fullApply:
            guard let mapping else { return }
            let operationID = beginSelectionOperation()
            let profileName = mapping.engine == .parakeet
                ? (mapping.parakeetModel?.displayName ?? "model")
                : "Apple Speech"
            if showLoadingOverlay { overlay.show(state: .preparingModel(name: profileName)) }
            switch mapping.engine {
            case .parakeet:
                guard let model = mapping.parakeetModel else { break }
                let hadVocabularyMode = settings.transcriptPostProcessingMode == .fluidAudioVocabulary
                // Model before language: the model didSet coerces unsupported
                // languages back to English.
                settings.parakeetModelChoice = model
                settings.selectedLanguage = mapping.language
                await handleParakeetModelSelectionChange(
                    userInitiated: true,
                    prewarmModel: prewarmModel,
                    selectionOperationID: operationID
                )
                guard ownsSelectionOperation(operationID), settings.engineChoice == .parakeet else { return }
                settings.noteAutoSwitchModelChange(hadVocabularyMode: hadVocabularyMode)
                settings.restoreVocabularyModeAfterAutoSwitchIfPending()
            case .appleSpeech:
                // Pin the language before the engine switch so the one and only
                // prepare targets the gated, installed language (a stale
                // appleSpeechLanguage would otherwise download the wrong
                // assets). The explicit invalidate matters: a session prepared
                // earlier for a different language would satisfy the isReady
                // check in prepareActiveEngine and skip preparation entirely.
                // handleEngineSelectionChange only invalidates when switching
                // AWAY from Apple Speech, so it won't double-invalidate here,
                // and no second language-change call is needed — that was the
                // duplicate-preparation path.
                settings.appleSpeechLanguage = mapping.language
                await appleSpeechEngine.invalidatePreparedSession()
                guard ownsSelectionOperation(operationID) else { return }
                await handleEngineSelectionChange(
                    .appleSpeech,
                    prewarmModel: prewarmModel,
                    selectionOperationID: operationID
                )
            case .assemblyAI:
                break
            }
            if showLoadingOverlay { overlay.hide(afterDelay: 0) }
        }
    }

    // MARK: - Ollama Model Management

    func deleteOllamaModel(_ model: String) async {
        guard ollamaDeletingModel == nil else { return }

        let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedModel.isEmpty else { return }

        ollamaDeletingModel = trimmedModel
        ollamaModelActionError = nil

        do {
            try await OllamaPostProcessingService.removeModel(
                baseURL: settings.ollamaBaseURL,
                model: trimmedModel
            )
            ollamaDeletingModel = nil
            ollamaModelActionError = nil
            ollamaModelActionsRevision += 1
        } catch {
            guard !Task.isCancelled else { return }
            ollamaDeletingModel = nil
            ollamaModelActionError = error.localizedDescription
        }
    }

    // MARK: - Dictation Flow

    private func beginPerformanceSession(mode: HotkeyMode?, sessionID: UUID) {
        PerfTrace.setSessionMetadata(
            performanceConfigurationLabels().merging([
                "session_id": sessionID.uuidString,
                "audio_tap_buffer_frames": "4096",
                "hotkey_mode": mode?.rawValue ?? "none"
            ]) { _, session in session }
        )
    }

    private func performanceConfigurationLabels() -> [String: String] {
        let engine = settings.engineChoice
        let model: String
        let language: String
        switch engine {
        case .parakeet:
            model = settings.parakeetModelChoice.rawValue
            language = settings.selectedLanguage.rawValue
        case .appleSpeech:
            model = settings.appleSpeechLanguage.rawValue
            language = settings.appleSpeechLanguage.rawValue
        case .assemblyAI:
            model = "assemblyAI"
            language = settings.assemblyAILanguage.rawValue
        }

        return [
            "engine": engine.rawValue,
            "model": model,
            "language": language,
            "eou_enabled": String(engine == .parakeet
                && settings.parakeetModelChoice.supportsEndOfUtterance
                && settings.autoStopAfterSpeechEndsEnabled),
            "cleanup_mode": settings.transcriptPostProcessingMode.rawValue,
            "s1_mini_enabled": String(settings.transcriptPostProcessingMode == .s1Mini),
            "filler_removal_enabled": String(settings.isFillerWordRemovalEnabled
                && !settings.fillerWordsToRemove.isEmpty),
            "context_awareness_enabled": String(RemyFeaturePolicy.allowsDictationContextGathering
                && settings.dictationContextAwarenessEnabled),
            "audio_mute_enabled": String(settings.muteSystemAudioDuringRecordingEnabled),
            "microphone_boost_enabled": String(settings.boostMicrophoneVolumeEnabled),
            "input_auto_switch_enabled": String(settings.inputSourceAutoSwitchEnabled),
            "custom_vocabulary_enabled": String(!settings.customVocabulary.isEmpty)
        ]
    }

    private func updatePerformanceConfigurationLabels() {
        PerfTrace.updateSessionMetadata(performanceConfigurationLabels())
    }

    private func updatePerformanceContextLabels() {
        let context = sessionDictationContext
        PerfTrace.updateSessionMetadata([
            "context_category": context?.category.rawValue ?? "none",
            "context_excluded": String(context?.isContextExcluded ?? false),
            "secure_field": String(context?.isSecureField ?? false),
            "text_position_snapshot": String(context?.hasTextPositionSnapshot ?? false)
        ])
    }

    func startDictation(mode: HotkeyMode? = nil) async {
        guard !isShuttingDown, !isSystemSleeping, !isInstallingAppleSpeech, !isPreparingHistory, !isDeletingStoredHistory else { return }
        guard !isCleanupDraining else { overlay.showBusy(); return }
        guard !transformRunner.isBusy else { overlay.showBusy(TransformNotice.alreadyRunning.title); return }
        recordingStartError = nil
        logger.info("startDictation: entry, status=\(self.status.diagnosticLabel, privacy: .public), isTransitioning=\(self.isTransitioning, privacy: .public), engineChoice=\(String(describing: self.settings.engineChoice), privacy: .public)")
        if case .error = status {
            status = .idle
        }
        guard status == .idle, !isTransitioning, startingRequestID == nil else {
            if status != .recording, !isCancelling { overlay.showBusy() }
            return
        }
        let requestID = UUID()
        let destinationPolicy = settings.dictationDestination
        var requestCompleted = false
        startingRequestID = requestID
        isDeliveringTranscript = false
        deliveryAuthorization = nil
        updateCancellationAvailability()
        defer {
            if startingRequestID == requestID {
                startingRequestID = nil
                if !requestCompleted, status == .idle {
                    insertionTargetApp = nil
                    overlay.hide(afterDelay: 0)
                }
            }
            updateCancellationAvailability()
        }
        // Capture field/window identity before permissions, preparation or an
        // overlay can change focus. This remains the original-policy target.
        let target = AppDelegate.isRunningTests ? nil : NSWorkspace.shared.frontmostApplication
        let destination = target?.processIdentifier == ProcessInfo.processInfo.processIdentifier ? nil : target
        let targetSnapshot = await captureInsertionTarget(destination)
        guard !Task.isCancelled, !isShuttingDown, !isSystemSleeping,
              startingRequestID == requestID, status == .idle, !isTransitioning else { return }
        if mode == .holdToRecord && !isHoldToRecordKeyDown { return }
        if hasStarted {
            await permissions.refreshForDictation()
            guard !isShuttingDown, startingRequestID == requestID, status == .idle, !isTransitioning else { return }
            if mode == .holdToRecord && !isHoldToRecordKeyDown { return }
        }
        rearmResolvedBlockingAttentionIssues()
        if !permissions.micGranted {
            let granted: Bool
            if let microphonePermissionRequester {
                granted = await microphonePermissionRequester()
            } else {
                granted = await permissions.requestMic()
            }

            guard startingRequestID == requestID, !isShuttingDown else { return }
            guard granted else {
                presentBlockingAttentionIssueOnce(.microphone)
                return
            }

            permissions.micGranted = true
            rearmResolvedBlockingAttentionIssues()
            return // The permission gesture must never become a recording gesture.
        }
        guard !isShuttingDown, startingRequestID == requestID else { return }
        beginPerformanceSession(mode: mode, sessionID: requestID)
        let requestTrace = PerfTrace.begin("dictation.requestToRecording")
        defer {
            if !requestCompleted {
                requestTrace.end(outcome: "aborted")
                PerfTrace.clearSessionMetadata(ifSessionID: requestID)
            }
        }
        if settings.engineChoice != .assemblyAI,
           settings.inputSourceAutoSwitchEnabled,
           let inputSourceID = inputSourceIDOverride?() ?? inputSourceMonitor.currentInputSourceID() {
            // Backstop: the eager pre-warm usually already did this; going
            // through the queue serializes against an apply still in flight.
            await PerfTrace.measure("dictation.inputSourceApply") {
                await enqueueInputSourceProfileApply(for: inputSourceID, showLoadingOverlay: true).value
            }
            guard startingRequestID == requestID, !isShuttingDown else { return }
            updatePerformanceConfigurationLabels()
            requestTrace.refreshSessionMetadata()
        }
        guard !isShuttingDown, startingRequestID == requestID else { return }
        let engine = activeEngine
        let expectedRecognition = RecognitionSessionConfiguration.capture(settings: settings)
        switch settings.engineChoice {
        case .parakeet:
            if parakeetEngine.isDownloading {
                logger.warning("startDictation: selected model is still downloading")
                status = .error("\(settings.parakeetModelChoice.displayName) is still downloading. Try again when it finishes.")
                status = .idle
                return
            }

            var modelReady = true
            if engineOverride == nil {
                modelReady = await parakeetEngine.refreshSelectedModelReadiness()
                guard startingRequestID == requestID, !isShuttingDown else { return }
                if !modelReady {
                    await prepareActiveEngine()
                    guard startingRequestID == requestID, !isShuttingDown else { return }
                    modelReady = await parakeetEngine.refreshSelectedModelReadiness()
                }
            }

            guard modelReady, engine.isReady else {
                logger.warning("startDictation: FluidAudio engine not ready, aborting")
                status = .error("\(settings.parakeetModelChoice.displayName) is not ready. Download it from Speech Model settings.")
                status = .idle
                presentBlockingAttentionIssueOnce(.speechSetup)
                return
            }
        case .appleSpeech:
            if !engine.isReady {
                await prepareActiveEngine()
                guard startingRequestID == requestID, !isShuttingDown else { return }
            }
            guard engine.isReady else {
                logger.warning("startDictation: Apple Speech engine not ready, aborting")
                status = .error("Apple Speech is not ready. Open Speech Model settings to finish setup.")
                status = .idle
                presentBlockingAttentionIssueOnce(.speechSetup)
                return
            }
        case .assemblyAI:
            guard engine.isReady else {
                logger.warning("startDictation: AssemblyAI API key is missing")
                status = .error("Add an AssemblyAI API key in Speech Model settings before dictating.")
                status = .idle
                presentBlockingAttentionIssueOnce(.speechSetup)
                return
            }
        }
        guard !isShuttingDown, startingRequestID == requestID else { return }
        // Model/engine edits while preparation was suspended apply to a new
        // request; never start an old engine with a newly selected snapshot.
        guard engine === activeEngine,
              expectedRecognition == RecognitionSessionConfiguration.capture(settings: settings) else { return }
        await beginRecording(engine: engine, mode: mode, sessionID: requestID, destination: destinationPolicy,
            contextTarget: destination, contextSnapshot: targetSnapshot, requestTrace: requestTrace) {
            requestCompleted = true
        }
    }

    private func beginRecording(
        engine: TranscriptionEngine,
        mode: HotkeyMode?,
        sessionID: UUID,
        destination: RemyDictationDestination? = nil,
        contextTarget: NSRunningApplication?,
        contextSnapshot: RemyInsertionTarget,
        requestTrace: PerfInterval? = nil,
        onCompleted: () -> Void = {}
    ) async {
        let trace = PerfTrace.begin("dictation.start")
        var completed = false
        defer {
            if !completed {
                trace.end(outcome: "aborted")
                PerfTrace.clearSessionMetadata(ifSessionID: sessionID)
            }
        }
        guard !isShuttingDown, !isSystemSleeping else { return }
        let modelManager = s1MiniModelManager
        let captured = RemyCleanupRuntime.capture(settings: settings, destination: destination, s1ModelURL: modelManager.modelURL,
            validateS1Model: { try await modelManager.validatedModelURL() },
            profile: cleanupProfiles?.selectedProfile, profileCredentials: cleanupProfileCredentials)
        guard let token = sessions.begin(configuration: captured.configuration, id: sessionID) else { return }
        sessionToken = token
        sessionCleanupRuntime = captured.runtime
        // Launch bounded context gathering only after its collection/sharing
        // settings and app rules have been frozen for this recording.
        captureInsertionTargetAppAndContext(engine: engine, target: contextTarget, snapshot: contextSnapshot,
            configuration: captured.configuration, token: token)
        updatePerformanceContextLabels()
        sessionHistoryContext = nil
        deliveryAuthorization = nil
        engine.setSessionConfiguration(RecognitionSessionConfiguration.capture(settings: settings))
        PerfTrace.updateSessionMetadata(["session_id": token.id.uuidString])
        requestTrace?.refreshSessionMetadata()
        defer {
            if !completed, sessions.isCurrent(token) {
                sessions.fail(token, failure: .init(stage: .capture, category: .unavailable, retryable: true))
            }
            if !completed, sessionToken == token {
                invalidateContextCapture()
                sessionDictationContext = nil
                engine.setSessionConfiguration(nil)
                sessionCleanupRuntime = nil
            }
        }
        if let history {
            let request = startingRequestID
            let context = await history.beginSession(id: sessionID, createdAt: Date(),
                enabled: captured.configuration.capture.historyEnabled,
                configuration: Self.historyConfiguration(captured.configuration))
            guard !Task.isCancelled, sessions.isCurrent(token), !isShuttingDown,
                  startingRequestID == request else { return }
            sessionHistoryContext = context
        }
        engine.setSessionContextualVocabulary(
            ScreenNameTerms.merged(sessionDictationContext?.lexicalHints ?? [], sessionScreenNames))
        engine.setSessionDictationContext(sessionDictationContext)

        isTransitioning = true
        pendingHoldRelease = false
        recordingInputDeviceID = nil
        let recordingStartupID = UUID()
        activeRecordingStartupID = recordingStartupID
        sessionEngine = engine
        sessionHotkeyMode = mode
        configureEndOfUtteranceHandler(for: engine)

        completedRecognitionTranscript = nil
        isDeliveringTranscript = false
        recordingSafetyFailure = nil
        prepareSessionRecovery(for: engine)
        if recoveryCapture == nil && captured.configuration.capture.historyEnabled {
            invalidateContextCapture()
            await discardSessionRecovery()
            clearEndOfUtteranceHandler(for: engine)
            engine.setSessionContextualVocabulary([])
            engine.setSessionDictationContext(nil)
            sessionEngine = nil
            sessionHotkeyMode = nil
            activeRecordingStartupID = nil
            insertionTargetApp = nil
            sessionDictationContext = nil
            isTransitioning = false
            status = .idle
            return
        }

        status = .recording
        currentTranscript = transcriptPrefix

        // Snapshot routing for every recording; a saved UID is a preference, not
        // proof that a device can still capture (notably in clamshell mode).
        audioDeviceManager.refreshDevices()
        let startCandidates = MicrophoneHelper.startCandidates()
        let preferredDeviceID = startCandidates.first

        if captured.configuration.capture.boostMicrophoneVolume, let preferredDeviceID {
            volumeController.boostMicrophoneVolume(deviceID: preferredDeviceID)
        }
        if captured.configuration.capture.muteSystemAudio {
            volumeController.adjustForRecording()
        }

        var lastStartError: Error? = startCandidates.isEmpty ? TranscriptionError.audioEngineSetupFailed : nil
        var didStart = false
        for (index, candidateID) in startCandidates.enumerated() {
            guard !isShuttingDown, activeRecordingStartupID == recordingStartupID else { return }
            if index > 0 {
                logger.warning(
                    "startDictation: retrying startRecording attempt \(index + 1, privacy: .public) with deviceID=\(String(candidateID), privacy: .public)"
                )
                try? await Task.sleep(for: .milliseconds(220))
                guard !isShuttingDown, activeRecordingStartupID == recordingStartupID else { return }
            }

            do {
                let startTask = Task {
                    try Task.checkCancellation()
                    try await engine.startRecording(deviceID: candidateID)
                }
                recordingStartTask = startTask
                try await startTask.value
                recordingStartTask = nil
                guard !isShuttingDown, activeRecordingStartupID == recordingStartupID else { return }
                recordingInputDeviceID = candidateID
                didStart = true
                logger.info(
                    "startDictation: startRecording succeeded on attempt \(index + 1, privacy: .public), deviceID=\(String(candidateID), privacy: .public)"
                )
                break
            } catch {
                recordingStartTask = nil
                guard !isShuttingDown, activeRecordingStartupID == recordingStartupID else { return }
                lastStartError = error
                logger.error(
                    "startDictation: startRecording attempt \(index + 1, privacy: .public) failed: \(RemyDiagnostics.errorCategory(error), privacy: .public)"
                )
            }
        }

        guard !isShuttingDown else { return }
        guard didStart else {
            await permissions.refreshForDictation()
            guard !isShuttingDown, activeRecordingStartupID == recordingStartupID else { return }
            if !permissions.micGranted {
                presentBlockingAttentionIssueOnce(.microphone)
            }
            invalidateContextCapture()
            let wasContinuing = continuingEntryID != nil
            await discardSessionRecovery()
            let message = lastStartError?.localizedDescription ?? "Unknown audio startup error"
            if wasContinuing {
                currentTranscript = ""
                recoveryStore.errorMessage = "Could not start the microphone. Your saved session is still available. \(message)"
            }
            status = .error("Failed to start recording: \(message)")
            recordingStartError = permissions.micGranted && !wasContinuing
                ? "Could not start the microphone. \(message)" : nil
            selectedAttentionIssueID = !permissions.micGranted ? .microphone
                : (wasContinuing ? .recovery : .recordingFailed)
            NotificationCenter.default.post(name: .requestShowMainWindow, object: nil)
            overlay.show(state: .processing)
            overlay.hide(afterDelay: 2.0)
            insertionTargetApp = nil
            sessionDictationContext = nil
            engine.setSessionContextualVocabulary([])
            engine.setSessionDictationContext(nil)
            volumeController.restoreMicrophoneVolume()
            if captured.configuration.capture.muteSystemAudio {
                volumeController.restoreAfterRecording()
            }
            isTransitioning = false
            pendingHoldRelease = false
            activeRecordingStartupID = nil
            clearEndOfUtteranceHandler(for: engine)
            sessionEngine = nil
            sessionHotkeyMode = nil
            status = .idle
            return
        }

        // Show overlay only for this live session after mic is confirmed active.
        guard !isShuttingDown, sessions.markRecording(token) else { return }
        settings.playDictationSound(.start)
        overlay.showListening(level: 0, transcript: currentTranscript)

        // Begin preparation only after capture is active. It overlaps speech
        // without extending microphone startup or sending transcript text.
        prewarmCleanupForRecording()

        // Start audio level polling
        startAudioLevelPolling(engine: engine)

        activeRecordingStartupID = nil
        isTransitioning = false
        trace.end(outcome: "completed")
        requestTrace?.end(outcome: "completed")
        completed = true
        onCompleted()

        // If the user released a hold-to-record key while we were starting up, stop now.
        if pendingHoldRelease {
            pendingHoldRelease = false
            await stopDictation()
        }
    }

    private func prewarmCleanupForRecording() {
        guard !AppDelegate.isRunningTests || cleanupModelPreparation != nil else { return }
        let key = cleanupPreparationKey
        guard key.enabled, key.engine != .assemblyAI else { return }
        guard key.mode == .appleIntelligence || key.mode == .ollama
            || key.mode == .openRouter || key.mode == .openAICompatible else { return }
        recordingCleanupPreparation?.cancel()
        recordingCleanupPreparation = Task(priority: .utility) { [weak self] in
            guard let self else { return }
            await self.prepareCleanupEngineIfNeeded(force: false, allowRecording: true)
        }
    }

    func stopDictation() async {
        guard status == .recording, !isTransitioning,
              let token = sessionToken, sessions.requestStop(token) else { return }
        isTransitioning = true
        status = .processing
        hotkeyService.resetCancellationGesture()
        let operationID = UUID()
        processingOperationID = operationID
        let task = Task { @MainActor in await self.finishDictation(token: token) }
        processingTask = task
        await task.value
        guard processingOperationID == operationID else { return }
        processingTask = nil
        processingOperationID = nil
        if !isCancelling { isTransitioning = false }
    }

    private func finishDictation(token: SessionCoordinator.Token) async {
        guard let configuration = sessions.configuration(for: token),
              let cleanupRuntime = sessionCleanupRuntime else { return }
        let trace = PerfTrace.begin("dictation.stopToInsertion")
        defer {
            trace.end(outcome: Task.isCancelled ? "cancelled" : "aborted")
            PerfTrace.clearSessionMetadata()
        }
        stopAudioLevelPolling()

        // Show processing overlay
        overlay.show(state: .processing)

        let engine = sessionEngine ?? activeEngine

        // Local final decoding can overlap a cold editor's context capture.
        // Context-dependent engines retain their context-before-decode order.
        await engine.stopAudioCapture()
        volumeController.recordCaptureStopped()
        let contextTask = contextApplicationTask
        async let recognition = finalizeRecording(
            engine: engine,
            contextTask: engine.requiresContextBeforeFinalization ? contextTask : nil
        )
        await contextTask?.value
        // Keep the recording-time destination even if focus changes while
        // decoding. The runtime applies this session's frozen sharing policy.
        let cleanupContext = sessionDictationContext
        let newTranscript = await recognition
        // Polling has stopped, but the final audio callback/write can still
        // fail. Drain the safety file before deciding that this session
        // succeeded and its audio can be removed.
        if let capture = recoveryCapture, sessionToken == token {
            let result = await capture.finish()
            if let error = result.error { recordingSafetyFailure = error }
        }
        // Cancellation joins this decoder before the engine or recovery capture
        // can be reused. Retain its completed checkpoint only for that same
        // session's optional recovery; this grants no history/output authority.
        if sessionToken == token, !isShuttingDown, engine.lastTranscriptionError == nil {
            completedRecognitionTranscript = newTranscript.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                ? nil : CancelledDictation.joining(transcriptPrefix, newTranscript)
        }
        guard !Task.isCancelled, sessions.isCurrent(token) else { return }
        // Use whatever names the walk produced by now; never wait for it.
        let cleanupScreenNames = sessionScreenNames
        invalidateContextCapture()

        let usesAssemblyAI = engine === assemblyAIEngine
        let modelInsertionPlan = usesAssemblyAI && transcriptPrefix.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? assemblyAIEngine.lastInsertionPlan : nil
        let preserveModelFormatting = usesAssemblyAI && assemblyAIEngine.lastResultWasPolished
        if let error = engine.lastTranscriptionError ?? recordingSafetyFailure {
            trace.end(outcome: "failed")
            await handleTranscriptionFailure(error, engine: engine, token: token,
                completedTranscript: engine.lastTranscriptionError == nil ? completedRecognitionTranscript : nil)
            sessions.fail(token, failure: .init(stage: .recognition, category: .unavailable, retryable: true))
            return
        }
        let transcript = CancelledDictation.joining(transcriptPrefix, newTranscript)
        // A continued session's prefix can already be polished; do not label
        // that mixture (or just the newest chunk) as the full raw transcript.
        let rawTranscript = transcriptPrefix.isEmpty
            ? (usesAssemblyAI ? assemblyAIEngine.lastRawTranscript : newTranscript) : nil
        guard sessions.recordFinalRaw(token, text: newTranscript) else { return }
        guard !Task.isCancelled, sessions.isCurrent(token) else { return }
        engine.setSessionContextualVocabulary([])
        engine.setSessionDictationContext(nil)
        clearEndOfUtteranceHandler(for: engine)
        sessionHotkeyMode = nil

        // Engines own decoder fallback. A provisional preview can be longer
        // because it contains repetitions or words rejected by final decoding.
        let normalizeTrace = PerfTrace.begin("transcript.normalize")
        let finalText: String
        if usesAssemblyAI {
            finalText = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        } else {
            // Spelling corrections run once here, so cleanup, "Use Raw Now"
            // and every fallback start from the same locally corrected text.
            finalText = configuration.capture.textRules.applyingCorrections(
                to: configuration.capture.fillerRules.removingFillers(from: transcript)
            ).trimmingCharacters(in: .whitespacesAndNewlines)
        }
        normalizeTrace.end()

        guard !finalText.isEmpty else {
            trace.end(outcome: "noText")
            currentTranscript = ""
            volumeController.restoreMicrophoneVolume()
            // Restore recording audio state (brief pause lets BT audio routing settle)
            await volumeController.restoreAfterRecordingWithSettle()
            guard !Task.isCancelled, sessions.isCurrent(token) else { return }
            guard sessions.markReady(token) else { return }
            await discardSessionRecovery(completed: true)
            guard !isShuttingDown, sessions.complete(token) else { return }
            sessionEngine = nil
            engine.setSessionConfiguration(nil)
            sessionCleanupRuntime = nil
            overlay.show(state: .success)
            overlay.hide(afterDelay: 0.5)
            status = .idle
            insertionTargetApp = nil
            sessionDictationContext = nil
            prepareAppleSpeechForNextDictation()
            return
        }

        currentTranscript = finalText
        lastTranscript = finalText
        Self.lastTranscriptForMenuBar = finalText

        let historyContext = sessionHistoryContext
        if let history, let historyContext {
            let saved = await history.recordRaw(historyContext, rawChunk: newTranscript, continuationPrefix: transcriptPrefix)
            guard !Task.isCancelled, sessions.isCurrent(token) else { return }
            guard Self.permitsDelivery(saved) else {
                await stopForHistoryFailure(token: token, engine: engine)
                return
            }
        }

        // Only immutable session values reach cleanup. The runtime privately
        // owns the credential captured at start; the coordinator stores a reference.
        guard sessions.beginCleanup(token) else { return }
        var processedText = finalText
        var cleanupFailure: String?
        let postProcessingMode = usesAssemblyAI ? TranscriptPostProcessingMode.none : configuration.cleanup.mode
        // The whole-cleanup deadline includes any remaining cold preparation.
        // Its decision can finish before a noncooperative local worker drains.
        let preparation = postProcessingMode == .appleIntelligence ? recordingCleanupPreparation : nil
        if postProcessingMode == .appleIntelligence { preparedCleanupKey = nil }
        let operation = transcriptCleanupOverride
        let attempt = CleanupAttempt(token: token, correctedRaw: finalText, timeout: cleanupTimeout) {
            do {
                await preparation?.value
                try Task.checkCancellation()
                if let operation { return try await operation(finalText, configuration) }
                return try await cleanupRuntime.process(text: finalText, context: cleanupContext,
                    screenNames: cleanupScreenNames)
            } catch {
                // Normalize provider/runtime failures before the dependency-free
                // deadline primitive sees them; never retain private error details.
                throw RemyCleanupFailure.classify(error)
            }
        }
        cleanupAttempts[token] = attempt
        Task { @MainActor [weak self] in
            await attempt.joinWorker()
            guard let self, self.cleanupAttempts[token] === attempt else { return }
            self.cleanupAttempts.removeValue(forKey: token)
        }
        switch await attempt.result() {
        case .cleaned(let text): processedText = text
        case .fallback(let reason, let correctedRaw):
            processedText = correctedRaw
            switch reason {
            case .timeout: cleanupFailure = "timeout"
            case .useRaw: cleanupFailure = "useRaw"
            case .providerFailure: cleanupFailure = attempt.failureCategory?.rawValue ?? "providerFailure"
            }
        case .cancelled:
            // This is diagnostic bookkeeping only; cancellation never authorizes delivery.
            if sessionToken == token, !isShuttingDown, let history, let historyContext,
               history.isValid(historyContext), postProcessingMode != .none,
               postProcessingMode != .fluidAudioVocabulary {
                _ = await history.recordAttempt(historyContext, input: finalText, output: nil,
                    status: .cancelled, failureCategory: "cancelled", selectOutput: false,
                    startedAt: attempt.startedAt, durationMilliseconds: attempt.durationMilliseconds)
                _ = await history.interruptSession(historyContext)
            }
            return
        }
        guard !Task.isCancelled, sessions.isCurrent(token) else { return }
        if !attempt.isDraining, postProcessingMode == .s1Mini, let url = configuration.cleanup.s1ModelURL {
            let runtimeKey = cleanupPreparationKey
            recordS1MiniRuntimeReadiness(await S1MiniPostProcessingService.isPrepared(modelURL: url), for: runtimeKey)
            guard !Task.isCancelled, sessions.isCurrent(token) else { return }
        }
        let historyTrace = PerfTrace.begin("transcript.history")
        if cleanupFailure == nil, postProcessingMode != .none,
           postProcessingMode != .fluidAudioVocabulary {
            processedText = normalizePostProcessedTranscript(processedText)
            let guarded = TranscriptCleanupIntegrity.revertingUnsupportedTerms(source: finalText, output: processedText,
                terms: configuration.capture.vocabulary + cleanupScreenNames)
            if guarded.reverted > 0 {
                processedText = guarded.text
                PerfTrace.event("cleanup.unsupportedTermReverted", counts: ["regions": guarded.reverted])
            }
        }
        if let cleanupFailure {
            logger.warning("postProcessing: rawFallback reason=\(cleanupFailure, privacy: .public), elapsedMs=\(attempt.durationMilliseconds ?? 0, privacy: .public)")
        } else {
            logger.info("postProcessing: completed mode=\(postProcessingMode.rawValue, privacy: .public), changed=\(processedText != finalText, privacy: .public), elapsedMs=\(attempt.durationMilliseconds ?? 0, privacy: .public)")
        }
        if !usesAssemblyAI {
            // Snippets expand after cleanup (or its fallback), so expansions are
            // never sent to a provider. A dictation that would expand to nothing
            // is delivered unchanged rather than as an empty insertion.
            let expanded = configuration.capture.textRules.expandingSnippets(in: processedText)
            if !expanded.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { processedText = expanded }
        }

        guard !Task.isCancelled, sessions.isCurrent(token) else { historyTrace.end(); return }
        // History/UI readiness grants no output authority. Cancellation remains
        // available until the actual output worker claims its side effect.
        guard sessions.markReady(token) else { historyTrace.end(); return }
        updateCancellationAvailability()
        currentTranscript = processedText
        lastTranscript = processedText
        Self.lastTranscriptForMenuBar = processedText
        if let history, let historyContext {
            let saved: RemyHistoryAcknowledgement
            if postProcessingMode == .none || postProcessingMode == .fluidAudioVocabulary {
                saved = await history.completeRawSelection(historyContext, text: processedText)
            } else if let cleanupFailure {
                let attempt = await history.recordAttempt(historyContext, input: finalText, output: nil,
                    status: .failed, failureCategory: cleanupFailure, selectOutput: false,
                    startedAt: attempt.startedAt, durationMilliseconds: attempt.durationMilliseconds)
                guard !Task.isCancelled, sessions.isCurrent(token) else { historyTrace.end(); return }
                if Self.permitsDelivery(attempt) {
                    saved = await history.selectFallback(historyContext, text: processedText)
                } else { saved = attempt }
            } else {
                saved = await history.recordAttempt(historyContext, input: finalText, output: processedText,
                    status: .succeeded, selectOutput: true,
                    startedAt: attempt.startedAt, durationMilliseconds: attempt.durationMilliseconds)
            }
            guard !Task.isCancelled, sessions.isCurrent(token) else { historyTrace.end(); return }
            guard Self.permitsDelivery(saved) else {
                historyTrace.end(outcome: "notSaved")
                await stopForHistoryFailure(token: token, engine: engine)
                return
            }
        } else if configuration.capture.historyEnabled {
            settings.addTranscriptHistoryEntry(processedText, rawText: rawTranscript)
        }
        historyTrace.end()

        // Insert text
        let insertionOrchestrationTrace = PerfTrace.begin("insertion.orchestration")
        NotificationCenter.default.post(name: .dismissMenusForPaste, object: nil)
        guard !Task.isCancelled, sessions.isCurrent(token) else { return }
        let insertionContext = configuration.capture.destination == .originalField
            ? await insertionContextForDelivery() : nil
        guard !Task.isCancelled, sessions.isCurrent(token) else { return }
        // Local insertion formatting only knows built-ins; custom styles behave like Original tone.
        let insertionStyle = insertionContext.map {
            configuration.cleanup.contextPolicy.writingStyle(for: $0.category).builtInStyle ?? .original
        }
        // Encoding overlaps target/context preparation, but history is saved
        // before delivery can discard the recording's recovery copy.
        await settings.flushTranscriptHistory()
        guard !Task.isCancelled, sessions.isCurrent(token) else { return }
        let deliveryID = UUID()
        if let history, let historyContext {
            let saved = await history.recordDeliveryIntent(historyContext, id: deliveryID, payload: processedText)
            guard !Task.isCancelled, sessions.isCurrent(token) else { return }
            guard Self.permitsDelivery(saved) else {
                await stopForHistoryFailure(token: token, engine: engine)
                return
            }
        }
        guard !Task.isCancelled, let permit = sessions.authorizeDelivery(token) else { return }
        deliveryAuthorization = permit.authorization
        if let history, let historyContext, !history.isValid(historyContext) {
            permit.authorization.revoke()
            await stopForHistoryFailure(token: token, engine: engine)
            return
        }
        let original = RemyDestinationRouter.Destination(
            processIdentifier: insertionTargetApp?.processIdentifier,
            target: insertionTargetSnapshot,
            allowsPaste: insertionTargetApp != nil && insertionTargetApp?.isTerminated == false)
        let destination: RemyDestinationRouter.Destination
        if let destinationResolutionOverride {
            destination = await destinationResolutionOverride(configuration.capture.destination, original, permit.authorization)
        } else if authorizedTranscriptDeliveryOverride != nil || transcriptDeliveryOverride != nil {
            // Test delivery adapters own their fake output boundary; never move
            // native focus merely to exercise one of those adapters.
            destination = .unavailable
        } else {
            destination = await RemyDestinationRouter.resolve(
                policy: configuration.capture.destination, original: original,
                authorization: permit.authorization,
                captureCurrent: { [self] in
                    let frontmost = NSWorkspace.shared.frontmostApplication
                    let target = frontmost?.processIdentifier == ProcessInfo.processInfo.processIdentifier ? nil : frontmost
                    let proof = await captureInsertionTarget(target)
                    return .init(processIdentifier: target?.processIdentifier, target: proof,
                                 allowsPaste: target != nil && target?.isTerminated == false)
                },
                restoreOriginal: { await original.target.restoreFocus(authorization: permit.authorization) })
        }
        // A late resolver never gets authority over a cancelled or newer session.
        guard !Task.isCancelled, sessions.isCurrent(token), permit.authorization.isEligible else { return }
        if let history, let historyContext, !history.isValid(historyContext) {
            permit.authorization.revoke()
            await stopForHistoryFailure(token: token, engine: engine)
            return
        }
        let result: TextInsertionResult
        if let authorizedTranscriptDeliveryOverride {
            result = await authorizedTranscriptDeliveryOverride(processedText, permit.authorization)
        } else if let transcriptDeliveryOverride {
            // A fake adapter's invocation is its dispatch boundary. Production
            // workers consume the same lease at the actual OS effect instead.
            guard permit.authorization.consumeClipboard() else { return }
            permit.authorization.recordCopied()
            guard permit.authorization.consumePaste() else { return }
            updateCancellationAvailability()
            result = await transcriptDeliveryOverride(processedText)
        } else {
            result = await textInserter.insertText(
                processedText, context: insertionContext, style: insertionStyle,
                knownTerms: configuration.capture.vocabulary,
                targetProcessIdentifier: destination.processIdentifier,
                pasteAutomatically: destination.allowsPaste,
                modelInsertionPlan: configuration.capture.destination == .originalField ? modelInsertionPlan : nil,
                preserveModelFormatting: preserveModelFormatting,
                verbatimPayload: true,
                targetSnapshot: destination.target,
                authorization: permit.authorization
            )
        }
        guard sessions.isCurrent(token) else { return }
        isDeliveringTranscript = true
        updateCancellationAvailability()
        if let history, let historyContext {
            let outcome: RemyHistoryDelivery.Outcome = switch result {
            case .success: .dispatched
            case .copiedOnly: permit.authorization.snapshot.pasteClaimed ? .uncertain : .copied
            case .failed: permit.authorization.snapshot.pasteClaimed ? .uncertain : .failed
            }
            // A database failure after an OS effect must never cause a retry.
            _ = await history.recordDeliveryResult(historyContext, id: deliveryID, outcome: outcome)
            guard sessions.isCurrent(token), !isShuttingDown else { return }
        }
        switch result {
        case .success:
            insertionOrchestrationTrace.end(outcome: "success")
            trace.end(outcome: "success")
        case .copiedOnly:
            insertionOrchestrationTrace.end(outcome: "copiedOnly")
            trace.end(outcome: "copiedOnly")
            if transcriptDeliveryOverride == nil && !permissions.accessibilityGranted {
                presentBlockingAttentionIssueOnce(.accessibility)
            }
        case .failed:
            insertionOrchestrationTrace.end(outcome: "failed")
            trace.end(outcome: "failed")
        }
        insertionTargetApp = nil
        sessionDictationContext = nil

        // Restore mic volume and recording audio state after text insertion.
        // gives Bluetooth audio routing time to settle back to playback mode.
        let teardownTrace = PerfTrace.begin("dictation.teardown")
        volumeController.restoreMicrophoneVolume()
        await volumeController.restoreAfterRecordingWithSettle()
        guard !isShuttingDown, sessions.isCurrent(token) else { return }

        switch result {
        case .success:
            settings.playDictationSound(.completion)
            overlay.show(state: .success)
        case .copiedOnly:
            settings.playDictationSound(.completion)
            overlay.show(state: .copiedOnly)
        case .failed:
            overlay.show(state: .copiedOnly)
        }

        if let cleanupFailure {
            let delivery: CleanupFallbackDelivery = switch result {
            case .success: .inserted
            case .copiedOnly: .copied
            case .failed: .failed
            }
            overlay.show(state: .cleanupFallback(reason: cleanupFailure, delivery: delivery))
        }
        overlay.hide(afterDelay: cleanupFailure == nil ? 1.0 : 5.0)
        await discardSessionRecovery(completed: true)
        guard !isShuttingDown, sessions.isCurrent(token) else { return }
        sessionEngine = nil
        engine.setSessionConfiguration(nil)
        sessionCleanupRuntime = nil
        sessions.complete(token)
        status = .idle
        prepareAppleSpeechForNextDictation()
        teardownTrace.end()
    }

    private static func historyConfiguration(_ value: RemyDictationConfiguration) -> RemyHistoryConfiguration {
        .init(engine: value.capture.engine.rawValue, model: value.capture.model.rawValue,
            language: value.capture.language.rawValue, settingsVersion: value.settingsVersion,
            instructionVersion: value.cleanup.profile?.promptVersion ?? value.instructionVersion,
            provider: value.cleanup.profile?.adapter.rawValue ?? value.cleanup.mode.rawValue,
            cleanupModel: value.cleanup.model, prompt: value.cleanup.prompt,
            profile: .init(profileID: value.cleanup.profile?.id, cleanupMode: value.cleanup.mode.rawValue,
                reasoningEnabled: value.cleanup.reasoningEnabled,
                contextEnabled: value.cleanup.contextPolicy.isEnabled && value.cleanup.mode.usesDictationContext,
                style: value.cleanup.s1Styling.rawValue, structure: value.cleanup.s1Structure.rawValue,
                profileRevision: value.cleanup.profile?.revision,
                destinationMode: value.capture.destination.rawValue))
    }

    private static func permitsDelivery(_ acknowledgement: RemyHistoryAcknowledgement) -> Bool {
        switch acknowledgement {
        case .saved, .historyOff: true
        case .notSaved: false
        }
    }

    private func stopForHistoryFailure(token: SessionCoordinator.Token, engine: TranscriptionEngine) async {
        deliveryAuthorization?.revoke()
        // The controller retains raw/selected text in memory. Keep an existing
        // continuation source until the user explicitly recovers or deletes it.
        if let capture = recoveryCapture {
            do {
                _ = try await recoveryStore.preserve(capture, preview: currentTranscript,
                    completedTranscript: completedRecognitionTranscript,
                    transcriptPrefix: transcriptPrefix.isEmpty ? nil : transcriptPrefix,
                    previousDuration: previousRecordingDuration,
                    targetBundleIdentifier: insertionTargetApp?.bundleIdentifier ?? continuationTargetBundleIdentifier)
                recoveryCapture = nil
                engine.recoveryCapture = nil
                finishContinuation(removingSource: false)
            } catch {
                recoveryStore.errorMessage = "Recovery details could not be saved. Existing audio was kept; copy the unsaved transcript before quitting Remy."
                recoveryCapture = nil
                engine.recoveryCapture = nil
                finishContinuation(removingSource: false)
            }
        } else { await discardSessionRecovery(completed: false) }
        guard sessions.isCurrent(token), !isShuttingDown else { return }
        volumeController.restoreMicrophoneVolume()
        await volumeController.restoreAfterRecordingWithSettle()
        guard sessions.isCurrent(token), !isShuttingDown else { return }
        engine.setSessionConfiguration(nil)
        sessionEngine = nil
        sessionCleanupRuntime = nil
        insertionTargetApp = nil
        sessionDictationContext = nil
        sessions.fail(token, failure: .init(stage: .history, category: .persistence, retryable: false))
        status = .idle
        overlay.hide(afterDelay: 0)
        selectedPage = .history
        if !AppDelegate.isRunningTests {
            NotificationCenter.default.post(name: .requestShowMainWindow, object: nil)
        }
    }

    func removeHistoryEntry(id: UUID) async {
        if sessionHistoryContext?.id == id { await cancelDictation() }
        if let history { _ = await history.delete(id: id) }
        else { settings.removeTranscriptHistoryEntry(id: id) }
    }

    func clearHistory() async {
        // Revoke before awaiting deletion so queued cleanup/output cannot win.
        await cancelDictation()
        if let history { _ = await history.clear() }
        else { settings.clearTranscriptHistory() }
    }

    /// Explicit destructive action; ordinary per-entry deletion and migration
    /// continue to preserve rollback snapshots. Never remove the open database.
    func clearAllStoredHistory(paths: RemyStoragePaths = .current,
                               defaults: UserDefaults = .standard) async {
        guard !isDeletingStoredHistory, !isPreparingHistory,
              recoveringEntryID == nil, continuingEntryID == nil else { return }
        isDeletingStoredHistory = true
        storedHistoryDeletionError = nil
        defer { isDeletingStoredHistory = false }
        await cancelDictation()
        guard !recoveryStore.isInUse else {
            storedHistoryDeletionError = "History could not be cleared while recovery audio is in use. Try Clear All again after recovery finishes."
            return
        }
        var failed = false
        if let history, await history.clear() != .saved { failed = true }
        settings.clearTranscriptHistory()
        await settings.flushTranscriptHistory()
        defaults.removeObject(forKey: "transcriptHistory")
        RemyHistoryBootstrap.forgetLegacyAfterExplicitDeletion()
        do {
            try paths.removeStoredHistoryArtifacts()
            recoveryStore.forgetDeletedArtifacts()
        } catch {
            failed = true
            // Other owned files may already have been removed before a backup
            // failed. Refresh disk-backed rows; reload deliberately preserves
            // volatile recovery text until a complete explicit deletion succeeds.
            try? recoveryStore.reload()
        }
        if failed {
            storedHistoryDeletionError = "Some stored history could not be deleted. Saved transcripts, recovery files or rollback backups may remain and return after restart. Repair folder access, then use Clear All again."
        }
    }

    private func handleTranscriptionFailure(_ message: String, engine: TranscriptionEngine,
                                            token: SessionCoordinator.Token,
                                            completedTranscript: String? = nil) async {
        var presentedMessage = message
        if let capture = recoveryCapture {
            do {
                let saved = try await recoveryStore.preserve(
                    capture,
                    preview: currentTranscript,
                    completedTranscript: completedTranscript,
                    transcriptPrefix: transcriptPrefix.isEmpty ? nil : transcriptPrefix,
                    previousDuration: previousRecordingDuration,
                    targetBundleIdentifier: insertionTargetApp?.bundleIdentifier
                        ?? continuationTargetBundleIdentifier
                )
                if saved?.hasAudio == true {
                    presentedMessage += " The recording is available in History."
                }
            } catch {
                guard !isShuttingDown, sessions.isCurrent(token) else { return }
                recoveryStore.errorMessage =
                    "\(message) The recovery copy could not be saved: \(error.localizedDescription)"
            }
        }
        guard !isShuttingDown, sessions.isCurrent(token) else { return }
        finishContinuation(removingSource: false)
        recoveryCapture = nil
        completedRecognitionTranscript = nil
        engine.recoveryCapture = nil
        engine.setSessionContextualVocabulary([])
        engine.setSessionDictationContext(nil)
        clearEndOfUtteranceHandler(for: engine)
        sessionEngine = nil
        engine.setSessionConfiguration(nil)
        sessionCleanupRuntime = nil
        sessionHotkeyMode = nil
        insertionTargetApp = nil
        sessionDictationContext = nil
        currentTranscript = ""

        volumeController.restoreMicrophoneVolume()
        await volumeController.restoreAfterRecordingWithSettle()
        guard !isShuttingDown, sessions.isCurrent(token) else { return }
        overlay.hide(afterDelay: 0)
        recoveryStore.errorMessage = recoveryStore.errorMessage ?? presentedMessage
        status = .idle
        prepareAppleSpeechForNextDictation()
    }

    func cancelDictation(reason: SessionCoordinator.CancellationReason = .user) async {
        guard canCancelDictation else { return }
        if startingRequestID != nil, sessionEngine == nil, status == .idle {
            // Preparation may finish warming its cache, but loses authority to
            // begin capture. It cannot resurrect this cancelled start request.
            startingRequestID = nil
            let contextEngine = contextCaptureEngine ?? activeEngine
            invalidateContextCapture()
            sessionDictationContext = nil
            contextEngine.setSessionContextualVocabulary([])
            contextEngine.setSessionDictationContext(nil)
            insertionTargetApp = nil
            if let sessionToken {
                sessions.revoke(sessionToken, reason: reason)
                contextEngine.setSessionConfiguration(nil)
                sessionCleanupRuntime = nil
                sessionHistoryContext = nil
            }
            overlay.hide(afterDelay: 0)
            updateCancellationAvailability()
            return
        }
        startingRequestID = nil
        if let sessionToken {
            guard sessions.cancel(sessionToken, reason: reason) != .alreadyDispatched else { return }
        }
        deliveryAuthorization?.revoke()
        if let sessionToken { cleanupAttempts[sessionToken]?.cancel() }
        let trace = PerfTrace.begin("dictation.cancel")
        defer {
            trace.end()
            PerfTrace.clearSessionMetadata()
        }

        isCancelling = true
        invalidateContextCapture()
        recordingCleanupPreparation?.cancel()
        let cancelledPreparation = recordingCleanupPreparation
        recordingCleanupPreparation = nil
        let cancelledCleanup = cleanupPreparation
        cancelledCleanup?.task.cancel()
        updateCancellationAvailability()
        let preview = currentTranscript
        processingTask?.cancel()

        activeRecordingStartupID = nil
        isTransitioning = true
        pendingHoldRelease = false
        isHoldToRecordKeyDown = false
        stopAudioLevelPolling()
        overlay.hide(afterDelay: 0)

        let engine = sessionEngine ?? activeEngine
        // Let a cancelled finish/cleanup unwind before reusing the same engine.
        // The task checks cancellation before saving or delivering its result.
        await engine.stopAudioCapture()
        volumeController.recordCaptureStopped()
        await processingTask?.value
        await joinCleanupAttempts()
        processingOperationID = nil
        processingTask = nil
        await engine.cancel()
        // Stop capture before joining preparation. Remote cache consumers
        // unwind on cancellation while shared requests continue; local loads
        // still join before their runtime/session can be reused.
        _ = await cancelledCleanup?.task.value
        if cleanupPreparation?.id == cancelledCleanup?.id {
            cleanupPreparation = nil
            isPreparingCleanupEngine = false
        }
        await cancelledPreparation?.value
        preparedCleanupKey = nil
        if #available(macOS 26, *) { await AIPostProcessingService.discardPreparedSession() }
        if let capture = recoveryCapture, preserveSessionOnCancellation {
            do {
                let saved = try await recoveryStore.preserve(
                    capture, preview: preview, completedTranscript: completedRecognitionTranscript,
                    transcriptPrefix: transcriptPrefix.isEmpty ? nil : transcriptPrefix,
                    previousDuration: previousRecordingDuration,
                    targetBundleIdentifier: insertionTargetApp?.bundleIdentifier ?? continuationTargetBundleIdentifier
                )
                finishContinuation(removingSource: saved != nil && saved?.captureError == nil)
            } catch { recoveryStore.errorMessage = "The recovery copy could not be saved: \(error.localizedDescription)" }
        }
        if let capture = recoveryCapture, !preserveSessionOnCancellation {
            do { try await recoveryStore.discard(capture) }
            catch { recoveryStore.errorMessage = error.localizedDescription }
        }
        finishContinuation(removingSource: false)
        recoveryCapture = nil
        engine.recoveryCapture = nil
        completedRecognitionTranscript = nil
        engine.setSessionContextualVocabulary([])
        engine.setSessionDictationContext(nil)
        clearEndOfUtteranceHandler(for: engine)
        sessionEngine = nil
        engine.setSessionConfiguration(nil)
        sessionCleanupRuntime = nil
        sessionHotkeyMode = nil

        volumeController.restoreMicrophoneVolume()
        await volumeController.restoreAfterRecordingWithSettle()

        currentTranscript = ""
        overlay.hide(afterDelay: 0)
        status = .idle
        insertionTargetApp = nil
        sessionDictationContext = nil
        isCancelling = false
        isTransitioning = false
        updateCancellationAvailability()
    }

    private func discardSessionRecovery(completed: Bool = false) async {
        if let capture = recoveryCapture {
            do { try await recoveryStore.discard(capture) }
            catch { recoveryStore.errorMessage = error.localizedDescription }
        }
        recoveryCapture = nil
        completedRecognitionTranscript = nil
        (sessionEngine ?? activeEngine).recoveryCapture = nil
        finishContinuation(removingSource: completed)
    }

    private func finishContinuation(removingSource: Bool) {
        if let id = continuingEntryID {
            recoveryStore.release(id: id)
            if removingSource {
                do { try recoveryStore.remove(id: id) }
                catch { recoveryStore.errorMessage = error.localizedDescription }
            }
        }
        continuingEntryID = nil
        transcriptPrefix = ""
        previousRecordingDuration = 0
        continuationTargetBundleIdentifier = nil
    }

    func prepareSessionRecovery(for engine: TranscriptionEngine) {
        let capture = sessionToken.flatMap { sessions.configuration(for: $0)?.capture }
        guard capture?.historyEnabled ?? settings.historyEnabled else {
            preserveSessionOnCancellation = false
            recoveryCapture = nil
            engine.recoveryCapture = nil
            return
        }
        // Continuing explicitly opts into retaining this existing session,
        // even if preservation has since been disabled for new dictations.
        preserveSessionOnCancellation = (capture?.preserveCancelledSessions ?? settings.preserveCancelledSessions) || continuingEntryID != nil
        // A temporary safety recording protects decode/storage failures even
        // when intentional cancellation retention is off. Successful delivery
        // or an ordinary non-retained cancellation removes it. History Off
        // above remains an explicit no-disk-audio policy.
        if recoveryCapture == nil {
            do { recoveryCapture = try recoveryStore.beginCapture() } catch {
                recoveryStore.errorMessage =
                    "Audio recovery is unavailable for this recording: \(error.localizedDescription)"
            }
        }
        engine.recoveryCapture = recoveryCapture
    }

    func recoverCancelledDictation(_ entry: CancelledDictation) async {
        guard !isDeletingStoredHistory else { return }
        guard status == .idle, !isTransitioning, !transformRunner.isBusy else { return }
        guard settings.historyEnabled else {
            recoveryStore.errorMessage = "Turn on Save new dictations in History before recovering this recording. The saved audio is unchanged."
            return
        }
        let capture = RemyDictationConfiguration(capture: .init(engine: settings.engineChoice,
            model: settings.parakeetModelChoice,
            language: settings.engineChoice == .appleSpeech ? settings.appleSpeechLanguage : settings.selectedLanguage,
            fillerRules: .init(isEnabled: settings.isFillerWordRemovalEnabled, words: settings.fillerWordsToRemove),
            textRules: .init(corrections: settings.vocabularyCorrections, snippets: settings.dictationSnippets)),
            cleanup: .init())
        let trace = PerfTrace.begin("recovery.transcribe")
        defer { trace.end() }
        do {
            try recoveryStore.reload()
            guard recoveryStore.entries.contains(where: { $0.id == entry.id }) else { return }
            recoveringEntryID = entry.id
            recoveryStore.retain(id: entry.id)
            isTransitioning = true
            status = .processing
            defer {
                recoveringEntryID = nil
                recoveryStore.release(id: entry.id)
                isTransitioning = false
                status = .idle
            }
            let context = await history?.beginSession(id: UUID(), createdAt: entry.createdAt, enabled: true,
                configuration: Self.historyConfiguration(capture))
            let text = try await restoredTranscript(for: entry, engine: activeEngine)
            guard !isShuttingDown, !Task.isCancelled else { return }
            // Recovery never runs cleanup, so both local text passes apply here.
            let rules = capture.capture.textRules
            let cleaned = rules.expandingSnippets(in: rules.applyingCorrections(
                to: capture.capture.fillerRules.removingFillers(from: text)
            )).trimmingCharacters(in: .whitespacesAndNewlines)
            guard !cleaned.isEmpty else {
                recoveryStore.errorMessage = "No speech was recovered. The recording is still available; try a different speech model."
                return
            }
            if let history, let context {
                let knownRaw = entry.completedTranscript == nil && entry.transcriptPrefix == nil && entry.hasAudio ? text : nil
                let saved = await history.recordRecovered(context, text: cleaned, rawText: knownRaw)
                guard Self.permitsDelivery(saved), !isShuttingDown, !Task.isCancelled else {
                    recoveryStore.errorMessage = "Recovered text was not saved. Copy it from History before quitting. The saved recording has been kept."
                    return
                }
            } else {
                settings.addTranscriptHistoryEntry(cleaned)
                await settings.flushTranscriptHistory()
            }
            lastTranscript = cleaned
            Self.lastTranscriptForMenuBar = cleaned
            recoveryStore.release(id: entry.id)
            try recoveryStore.remove(id: entry.id)
        } catch {
            recoveryStore.errorMessage = "Recovery failed. The saved session is still available. \(error.localizedDescription)"
        }
    }

    private func restoredTranscript(for entry: CancelledDictation, engine: TranscriptionEngine) async throws -> String {
        if let completed = entry.completedTranscript, !completed.isEmpty { return completed }
        if entry.hasAudio {
            if !engine.isReady { try await engine.prepare() }
            let text = try await engine.transcribeRecording(at: recoveryStore.audioURL(id: entry.id))
            if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
               entry.transcriptPrefix?.isEmpty == false {
                // The earlier words must not conceal a failed decode of the
                // newest audio and allow that audio to be replaced on cancel.
                throw NSError(domain: "DictationRecovery", code: 3, userInfo: [NSLocalizedDescriptionKey:
                    "No speech was recognized in the latest saved audio. It has been kept so you can try another speech model."])
            }
            return CancelledDictation.joining(entry.transcriptPrefix ?? "", text)
        }
        return entry.preview.isEmpty ? (entry.transcriptPrefix ?? "") : entry.preview
    }

    func continueCancelledDictation(_ entry: CancelledDictation) async {
        guard !isDeletingStoredHistory else { return }
        guard !isShuttingDown, !isSystemSleeping, status == .idle, !isTransitioning, !isCleanupDraining else { return }
        let destinationPolicy = settings.dictationDestination
        let continuationGeneration = activationGeneration
        if hasStarted {
            await permissions.refreshForDictation()
            guard !isShuttingDown, !isSystemSleeping, status == .idle, !isTransitioning,
                  continuationGeneration == activationGeneration else { return }
        }
        rearmResolvedBlockingAttentionIssues()
        let trace = PerfTrace.begin("recovery.continue")
        defer { trace.end() }
        isTransitioning = true
        defer { if status != .recording { isTransitioning = false } }
        if !permissions.micGranted {
            let granted: Bool
            if let microphonePermissionRequester { granted = await microphonePermissionRequester() }
            else { granted = await permissions.requestMic() }
            permissions.micGranted = granted
            rearmResolvedBlockingAttentionIssues()
            recoveryStore.errorMessage = granted
                ? "Microphone access is ready. Click Continue to resume your saved session."
                : "Microphone access is required to continue. Your saved session is still available."
            if !granted { presentBlockingAttentionIssueOnce(.microphone) }
            return
        }
        do {
            try recoveryStore.reload()
            guard let saved = recoveryStore.entries.first(where: { $0.id == entry.id }) else { return }
            recoveryStore.retain(id: saved.id)
            continuingEntryID = saved.id
            recoveringEntryID = saved.id
            status = .processing
            let engine = activeEngine
            if !engine.isReady { try await engine.prepare() }
            guard continuationGeneration == activationGeneration, !isShuttingDown, !isSystemSleeping else { throw CancellationError() }
            let restored = try await restoredTranscript(for: saved, engine: engine)
            try Task.checkCancellation()
            if isSystemSleeping || continuationGeneration != activationGeneration { throw CancellationError() }
            guard !isShuttingDown else {
                recoveryStore.release(id: saved.id)
                return
            }
            guard !restored.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                throw NSError(domain: "DictationRecovery", code: 2, userInfo: [NSLocalizedDescriptionKey:
                    "No speech was restored. Try Recover text with a different speech model."])
            }
            transcriptPrefix = restored
            previousRecordingDuration = saved.duration
            continuationTargetBundleIdentifier = saved.targetBundleIdentifier
            let target = saved.targetBundleIdentifier.flatMap {
                NSRunningApplication.runningApplications(withBundleIdentifier: $0).first { !$0.isTerminated }
            }
            // Continue is an explicit request to return to the saved app.
            // Capture the current field only after that activation completes.
            insertionTargetApp = target
            await reactivateInsertionTargetIfNeeded()
            guard continuationGeneration == activationGeneration, !isShuttingDown, !isSystemSleeping else { throw CancellationError() }
            let targetSnapshot = await captureInsertionTarget(target)
            try Task.checkCancellation()
            if isSystemSleeping || continuationGeneration != activationGeneration { throw CancellationError() }
            guard !isShuttingDown else {
                recoveryStore.release(id: saved.id)
                return
            }
            recoveringEntryID = nil
            let sessionID = UUID()
            beginPerformanceSession(mode: .handsFreeToggle, sessionID: sessionID)
            updatePerformanceContextLabels()
            await beginRecording(engine: engine, mode: .handsFreeToggle, sessionID: sessionID, destination: destinationPolicy,
                contextTarget: target, contextSnapshot: targetSnapshot)
        } catch {
            invalidateContextCapture()
            finishContinuation(removingSource: false)
            recoveringEntryID = nil
            insertionTargetApp = nil
            sessionDictationContext = nil
            status = .idle
            recoveryStore.errorMessage = "Could not continue. Your saved session is still available. \(error.localizedDescription)"
        }
    }

    // MARK: - Audio Level Polling

    private func joinCleanupAttempts() async {
        let pending = cleanupAttempts
        for (token, attempt) in pending {
            await attempt.joinWorker()
            if cleanupAttempts[token] === attempt { cleanupAttempts.removeValue(forKey: token) }
        }
    }

    private func finalizeRecording(engine: TranscriptionEngine, contextTask: Task<Void, Never>?) async -> String {
        await contextTask?.value
        guard !Task.isCancelled else { return "" }
        return await engine.stopRecording()
    }

    private func invalidateContextCapture() {
        contextCaptureID = nil
        contextApplicationTask?.cancel()
        contextApplicationTask = nil
        contextCaptureEngine = nil
        screenNamesTask?.cancel()
        screenNamesTask = nil
        sessionScreenNames = []
    }

    private func captureInsertionTarget(_ target: NSRunningApplication?) async -> RemyInsertionTarget {
        if let insertionTargetCaptureOverride { return await insertionTargetCaptureOverride(target) }
        // Ordinary unit tests never inspect live AX destinations.
        guard !AppDelegate.isRunningTests else { return .unavailable }
        return await textInserter.captureTarget(for: target)
    }

    private func captureInsertionTargetAppAndContext(
        engine: TranscriptionEngine,
        target: NSRunningApplication?,
        snapshot: RemyInsertionTarget,
        configuration: RemyDictationConfiguration,
        token: SessionCoordinator.Token
    ) {
        let trace = PerfTrace.begin("dictation.capture")
        defer { trace.end() }
        invalidateContextCapture()
        sessionDictationContext = nil
        let currentPID = ProcessInfo.processInfo.processIdentifier
        insertionTargetApp = target?.processIdentifier == currentPID ? nil : target
        insertionTargetSnapshot = snapshot

        // The bounded AX identity capture completed before this overlay starts.
        overlay.beginSession(targetProcessIdentifier: insertionTargetApp?.processIdentifier)

        // Only read the screen when the active speech or cleanup model can use
        // what we read. Local `none` and FluidAudio Vocabulary never see it.
        let activeModelUsesContext = configuration.capture.engine == .assemblyAI
            || configuration.cleanup.mode.usesDictationContext
        // Window names have their own consumer gate. When they are the only
        // reason to capture, the classified context is used just to gate the
        // walk and is not published to the session.
        let walksScreenNames = ScreenNamePolicy.shouldCapture(configuration)
        let publishesContext = contextCaptureOverride != nil
            || (configuration.cleanup.contextPolicy.isEnabled && activeModelUsesContext)
        let capture: @Sendable () async -> DictationContext?
        if let contextCaptureOverride {
            let pid = insertionTargetApp?.processIdentifier
            capture = { await contextCaptureOverride(pid) }
        } else {
            guard RemyFeaturePolicy.allowsDictationContextGathering,
                  let frontmost = insertionTargetApp,
                  configuration.cleanup.contextPolicy.isEnabled,
                  activeModelUsesContext || walksScreenNames else { return }

            let processIdentifier = frontmost.processIdentifier
            let bundleIdentifier = frontmost.bundleIdentifier
            let appName = frontmost.localizedName ?? bundleIdentifier ?? "Unknown app"
            let rules = configuration.cleanup.contextPolicy.appRules
            capture = {
                DictationContextCapture.capture(
                    processIdentifier: processIdentifier,
                    bundleIdentifier: bundleIdentifier,
                    appName: appName,
                    rules: rules,
                    // Never enable Chromium/Electron accessibility for names alone.
                    requestsWebAccessibility: publishesContext
                )
            }
        }

        let id = UUID()
        contextCaptureID = id
        contextCaptureEngine = engine
        let started = ContinuousClock.now
        // AsyncStream makes cancellation release the waiter immediately, even
        // if a synchronous Accessibility call is still returning on its worker.
        let (stream, continuation) = AsyncStream<DictationContext?>.makeStream()
        let worker = Task.detached(priority: .userInitiated) {
            guard !Task.isCancelled else { continuation.finish(); return }
            let context = await capture()
            continuation.yield(context)
            continuation.finish()
        }
        continuation.onTermination = { _ in worker.cancel() }
        contextApplicationTask = Task { [weak self] in
            for await context in stream {
                guard let self, !Task.isCancelled, !self.isShuttingDown,
                      self.contextCaptureID == id, self.sessionToken == token,
                      self.sessions.isCurrent(token) else { return }
                // Starts after context so classification gates it; the stop
                // path waits for this task, never for the walk.
                if walksScreenNames, let context, ScreenNamePolicy.allowsWalk(in: context) {
                    self.startScreenNameWalk(context: context, engine: engine,
                        updatesSpeechVocabulary: configuration.capture.engine == .appleSpeech, token: token, id: id)
                }
                guard publishesContext else { return }
                self.sessionDictationContext = context
                self.updatePerformanceContextLabels()
                engine.setSessionDictationContext(context)
                await engine.updateSessionContextualVocabulary(
                    ScreenNameTerms.merged(context?.lexicalHints ?? [], self.sessionScreenNames))
                guard !Task.isCancelled, self.contextCaptureID == id,
                      self.sessionToken == token, self.sessions.isCurrent(token) else { return }
                if context != nil, self.status == .recording, !self.isTransitioning,
                   configuration.cleanup.mode == .appleIntelligence {
                    self.prewarmCleanupForRecording()
                }
                self.logger.info("contextCapture: completed alongside recording in \(String(describing: started.duration(to: .now)), privacy: .public)")
            }
        }
    }

    /// Bounded walk on a detached worker. Its result applies only to the same
    /// live capture; a late result after stop/cancel is discarded.
    private func startScreenNameWalk(
        context: DictationContext,
        engine: TranscriptionEngine,
        updatesSpeechVocabulary: Bool,
        token: SessionCoordinator.Token,
        id: UUID
    ) {
        let walk: @Sendable () async -> [String]
        if let screenNamesCaptureOverride {
            walk = { await screenNamesCaptureOverride(context) }
        } else {
            let processIdentifier = pid_t(context.processIdentifier)
            walk = { ScreenNameCapture.capture(processIdentifier: processIdentifier) }
        }
        screenNamesTask?.cancel()
        screenNamesTask = Task { [weak self] in
            let names = await Task.detached(priority: .userInitiated) { await walk() }.value
            guard let self, !Task.isCancelled, !self.isShuttingDown, !names.isEmpty,
                  self.contextCaptureID == id, self.sessionToken == token,
                  self.sessions.isCurrent(token) else { return }
            self.sessionScreenNames = names
            // Apple Speech takes contextual strings mid-session. Names landing
            // after stop but before recognition ends still reach cleanup.
            guard updatesSpeechVocabulary, self.status != .processing else { return }
            await engine.updateSessionContextualVocabulary(
                ScreenNameTerms.merged(self.sessionDictationContext?.lexicalHints ?? [], names))
        }
    }

    private func postProcessingContext(includeCapturedText: Bool) -> DictationPostProcessingContext? {
        if let token = sessionToken, sessions.isCurrent(token), let runtime = sessionCleanupRuntime {
            return runtime.postProcessingContext(from: sessionDictationContext)
        }
        guard RemyFeaturePolicy.allowsDictationContextGathering,
              settings.dictationContextAwarenessEnabled,
              let context = sessionDictationContext else { return nil }
        return context.postProcessingContext(
            style: settings.resolvedDictationWritingStyle(for: context.category),
            includeCapturedText: includeCapturedText
        )
    }

    private func postProcessingContext(
        for mode: TranscriptPostProcessingMode,
        isConfiguredServerLocal: Bool = false
    ) -> DictationPostProcessingContext? {
        let trace = PerfTrace.begin("cleanup.context")
        defer { trace.end() }
        let support = mode.dictationContextSupport(
            isConfiguredServerLocal: isConfiguredServerLocal
        )
        return postProcessingContext(
            includeCapturedText: support.includesCapturedText(
                remoteSharingEnabled: RemyFeaturePolicy.allowsRemoteDictationContextSharing
                    && settings.shareDictationContextWithRemoteProviders
            )
        )
    }

    private func reactivateInsertionTargetIfNeeded() async {
        let trace = PerfTrace.begin("insertion.targetActivation")
        defer { trace.end() }
        guard let app = insertionTargetApp, !app.isTerminated else { return }
        if NSWorkspace.shared.frontmostApplication?.processIdentifier == app.processIdentifier { return }
        guard app.activate() else { return }
        let deadline = ContinuousClock.now + .milliseconds(120)
        while NSWorkspace.shared.frontmostApplication?.processIdentifier != app.processIdentifier {
            guard !Task.isCancelled, ContinuousClock.now < deadline else { break }
            try? await Task.sleep(for: .milliseconds(8))
        }
    }

    /// A target can transiently omit its selected-text range while dictation
    /// starts. Retry only missing snapshots after the original app is active;
    /// successful start-of-session snapshots remain the source of truth.
    private func insertionContextForDelivery() async -> DictationContext? {
        let trace = PerfTrace.begin("dictation.context")
        defer { trace.end() }
        guard let captured = sessionDictationContext,
              !captured.hasTextPositionSnapshot,
              !captured.isSecureField,
              !captured.isContextExcluded,
              let target = insertionTargetApp,
              !target.isTerminated else {
            return sessionDictationContext
        }

        let processIdentifier = target.processIdentifier
        let rules = sessionToken.flatMap { sessions.configuration(for: $0)?.cleanup.contextPolicy.appRules }
            ?? settings.dictationAppRules
        let refreshed = await Task.detached(priority: .userInitiated) {
            DictationContextCapture.refreshCursorSnapshot(for: captured, rules: rules)
        }.value

        guard let refreshed,
              insertionTargetApp?.processIdentifier == processIdentifier,
              !target.isTerminated,
              NSWorkspace.shared.frontmostApplication?.processIdentifier == processIdentifier else {
            logger.warning(
                "textInsertion: target context has no cursor snapshot; using non-contextual insertion"
            )
            return captured
        }
        guard refreshed.hasTextPositionSnapshot || refreshed.isSecureField || refreshed.isContextExcluded else {
            logger.warning(
                "textInsertion: target context has no cursor snapshot; using non-contextual insertion"
            )
            return captured
        }
        guard !refreshed.isSecureField, !refreshed.isContextExcluded else { return refreshed }
        logger.info("textInsertion: recovered cursor snapshot after target reactivation")
        return refreshed
    }

    private func startAudioLevelPolling(engine: TranscriptionEngine) {
        guard let token = sessionToken else { return }
        audioLevelTask = Task { [weak self] in
            var displayTranscript = self?.transcriptPrefix ?? ""
            var transcriptPollTick = 0
            var levelPollCount = 0
            var lastTranscript = ""
            var lastDisplayedLevel: Float?
            while !Task.isCancelled {
                guard let self, self.status == .recording, self.sessions.isCurrent(token) else { break }

                if let failure = self.recoveryCapture?.failureMessage {
                    self.recordingSafetyFailure = failure
                    self.recoveryStore.errorMessage = failure
                    // Stop on a fresh task: stopDictation cancels this polling
                    // task and must still be able to drain/save the recording.
                    Task { @MainActor [weak self] in await self?.stopDictation() }
                    break
                }

                levelPollCount += 1
                let levelPollTrace = levelPollCount.isMultiple(of: 30)
                    ? PerfTrace.begin("audio.levelPoll") : nil

                // Pull level samples from the lock-protected buffer (thread-safe)
                let samples = engine.levelSamples(count: AudioMonitor.windowSampleCount)
                self.audioMonitor.update(samples: samples[...])
                let level = self.audioMonitor.smoothedLevel
                transcriptPollTick += 1
                var transcriptChanged = false

                // Only copy transcript when it has actually changed
                if transcriptPollTick >= 6 {
                    transcriptPollTick = 0
                    let transcript = engine.currentTranscript
                    if transcript != lastTranscript {
                        lastTranscript = transcript
                        displayTranscript = CancelledDictation.joining(self.transcriptPrefix, transcript)
                        self.currentTranscript = displayTranscript
                        transcriptChanged = true
                    }
                }

                if transcriptChanged || AudioMonitor.hasMeaningfulLevelChange(from: lastDisplayedLevel, to: level) {
                    self.overlay.showListening(level: level, transcript: displayTranscript)
                    lastDisplayedLevel = level
                }
                levelPollTrace?.end()
                try? await Task.sleep(for: .milliseconds(33))
            }
        }
    }

    private func stopAudioLevelPolling() {
        audioLevelTask?.cancel()
        audioLevelTask = nil
        audioMonitor.reset()
    }

    private func configureEndOfUtteranceHandler(for engine: TranscriptionEngine) {
        guard let parakeet = engine as? ParakeetEngine, let token = sessionToken,
              let configuration = sessions.configuration(for: token) else { return }
        parakeet.endOfUtteranceHandler = { [weak self] in
            PerfTrace.event("eou.detected")
            Task { @MainActor [weak self] in
                guard let self else { return }
                guard self.sessions.isCurrent(token), configuration.capture.autoStopAfterSpeechEnds else { return }
                guard configuration.capture.model.supportsEndOfUtterance else { return }
                guard self.sessionHotkeyMode == .handsFreeToggle else { return }
                guard self.status == .recording, !self.isTransitioning else { return }
                PerfTrace.event("eou.stop")
                await self.stopDictation()
            }
        }
    }

    private func clearEndOfUtteranceHandler(for engine: TranscriptionEngine) {
        (engine as? ParakeetEngine)?.endOfUtteranceHandler = nil
    }

}

// MARK: - Audio Device Manager

@Observable
final class AudioDeviceManager {
    var availableInputDevices: [(uid: String, name: String)] = []

    private var listenerBlock: AudioObjectPropertyListenerBlock?

    init() {
        refreshDevices()
        installDeviceChangeListener()
    }

    deinit {
        removeDeviceChangeListener()
    }

    func refreshDevices() {
        availableInputDevices = Self.enumerateInputDevices()
    }

    // MARK: - Device Enumeration

    static func enumerateInputDevices() -> [(uid: String, name: String)] {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var dataSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(
            AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &dataSize
        ) == noErr, dataSize > 0 else { return [] }

        let deviceCount = Int(dataSize) / MemoryLayout<AudioDeviceID>.size
        var deviceIDs = [AudioDeviceID](repeating: 0, count: deviceCount)
        guard AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &dataSize, &deviceIDs
        ) == noErr else { return [] }

        let lidClosed = MicrophoneHelper.isLidClosed()
        var result: [(uid: String, name: String)] = []
        for id in deviceIDs {
            guard isPhysicalDevice(deviceID: id),
                  hasInputChannels(deviceID: id),
                  isAvailable(deviceID: id),
                  !(lidClosed && transport(for: id) == kAudioDeviceTransportTypeBuiltIn),
                  let uid = deviceUID(for: id),
                  let name = deviceName(for: id) else { continue }
            result.append((uid: uid, name: name))
        }
        return result
    }

    private static func isPhysicalDevice(deviceID: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyTransportType,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var transportType: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, &transportType) == noErr else {
            return false
        }
        return supportsTransport(transportType)
    }

    nonisolated static func supportsTransport(_ transport: UInt32) -> Bool {
        // Continuity microphones are virtual inputs and can take seconds to wake.
        ![kAudioDeviceTransportTypeAggregate, kAudioDeviceTransportTypeVirtual,
          kAudioDeviceTransportTypeContinuityCaptureWired,
          kAudioDeviceTransportTypeContinuityCaptureWireless].contains(transport)
    }

    static func isAvailable(deviceID: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceIsAlive,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var alive: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, &alive) == noErr,
              alive != 0 else { return false }
        if let uid = deviceUID(for: deviceID),
           let device = AVCaptureDevice(uniqueID: uid) {
            return device.isConnected && !device.isSuspended
        }
        return true
    }

    nonisolated static func transport(for deviceID: AudioDeviceID) -> UInt32? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyTransportType,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var transport: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, &transport) == noErr else { return nil }
        return transport
    }

    private static func hasInputChannels(deviceID: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain
        )
        var dataSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(deviceID, &address, 0, nil, &dataSize) == noErr, dataSize > 0 else {
            return false
        }
        let rawPointer = UnsafeMutableRawPointer.allocate(
            byteCount: Int(dataSize),
            alignment: MemoryLayout<AudioBufferList>.alignment
        )
        defer { rawPointer.deallocate() }
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &dataSize, rawPointer) == noErr else {
            return false
        }
        let bufferList = rawPointer.assumingMemoryBound(to: AudioBufferList.self)
        let buffers = UnsafeMutableAudioBufferListPointer(bufferList)
        return buffers.contains { $0.mNumberChannels > 0 }
    }

    private static func deviceUID(for deviceID: AudioDeviceID) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceUID,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var uid: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, &uid) == noErr,
              let result = uid?.takeUnretainedValue() else { return nil }
        return result as String
    }

    static func deviceName(for deviceID: AudioDeviceID) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceNameCFString,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var name: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, &name) == noErr,
              let result = name?.takeUnretainedValue() else { return nil }
        return result as String
    }

    // MARK: - Device Change Listener

    private func installDeviceChangeListener() {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            DispatchQueue.main.async { [weak self] in
                self?.refreshDevices()
            }
        }
        listenerBlock = block
        AudioObjectAddPropertyListenerBlock(
            AudioObjectID(kAudioObjectSystemObject), &address, DispatchQueue.main, block
        )
    }

    private func removeDeviceChangeListener() {
        guard let block = listenerBlock else { return }
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        AudioObjectRemovePropertyListenerBlock(
            AudioObjectID(kAudioObjectSystemObject), &address, DispatchQueue.main, block
        )
        listenerBlock = nil
    }
}

// MARK: - Microphone Helper

enum MicrophoneHelper {
    struct Input: Equatable {
        let id: AudioDeviceID
        let uid: String
        let isBuiltIn: Bool
    }

    // Pure routing policy: preferred usable input, usable system default, then
    // local hardware. Never try an implicit route that bypasses this filtering.
    static func candidates(selectedUID: String?, defaultID: AudioDeviceID?, inputs: [Input], lidClosed: Bool) -> [AudioDeviceID] {
        let usable = inputs.filter { !($0.isBuiltIn && lidClosed) }
        var result: [AudioDeviceID] = []
        if let selected = usable.first(where: { $0.uid == selectedUID }) { result.append(selected.id) }
        if let system = usable.first(where: { $0.id == defaultID }), !result.contains(system.id) { result.append(system.id) }
        for input in usable.sorted(by: { $0.isBuiltIn && !$1.isBuiltIn }) where !result.contains(input.id) {
            result.append(input.id)
        }
        return result
    }

    static func startCandidates() -> [AudioDeviceID] {
        let inputs = AudioDeviceManager.enumerateInputDevices().compactMap { device -> Input? in
            guard let id = deviceID(forUID: device.uid), let transport = AudioDeviceManager.transport(for: id) else { return nil }
            return Input(id: id, uid: device.uid, isBuiltIn: transport == kAudioDeviceTransportTypeBuiltIn)
        }
        return candidates(selectedUID: Settings.shared.selectedMicrophoneUID,
                          defaultID: currentDefaultInputDeviceID(), inputs: inputs, lidClosed: isLidClosed())
    }

    static func effectiveDeviceID() -> AudioDeviceID? {
        startCandidates().first
    }

    nonisolated static func isLidClosed() -> Bool {
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPMrootDomain"))
        guard service != 0 else { return false }
        defer { IOObjectRelease(service) }
        return (IORegistryEntryCreateCFProperty(service, "AppleClamshellState" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? Bool) ?? false
    }

    nonisolated static func currentDefaultInputDeviceID() -> AudioDeviceID? {
        var propertyAddress = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultInputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var deviceID: AudioDeviceID = 0
        var dataSize = UInt32(MemoryLayout<AudioDeviceID>.size)
        let status = AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject),
            &propertyAddress, 0, nil, &dataSize, &deviceID
        )
        guard status == noErr, deviceID != 0, deviceID != AudioDeviceID(kAudioObjectUnknown) else { return nil }
        return deviceID
    }

    static func deviceID(forUID uid: String) -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var dataSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(
            AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &dataSize
        ) == noErr, dataSize > 0 else { return nil }

        let deviceCount = Int(dataSize) / MemoryLayout<AudioDeviceID>.size
        var deviceIDs = [AudioDeviceID](repeating: 0, count: deviceCount)
        guard AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &dataSize, &deviceIDs
        ) == noErr else { return nil }

        for id in deviceIDs {
            var uidAddress = AudioObjectPropertyAddress(
                mSelector: kAudioDevicePropertyDeviceUID,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain
            )
            var deviceUID: Unmanaged<CFString>?
            var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
            if AudioObjectGetPropertyData(id, &uidAddress, 0, nil, &size, &deviceUID) == noErr,
               let uidValue = deviceUID?.takeUnretainedValue(),
               (uidValue as String) == uid {
                return id
            }
        }
        return nil
    }
}
