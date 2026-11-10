//
//  TransformRunner.swift
//  Dictate Anywhere
//
//  One foreground "rewrite the selection" action: read → provider → replace.
//  AppState only wires dependencies; this type owns the transform state.
//

import Foundation

/// Every user-visible transform result. Titles/details never include provider
/// error bodies, endpoints or selected text.
nonisolated enum TransformNotice: Error, Equatable, Sendable {
    case cleanupOff
    case providerUnsupported(String)
    case providerUnavailable
    case insecureEndpoint
    case missingCredential
    case invalidProfile
    case accessibilityRequired
    case noSelection
    case protectedField
    case selectionUnreadable
    case selectionTooLong(words: Int)
    case emptyPrompt
    case dictationBusy
    case alreadyRunning
    case providerFailed(RemyCleanupFailure)
    case emptyResult
    case refusal
    case focusChanged
    case deliveryFailed

    var title: String {
        switch self {
        case .cleanupOff: "Transforms need an AI provider"
        case .providerUnsupported(let name): "\(name) can't run transforms"
        case .providerUnavailable: "AI provider unavailable"
        case .insecureEndpoint: "Transforms need HTTPS"
        case .missingCredential: "Provider key missing"
        case .invalidProfile: "Saved profile unavailable"
        case .accessibilityRequired: "Accessibility access needed"
        case .noSelection: "Select text to transform"
        case .protectedField: "Transforms are off here"
        case .selectionUnreadable: "Couldn't read the selection"
        case .selectionTooLong(let words): "Selection too long (\(words.formatted()) words)"
        case .emptyPrompt: "Transform has no instructions"
        case .dictationBusy: "Finish dictating first"
        case .alreadyRunning: "Already transforming"
        case .providerFailed(let failure): Self.title(for: failure)
        case .emptyResult: "Transform returned nothing"
        case .refusal: "The model declined"
        case .focusChanged: "Focus changed"
        case .deliveryFailed: "Couldn't paste the result"
        }
    }

    var detail: String {
        switch self {
        case .cleanupOff, .providerUnsupported, .providerUnavailable, .invalidProfile:
            "Choose a provider in Transcript Cleanup."
        case .insecureEndpoint: "Use https:// or a local endpoint in Transcript Cleanup."
        case .missingCredential: "Add the API key in Transcript Cleanup."
        case .accessibilityRequired: "Allow Remy in Privacy & Security."
        case .noSelection: "Highlight 1–\(TransformLimits.maximumWords.formatted()) words, then try again."
        case .protectedField: "Password and excluded apps aren't read."
        case .selectionUnreadable: "This app doesn't share its selection."
        case .selectionTooLong: "Transforms accept up to \(TransformLimits.maximumWords.formatted()) words."
        case .emptyPrompt: "Add instructions on the Transforms page."
        case .dictationBusy, .alreadyRunning: "Try again when it finishes."
        case .providerFailed, .emptyResult, .refusal: "Your text was not changed."
        case .focusChanged: "Result copied · Press ⌘V"
        case .deliveryFailed: "Your text was not changed."
        }
    }

    private static func title(for failure: RemyCleanupFailure) -> String {
        switch failure {
        case .timeout: "Transform timed out"
        case .inputTooLong, .requestBudgetExceeded: "Too long for this model"
        case .authentication: "Provider rejected the key"
        case .rateLimited: "Provider is rate limiting"
        case .modelUnavailable: "Model unavailable"
        default: "Transform failed"
        }
    }
}

nonisolated struct TransformRequest: Equatable, Sendable {
    let system: String
    let user: String
}

/// The text and destination proof captured when the shortcut fired.
struct TransformSelection {
    let text: String
    let processIdentifier: pid_t
    let target: RemyInsertionTarget
}

enum TransformSelectionResult {
    case selected(TransformSelection)
    case rejected(TransformNotice)
}

enum TransformOutcome: Equatable {
    case inserted
    /// Copied without pasting; the notice explains why when it is not routine.
    case copied(TransformNotice?)
    case cancelled
    case notStarted(TransformNotice)
    case failed(TransformNotice)
}

enum TransformEvent: Equatable {
    case processing(name: String, processIdentifier: pid_t)
    case finished(TransformOutcome)
}

@Observable
final class TransformRunner {
    enum Phase: Equatable { case idle, reading, processing, delivering }

    struct Dependencies {
        /// Resolved at trigger time from the current cleanup settings.
        var route: () -> TransformProviderRoute
        var readSelection: () async -> TransformSelectionResult
        /// Re-reads the live selection right before pasting.
        var selectionStillMatches: (TransformSelection) async -> Bool
        var frontmostProcessIdentifier: () -> pid_t?
        /// Delivers through the guarded clipboard/paste path.
        var deliver: (String, TransformSelection, _ pasteAutomatically: Bool, RemyDeliveryAuthorization) async -> TextInsertionResult

