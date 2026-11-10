import Foundation
import XCTest
@testable import Dictate_Anywhere

@MainActor
final class RemyDictationConfigurationTests: XCTestCase {
    private func configuration(mode: TranscriptPostProcessingMode = .none,
                               language: SupportedLanguage = .english,
                               s1ModelURL: URL? = nil) -> RemyDictationConfiguration {
        .init(capture: .init(language: language), cleanup: .init(mode: mode, s1ModelURL: s1ModelURL))
    }

    func testValueSnapshotKeepsOriginalCaptureAndCleanupSelections() {
        var vocabulary = ["Remy"]
        var fillers = ["um"]
        let reference = UUID()
        let snapshot = RemyDictationConfiguration(credentialReference: reference,
            capture: .init(engine: .appleSpeech, model: .compactEnglish, language: .german,
                microphoneUID: "synthetic-microphone", vocabulary: vocabulary,
                fillerRules: .init(isEnabled: true, words: fillers), muteSystemAudio: true,
                boostMicrophoneVolume: true, preserveCancelledSessions: true, autoStopAfterSpeechEnds: true),
            cleanup: .init(mode: .ollama, prompt: "Synthetic instruction", baseURL: "http://127.0.0.1:11434",
                model: "synthetic-model", reasoningEnabled: false, s1Styling: .formal,
                s1Structure: .lists, s1ContextSetting: .email,
                s1ModelURL: URL(fileURLWithPath: "/synthetic/model.gguf")))
        vocabulary[0] = "Changed"
        fillers[0] = "uh"
        XCTAssertEqual(snapshot.settingsVersion, 1)
        XCTAssertEqual(snapshot.instructionVersion, 1)
        XCTAssertEqual(snapshot.credentialReference, reference)
        XCTAssertEqual(snapshot.capture.engine, .appleSpeech)
        XCTAssertEqual(snapshot.capture.model, .compactEnglish)
        XCTAssertEqual(snapshot.capture.language, .german)
        XCTAssertEqual(snapshot.capture.microphoneUID, "synthetic-microphone")
        XCTAssertEqual(snapshot.capture.vocabulary, ["Remy"])
        XCTAssertEqual(snapshot.capture.fillerRules.words, ["um"])
        XCTAssertTrue(snapshot.capture.muteSystemAudio)
        XCTAssertTrue(snapshot.capture.boostMicrophoneVolume)
        XCTAssertTrue(snapshot.capture.preserveCancelledSessions)
        XCTAssertTrue(snapshot.capture.autoStopAfterSpeechEnds)
        XCTAssertEqual(snapshot.cleanup.prompt, "Synthetic instruction")
        XCTAssertEqual(snapshot.cleanup.baseURL, "http://127.0.0.1:11434")
        XCTAssertEqual(snapshot.cleanup.model, "synthetic-model")
        XCTAssertFalse(snapshot.cleanup.reasoningEnabled)
        XCTAssertEqual(snapshot.cleanup.s1Styling, .formal)
        XCTAssertEqual(snapshot.cleanup.s1Structure, .lists)
        XCTAssertEqual(snapshot.cleanup.s1ContextSetting, .email)
        XCTAssertEqual(snapshot.cleanup.s1ModelURL, URL(fileURLWithPath: "/synthetic/model.gguf"))
        XCTAssertFalse(snapshot.cleanup.contextPolicy.isEnabled)
    }

    private func context(text: String = "Private preceding words") -> DictationContext {
        .init(processIdentifier: 7, bundleIdentifier: "com.apple.Terminal", appName: "Terminal",
            category: .terminalAgent, documentURL: "file:///private/project", documentTitle: "Claude Code — project",
            fieldRole: "AXTextArea", fieldSubrole: nil, fieldPurpose: .unknown,
            textBeforeCursor: text, selectedText: "Private selection", textAfterCursor: "Private following words",
            isSecureField: false, isContextExcluded: false)
    }

    func testFrozenContextPolicyAppliesLocalRemoteAndDisabledSharing() async throws {
        for (mode, endpoint, share, includesText) in [
            (TranscriptPostProcessingMode.appleIntelligence, "", false, true),
            (.s1Mini, "", true, false),
            (.ollama, "http://127.0.0.1:11434", false, true),
            (.openAICompatible, "http://[::1]:1234/v1", false, true),
            (.openAICompatible, "https://example.test/v1", false, false),
            (.openRouter, "https://openrouter.ai/api/v1", false, false),
            (.openRouter, "https://openrouter.ai/api/v1", true, true),
            (.none, "", true, false), (.fluidAudioVocabulary, "", true, false)
        ] {
            let config = RemyDictationConfiguration(capture: .init(), cleanup: .init(mode: mode,
                baseURL: endpoint, contextPolicy: .init(isEnabled: true, shareCapturedTextRemotely: share,
                    writingStyles: [.terminalAgent: .builtIn(.original)])))
            var received: DictationPostProcessingContext?
            let runtime = RemyCleanupRuntime(configuration: config, contextualOperation: { text, context in
                received = context
                return text
            })
            _ = try await runtime.process(text: "Update this function", context: context())
            if mode == .none || mode == .fluidAudioVocabulary {
                XCTAssertNil(received)
            } else {
                XCTAssertEqual(received?.category, .terminalAgent)
                XCTAssertEqual(received?.style, .builtIn(.original))
                XCTAssertEqual(received?.textBeforeCursor != nil, includesText, mode.rawValue)
                XCTAssertEqual(received?.documentTitle != nil, includesText, mode.rawValue)
                XCTAssertEqual(received?.appName, "Terminal")
            }
        }
        let disabled = RemyCleanupRuntime.make(configuration: configuration(mode: .appleIntelligence))
        XCTAssertNil(disabled.postProcessingContext(from: context()))
    }

    func testOnlyValidatedLoopbackRoutesReceiveFullContextWithoutOptIn() {
        for endpoint in ["http://localhost:1234/v1", "http://127.0.0.1:11434", "http://[::1]:1234/v1"] {
            XCTAssertTrue(RemyCleanupRuntime.isLocalContextEndpoint(baseURL: endpoint, model: "fixture"), endpoint)
        }
        for endpoint in ["http://0.0.0.0:1234", "http://192.168.1.2:1234", "https://localhost.example.test", "http://user@localhost:1234", "http://localhost:1234?token=private"] {
            XCTAssertFalse(RemyCleanupRuntime.isLocalContextEndpoint(baseURL: endpoint, model: "fixture"), endpoint)
        }
        for model in ["fixture-cloud", "fixture:cloud", " FIXTURE-CLOUD "] {
            XCTAssertFalse(RemyCleanupRuntime.isLocalContextEndpoint(baseURL: "http://localhost:11434", model: model))
        }
    }

    func testContextSnapshotKeepsPrivacyAndCategoryStylesAcrossSettingsChanges() {
        let settings = Settings.shared
        let awareness = settings.dictationContextAwarenessEnabled
        let sharing = settings.shareDictationContextWithRemoteProviders
        let mode = settings.transcriptPostProcessingMode
        let style = settings.dictationWritingStyle(for: .terminalAgent)
        let s1 = settings.s1MiniAppStyling
        let appRules = settings.dictationAppRules
        defer {
            settings.dictationContextAwarenessEnabled = awareness
            settings.shareDictationContextWithRemoteProviders = sharing
            settings.transcriptPostProcessingMode = mode
            settings.setDictationWritingStyle(style, for: .terminalAgent)
            settings.s1MiniAppStyling = s1
            settings.dictationAppRules = appRules
        }
        settings.dictationContextAwarenessEnabled = true
        settings.shareDictationContextWithRemoteProviders = false
        settings.transcriptPostProcessingMode = .openRouter
        settings.setDictationWritingStyle(.original, for: .terminalAgent)
        settings.s1MiniAppStyling.set(.casual, for: .terminalAgent)
        settings.dictationAppRules = [.init(bundleIdentifier: "com.apple.Terminal", appName: "Terminal", category: .terminalAgent)]
        let captured = RemyCleanupRuntime.capture(settings: settings)
        settings.dictationContextAwarenessEnabled = false
        settings.shareDictationContextWithRemoteProviders = true
        settings.setDictationWritingStyle(.formal, for: .terminalAgent)
        settings.s1MiniAppStyling.set(.formal, for: .terminalAgent)
        settings.dictationAppRules = []
        let providerContext = captured.runtime.postProcessingContext(from: context())
        XCTAssertNotNil(providerContext)
        XCTAssertNil(providerContext?.textBeforeCursor)
        XCTAssertEqual(providerContext?.style, .builtIn(.original))
        XCTAssertEqual(captured.configuration.cleanup.contextPolicy.appRules.first?.category, .terminalAgent)
        XCTAssertEqual(captured.configuration.cleanup.contextPolicy.s1Styling(for: .terminalAgent, fallback: .semiFormal), .casual)
        XCTAssertNil(RemyCleanupRuntime.capture(settings: settings).runtime.postProcessingContext(from: context()))
    }