        static let inert = Dependencies(
            route: { .unavailable(.providerUnavailable) },
            readSelection: { .rejected(.selectionUnreadable) },
            selectionStillMatches: { _ in false },
            frontmostProcessIdentifier: { nil },
            deliver: { _, _, _, _ in .failed })
    }

    @ObservationIgnored var dependencies: Dependencies
    @ObservationIgnored var onEvent: ((TransformEvent) -> Void)?
    @ObservationIgnored var onPhaseChange: (() -> Void)?

    private(set) var phase: Phase = .idle { didSet { if phase != oldValue { onPhaseChange?() } } }
    @ObservationIgnored private var generation: UUID?
    @ObservationIgnored private var authorization: RemyDeliveryAuthorization?
    @ObservationIgnored private var providerTask: Task<String, Error>?

    init(dependencies: Dependencies = .inert) {
        self.dependencies = dependencies
    }

    var isBusy: Bool { phase != .idle }

    /// A paste that may already have been dispatched cannot be retracted.
    var canCancel: Bool { isBusy && authorization?.snapshot.pasteClaimed != true }

    /// Mutual exclusion: callers pass whether dictation is active. A transform
    /// never starts during dictation, and AppState refuses dictation while busy.
    @discardableResult
    func run(_ instruction: TransformInstruction, dictationBusy: Bool) async -> TransformOutcome {
        guard !isBusy else { return finish(.notStarted(.alreadyRunning), generation: nil) }
        guard !dictationBusy else { return finish(.notStarted(.dictationBusy), generation: nil) }
        guard !instruction.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return finish(.notStarted(.emptyPrompt), generation: nil)
        }
        let complete: TransformProviderRoute.Completion
        switch dependencies.route() {
        case .unavailable(let notice): return finish(.notStarted(notice), generation: nil)
        case .ready(_, let completion): complete = completion
        }

        let generation = UUID()
        self.generation = generation
        phase = .reading
        let selection: TransformSelection
        switch await dependencies.readSelection() {
        case .rejected(let notice):
            guard isCurrent(generation) else { return .cancelled }
            return finish(.notStarted(notice), generation: generation)
        case .selected(let value):
            selection = value
        }
        guard isCurrent(generation) else { return .cancelled }
        if let notice = TransformLimits.validate(selection.text) {
            return finish(.notStarted(notice), generation: generation)
        }

        phase = .processing
        let authorization = RemyDeliveryAuthorization()
        self.authorization = authorization
        onEvent?(.processing(name: instruction.name, processIdentifier: selection.processIdentifier))
        let request = TransformPrompt.request(instruction, selection: selection.text)
        let task = Task { @MainActor in try await complete(request) }
        providerTask = task
        let output: String
        do {
            let response = try await task.value
            guard isCurrent(generation) else { return .cancelled }
            output = try TransformPrompt.finalizedOutput(response, original: selection.text)
        } catch let notice as TransformNotice {
            guard isCurrent(generation) else { return .cancelled }
            return finish(.failed(notice), generation: generation)
        } catch {
            guard isCurrent(generation) else { return .cancelled }
            return finish(.failed(.providerFailed(RemyCleanupFailure.classify(error))), generation: generation)
        }
        providerTask = nil

        phase = .delivering
        // Never paste into a different app, or over a selection that changed
        // while the provider was working. The result is still copied.
        var focusUnchanged = dependencies.frontmostProcessIdentifier() == selection.processIdentifier
        if focusUnchanged { focusUnchanged = await dependencies.selectionStillMatches(selection) }
        guard isCurrent(generation), authorization.isEligible else { return .cancelled }
        let result = await dependencies.deliver(output, selection, focusUnchanged, authorization)
        guard isCurrent(generation) else { return .cancelled }
        switch result {
        case .success: return finish(.inserted, generation: generation)
        case .copiedOnly: return finish(.copied(focusUnchanged ? nil : .focusChanged), generation: generation)
        case .failed: return finish(.failed(.deliveryFailed), generation: generation)
        }
    }

    /// Esc: stop provider work and revoke delivery. A dispatched paste remains.
    func cancel() {
        guard canCancel else { return }
        authorization?.revoke()
        providerTask?.cancel()
        _ = finish(.cancelled, generation: generation)
    }

    private func isCurrent(_ generation: UUID) -> Bool { self.generation == generation }

    private func finish(_ outcome: TransformOutcome, generation: UUID?) -> TransformOutcome {
        if let generation, isCurrent(generation) {
            self.generation = nil
            authorization = nil
            providerTask = nil
            phase = .idle
        }
        onEvent?(.finished(outcome))
        return outcome
    }
}