    func testActualContextIsAssessedBeforeProviderOperation() async {
        let config = RemyDictationConfiguration(capture: .init(), cleanup: .init(mode: .openRouter,
            contextPolicy: .init(isEnabled: true, shareCapturedTextRemotely: true)))
        var calls = 0
        let runtime = RemyCleanupRuntime(configuration: config, contextualOperation: { text, _ in
            calls += 1
            return text
        })
        do {
            _ = try await runtime.process(text: "short", context: context(text: String(repeating: "private ", count: 2_000)))
            XCTFail("Actual context must fit the same request budget as the transcript")
        } catch {
            XCTAssertEqual(error as? RemoteCleanupInputPolicyError, .singleRequestBudgetExceeded)
        }
        XCTAssertEqual(calls, 0)
    }

    func testFillerSnapshotPreservesBoundariesNegationUnicodeAndPunctuation() {
        let rules = RemyFillerRules(isEnabled: true, words: ["um", "uh", "嗯", "呃", "唔"])
        XCTAssertEqual(rules.removingFillers(from: "I um think uh this works."), "I think this works.")
        XCTAssertEqual(rules.removingFillers(from: "The umbrella is here"), "The umbrella is here")
        XCTAssertEqual(rules.removingFillers(from: "我嗯嗯觉得可以"), "我觉得可以")
        XCTAssertEqual(rules.removingFillers(from: "我唔知道"), "我唔知道")
        XCTAssertEqual(rules.removingFillers(from: "我知道，唔，佢唔系"), "我知道，佢唔系")
        XCTAssertEqual(rules.removingFillers(from: "好的 ，嗯，走吧"), "好的，走吧")
        XCTAssertEqual(rules.removingFillers(from: "Café 👩🏽‍💻 um works"), "Café 👩🏽‍💻 works")
    }

    func testDisabledAndEmptyFillerRulesPreserveExactRawInput() {
        let raw = "  um words\n "
        XCTAssertEqual(RemyFillerRules(isEnabled: false, words: ["um"]).removingFillers(from: raw), raw)
        XCTAssertEqual(RemyFillerRules(isEnabled: true, words: []).removingFillers(from: raw), raw)
        XCTAssertEqual(RemyFillerRules(isEnabled: true, words: [" "]).removingFillers(from: raw), raw)
    }

    func testOffAndVocabularyRuntimePassThroughWithoutServices() async throws {
        let raw = "Exact raw text\n"
        for mode in [TranscriptPostProcessingMode.none, .fluidAudioVocabulary] {
            let snapshot = configuration(mode: mode)
            let runtime = RemyCleanupRuntime.make(configuration: snapshot)
            XCTAssertEqual(runtime.credentialReference, snapshot.credentialReference)
            let output = try await runtime.process(text: raw)
            XCTAssertEqual(output, raw)
        }
    }

    func testRuntimeOperationFreezesValuesRatherThanMutableReferences() async throws {
        var selectedPrompt = "Original synthetic prompt"
        let snapshot = configuration()
        let runtime = RemyCleanupRuntime(configuration: snapshot) { [selectedPrompt] text in
            selectedPrompt + ": " + text
        }
        selectedPrompt = "Changed prompt"
        let output = try await runtime.process(text: "Synthetic input")
        XCTAssertEqual(output, "Original synthetic prompt: Synthetic input")
        XCTAssertEqual(selectedPrompt, "Changed prompt")
    }

    func testOpenRouterResolutionFreezesEnvironmentValueAndHonorsDirectKey() {
        var environment = ["SYNTHETIC_ROUTER_KEY": " original-synthetic-value "]
        let frozen = RemyCleanupRuntime.resolvedOpenRouterCredential(apiKey: "", environmentReference: "SYNTHETIC_ROUTER_KEY", environment: environment)
        environment["SYNTHETIC_ROUTER_KEY"] = "changed-synthetic-value"
        XCTAssertEqual(frozen, "original-synthetic-value")
        XCTAssertEqual(RemyCleanupRuntime.resolvedOpenRouterCredential(apiKey: " direct-synthetic-value ", environmentReference: "SYNTHETIC_ROUTER_KEY", environment: environment), "direct-synthetic-value")
        XCTAssertEqual(RemyCleanupRuntime.resolvedOpenRouterCredential(apiKey: "", environmentReference: "sk-or-synthetic-inline", environment: [:]), "sk-or-synthetic-inline")
        XCTAssertNil(RemyCleanupRuntime.resolvedOpenRouterCredential(apiKey: "", environmentReference: "MISSING_SYNTHETIC_KEY", environment: [:]))
    }

    func testMissingCapturedRouterKeyRejectsBeforeAnyProviderRequest() async {
        let runtime = RemyCleanupRuntime.make(configuration: configuration(mode: .openRouter))
        do {
            _ = try await runtime.process(text: "Synthetic input")
            XCTFail("Missing captured key must not trigger environment fallback or a request")
        } catch {
            XCTAssertEqual(error as? RemyCleanupRuntime.RuntimeError, .missingCapturedCredential)
        }
    }

    func testS1RequiresCapturedURLAndRejectsChangedValidationResult() async {
        let expected = URL(fileURLWithPath: "/synthetic/original.gguf")
        let changed = URL(fileURLWithPath: "/synthetic/changed.gguf")
        let missing = RemyCleanupRuntime.make(configuration: configuration(mode: .s1Mini))
        do {
            _ = try await missing.process(text: "Synthetic input")
            XCTFail("S1 cannot resolve a future manager URL")
        } catch {
            XCTAssertEqual(error as? RemyCleanupRuntime.RuntimeError, .missingValidatedS1Model)
        }
        var validations = 0
        let runtime = RemyCleanupRuntime.make(configuration: configuration(mode: .s1Mini, s1ModelURL: expected), validateS1Model: {
            validations += 1
            return changed
        })
        do {
            _ = try await runtime.process(text: "Synthetic input")
            XCTFail("Changed asset must be rejected before model loading")
        } catch {
            XCTAssertEqual(error as? RemyCleanupRuntime.RuntimeError, .s1ModelChanged)
        }
        XCTAssertEqual(validations, 1)
    }

    func testNonEnglishS1ReportsUnsupportedLanguageWithoutValidationOrModelLoading() async throws {
        var validations = 0
        let runtime = RemyCleanupRuntime.make(configuration: configuration(mode: .s1Mini, language: .german), validateS1Model: {
            validations += 1
            return URL(fileURLWithPath: "/synthetic/unused.gguf")
        })
        do {
            _ = try await runtime.process(text: "Synthetischer Text")
            XCTFail("An unavailable cleanup pass must not masquerade as successful unchanged output")
        } catch {
            XCTAssertEqual(error as? RemyCleanupFailure, .unsupportedLanguage)
        }
        XCTAssertEqual(validations, 0)
    }

    func testEmptyCleanupOutputIsRejectedAndUnchangedValidOutputIsAllowed() async throws {
        for output in ["", " \n\t", "Already clean."] {
            let runtime = RemyCleanupRuntime(configuration: configuration(mode: .appleIntelligence)) { _ in output }
            do {
                let actual = try await runtime.process(text: "Already clean.")
                XCTAssertEqual(output, "Already clean.")
                XCTAssertEqual(actual, output)
            } catch {
                XCTAssertNotEqual(output, "Already clean.")
                XCTAssertEqual(error as? RemyCleanupFailure, .invalidResponse)
            }
        }
    }

    func testCancellationBeforeProcessingDoesNotInvokeOperation() async {
        var calls = 0
        let runtime = RemyCleanupRuntime(configuration: configuration()) { text in
            calls += 1
            return text
        }
        let task = Task { try await runtime.process(text: "Synthetic input") }
        task.cancel()
        do {
            _ = try await task.value
            XCTFail("Cancelled task must not begin cleanup")
        } catch {
            XCTAssertTrue(error is CancellationError)
        }
        XCTAssertEqual(calls, 0)
    }

    func testCancellationDiscardsNoncooperativeLateOperationResult() async {
        let started = expectation(description: "fake cleanup suspended")
        var resume: CheckedContinuation<String, Never>?
        let runtime = RemyCleanupRuntime(configuration: configuration()) { _ in
            await withCheckedContinuation { continuation in
                resume = continuation
                started.fulfill()
            }
        }
        let task = Task { try await runtime.process(text: "Synthetic input") }
        await fulfillment(of: [started], timeout: 1)
        task.cancel()
        resume?.resume(returning: "Late synthetic output")
        do {
            _ = try await task.value
            XCTFail("Post-operation cancellation check must reject late output")
        } catch {
            XCTAssertTrue(error is CancellationError)
        }
    }
}
