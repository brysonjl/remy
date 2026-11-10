import ApplicationServices
import XCTest
@testable import Dictate_Anywhere

// MARK: - Term extraction

final class ScreenNameTermsTests: XCTestCase {
    func testExtractsPeopleFromMailAndChatText() {
        let terms = ScreenNameTerms.extract(from: [
            "Siobhan Delacroix-Ng <siobhan@example.com>",
            "Thanks Priya, see you Monday.",
            "Meeting notes from Joaquín Ortega",
            "Ludwig van Beethoven",
        ])
        XCTAssertTrue(terms.contains("Siobhan Delacroix-Ng"), "\(terms)")
        XCTAssertTrue(terms.contains("Priya"), "\(terms)")
        XCTAssertTrue(terms.contains("Joaquín Ortega"), "\(terms)")
        XCTAssertTrue(terms.contains("Ludwig van Beethoven"), "\(terms)")
        for absent in ["Monday", "Thanks", "Meeting", "siobhan@example.com", "Siobhan"] {
            XCTAssertFalse(terms.contains(absent), "\(absent) in \(terms)")
        }
        XCTAssertFalse(terms.contains { $0.contains("@") })
    }

    func testExtractsCodeIdentifiersAndDottedMembers() {
        let terms = ScreenNameTerms.extract(from: [
            "let profile = fetchUserProfile(for: account_id)",
            "self.viewModel.refreshToken()",
            "https://example.com/fooBar",
        ])
        XCTAssertEqual(Set(terms), ["fetchUserProfile", "account_id", "viewModel", "refreshToken"])
    }

    func testStoplistAndSentenceInitialCommonWordsAreDropped() {
        XCTAssertEqual(ScreenNameTerms.extract(from: [
            "Inbox", "Reply All", "Settings", "Monday March 3", "Archive", "Looking forward to it.", "Sounds good.",
        ]), [])
        XCTAssertEqual(ScreenNameTerms.extract(from: ["Looking forward. Talk to Anneliese tomorrow"]), ["Anneliese"])
    }

    func testRepeatedSenderLabelsSurviveAndRankAfterFullNames() {
        let terms = ScreenNameTerms.extract(from: ["Priya", "Hello there", "Priya", "Okonkwo Adeyemi"])
        XCTAssertEqual(terms, ["Okonkwo Adeyemi", "Priya"])
    }

    func testCapsAtTwentyFiveAndDeduplicatesCaseInsensitively() {
        let identifiers = (0..<60).map { "valueNumber\($0)x" }
        XCTAssertEqual(ScreenNameTerms.extract(from: identifiers).count, ScreenNameTerms.maximumTerms)
        XCTAssertEqual(ScreenNameTerms.extract(from: ["fetchUser", "FETCHUSER fetchuser fetchUser"]), ["fetchUser"])
    }

    func testMergesWithFocusedFieldHintsWithoutDuplicates() {
        let context = DictationContext(
            processIdentifier: 1, bundleIdentifier: "com.apple.mail", appName: "Mail", category: .email,
            documentURL: nil, documentTitle: nil, fieldRole: "AXTextArea", fieldSubrole: nil, fieldPurpose: .unknown,
            textBeforeCursor: "Hi Zephyrine, ", selectedText: "", textAfterCursor: "",
            isSecureField: false, isContextExcluded: false)
        let screen = ScreenNameTerms.extract(from: ["zephyrine", "Thanks Zephyrine, from Okonkwo Adeyemi"])
        let merged = ScreenNameTerms.merged(context.lexicalHints, screen)
        XCTAssertEqual(merged.filter { $0.lowercased() == "zephyrine" }.count, 1)
        XCTAssertTrue(merged.contains("Okonkwo Adeyemi"))
        XCTAssertEqual(Array(merged.prefix(context.lexicalHints.count)), context.lexicalHints)
    }
}

// MARK: - Walk caps

nonisolated private final class FakeAccessibility: @unchecked Sendable {
    var now = ContinuousClock.now
    var step: Duration = .zero
    var timeouts: [Float] = []
    var reads = 0
}

nonisolated private struct FakeNode: ScreenNameNode {
    let accessibility: FakeAccessibility
    var subrole: String? = nil
    var texts: [String] = []
    var children: [FakeNode] = []

    func read(messagingTimeout: Float) -> ScreenNameNodeSnapshot<FakeNode>? {
        accessibility.reads += 1
        accessibility.timeouts.append(messagingTimeout)
        accessibility.now += accessibility.step
        return ScreenNameNodeSnapshot(subrole: subrole, texts: texts, children: children)
    }
}

final class ScreenNameWalkTests: XCTestCase {
    private func walk(_ root: FakeNode, cancelled: @escaping () -> Bool = { false }) -> ScreenNameWalk.Result {
        ScreenNameWalk.walk(root: root, now: { root.accessibility.now }, isCancelled: cancelled)
    }

    func testStopsAtNodeCap() {
        let ax = FakeAccessibility()
        let root = FakeNode(accessibility: ax, children: (0..<1_000).map { FakeNode(accessibility: ax, texts: ["Name \($0)"]) })
        let result = walk(root)
        XCTAssertEqual(result.nodes, 400)
        XCTAssertEqual(ax.reads, 400)
        XCTAssertFalse(result.budgetHit)
    }

    func testStopsAtDepthCap() {
        let ax = FakeAccessibility()
        var node = FakeNode(accessibility: ax, texts: ["Leaf"])
        for _ in 0..<100 { node = FakeNode(accessibility: ax, children: [node]) }
        let result = walk(node)
        XCTAssertEqual(result.nodes, 41, "Depths 0 through 40 are read")
        XCTAssertFalse(result.budgetHit)
    }

    func testStopsAtTimeBudgetAndNeverGrantsMoreThanTheRemainingTime() {
        let ax = FakeAccessibility()
        ax.step = .milliseconds(10)
        let root = FakeNode(accessibility: ax, children: (0..<50).map { _ in FakeNode(accessibility: ax) })
        let result = walk(root)
        XCTAssertTrue(result.budgetHit)
        XCTAssertEqual(result.nodes, 8)
        XCTAssertLessThanOrEqual(result.elapsed, .milliseconds(80))
        XCTAssertEqual(ax.timeouts.first, 0.05)
        XCTAssertEqual(Double(ax.timeouts.last ?? 1), 0.005, accuracy: 0.0005, "The last read is capped by the remaining budget")
        XCTAssertTrue(ax.timeouts.allSatisfy { $0 <= 0.05 })
    }

    func testSkipsSecureFieldsAndTheirSubtrees() {
        let ax = FakeAccessibility()
        let secure = FakeNode(accessibility: ax, subrole: kAXSecureTextFieldSubrole as String, texts: ["Hunter Secret"],
            children: [FakeNode(accessibility: ax, texts: ["Inner Hidden"])])
        let root = FakeNode(accessibility: ax, children: [secure, FakeNode(accessibility: ax, texts: ["Visible Person"])])
        let result = walk(root)
        XCTAssertEqual(result.texts, ["Visible Person"])
        XCTAssertEqual(result.nodes, 3, "The secure field's child is never read")
    }

    func testBoundsEachValueAndDeduplicates() {
        let ax = FakeAccessibility()
        let long = String(repeating: "a", count: 2_000)
        let root = FakeNode(accessibility: ax, texts: [long, "  ", "Same"],
            children: [FakeNode(accessibility: ax, texts: ["Same"])])
        let result = walk(root)
        XCTAssertEqual(result.texts.map(\.count), [400, 4])
    }

    func testCancellationStopsTheWalk() {
        let ax = FakeAccessibility()
        let root = FakeNode(accessibility: ax, children: (0..<10).map { _ in FakeNode(accessibility: ax) })
        let result = walk(root, cancelled: { ax.reads >= 3 })
        XCTAssertEqual(result.nodes, 3)
    }

    func testLoggedCountsComeFromTheWalk() {
        let ax = FakeAccessibility()
        let root = FakeNode(accessibility: ax, texts: ["Okonkwo Adeyemi"])
        XCTAssertEqual(ScreenNameCapture.terms(walking: root, trace: PerfTrace.begin("dictation.screenNames")),
                       ["Okonkwo Adeyemi"])
    }
}

// MARK: - Gating, prompt assembly and snapshot

@MainActor
final class ScreenNamePolicyTests: XCTestCase {
    private func configuration(
        engine: TranscriptionEngineChoice = .parakeet, mode: TranscriptPostProcessingMode = .none,
        enabled: Bool = true, awareness: Bool = true, share: Bool = false,
        baseURL: String = "", model: String = ""
    ) -> RemyDictationConfiguration {
        .init(capture: .init(engine: engine, spellNamesFromActiveWindow: enabled),
              cleanup: .init(mode: mode, baseURL: baseURL, model: model,
                  contextPolicy: .init(isEnabled: awareness, shareCapturedTextRemotely: share)))
    }

    private func context(category: DictationContextCategory = .email, bundle: String = "com.apple.mail",
                         secure: Bool = false, excluded: Bool = false) -> DictationContext {
        DictationContext(processIdentifier: 7, bundleIdentifier: bundle, appName: "App", category: category,
            documentURL: nil, documentTitle: nil, fieldRole: "AXTextArea", fieldSubrole: nil, fieldPurpose: .unknown,
            textBeforeCursor: "", selectedText: "", textAfterCursor: "",
            isSecureField: secure, isContextExcluded: excluded)
    }

    func testCaptureRequiresSettingAwarenessAndAConsumer() {
        XCTAssertTrue(ScreenNamePolicy.shouldCapture(configuration(engine: .appleSpeech)))
        XCTAssertFalse(ScreenNamePolicy.shouldCapture(configuration(engine: .appleSpeech, enabled: false)))
        XCTAssertFalse(ScreenNamePolicy.shouldCapture(configuration(engine: .appleSpeech, awareness: false)))
        XCTAssertFalse(ScreenNamePolicy.shouldCapture(configuration(engine: .parakeet, mode: .none)))
    }

    func testConsumersMatchWhereNamesCanBeUsed() {
        XCTAssertTrue(ScreenNamePolicy.hasConsumer(configuration(engine: .appleSpeech, mode: .none)))
        XCTAssertFalse(ScreenNamePolicy.hasConsumer(configuration(engine: .assemblyAI, mode: .openRouter, share: true)))
        XCTAssertFalse(ScreenNamePolicy.hasConsumer(configuration(mode: .fluidAudioVocabulary)))
        XCTAssertFalse(ScreenNamePolicy.hasConsumer(configuration(mode: .s1Mini)), "S1-mini takes only a category")
        XCTAssertTrue(ScreenNamePolicy.hasConsumer(configuration(mode: .appleIntelligence)))
        XCTAssertTrue(ScreenNamePolicy.hasConsumer(configuration(mode: .ollama, baseURL: "http://127.0.0.1:11434", model: "m")))
        XCTAssertFalse(ScreenNamePolicy.hasConsumer(configuration(mode: .ollama, baseURL: "https://ollama.com", model: "m")))
        XCTAssertFalse(ScreenNamePolicy.hasConsumer(configuration(mode: .openRouter)))
        XCTAssertTrue(ScreenNamePolicy.hasConsumer(configuration(mode: .openRouter, share: true)))
    }

    func testWalkIsLimitedToAllowedUnprotectedDestinations() {
        for category in DictationContextCategory.allCases {
            XCTAssertEqual(ScreenNamePolicy.allowsWalk(in: context(category: category)),
                [.email, .workMessaging, .personalMessaging, .codingAgent, .aiChat].contains(category), "\(category)")
        }
        XCTAssertFalse(ScreenNamePolicy.allowsWalk(in: context(secure: true)))
        XCTAssertFalse(ScreenNamePolicy.allowsWalk(in: context(excluded: true)))
        XCTAssertFalse(ScreenNamePolicy.allowsWalk(in: context(bundle: "com.1password.1password")))
    }

    func testLocalRoutesReceiveNamesAndRemoteRoutesOnlyWithOptIn() {
        let names = ["Okonkwo Adeyemi"]
        let local = RemyCleanupRuntime(configuration: configuration(mode: .ollama, baseURL: "http://localhost:11434", model: "m")) { $0 }
        XCTAssertEqual(local.postProcessingContext(from: context(), screenNames: names)?.screenNames, names)
        let apple = RemyCleanupRuntime(configuration: configuration(mode: .appleIntelligence)) { $0 }
        XCTAssertEqual(apple.postProcessingContext(from: context(), screenNames: names)?.screenNames, names)
        for mode in [TranscriptPostProcessingMode.openRouter, .ollama, .openAICompatible] {
            let remote = RemyCleanupRuntime(configuration: configuration(mode: mode, baseURL: "https://example.com/v1", model: "m")) { $0 }
            XCTAssertEqual(remote.postProcessingContext(from: context(), screenNames: names)?.screenNames, [], "\(mode)")
            let shared = RemyCleanupRuntime(configuration: configuration(mode: mode, share: true,
                baseURL: "https://example.com/v1", model: "m")) { $0 }
            XCTAssertEqual(shared.postProcessingContext(from: context(), screenNames: names)?.screenNames, names, "\(mode)")
        }
        XCTAssertEqual(local.postProcessingContext(from: context(secure: true), screenNames: names)?.screenNames, [])
        let s1 = RemyCleanupRuntime(configuration: configuration(mode: .s1Mini)) { $0 }
        XCTAssertEqual(s1.postProcessingContext(from: context(), screenNames: names)?.screenNames, [])
    }

    func testRuntimeHandsNamesToTheProviderOperation() async throws {
        var received: [String]?
        let runtime = RemyCleanupRuntime(configuration: configuration(mode: .appleIntelligence),
            contextualOperation: { text, context in received = context?.screenNames; return text })
        _ = try await runtime.process(text: "hello", context: context(), screenNames: ["Okonkwo Adeyemi"])
        XCTAssertEqual(received, ["Okonkwo Adeyemi"])
    }

    func testRequestSectionIsSeparateFromKnownTermsAndInstructions() {
        var providerContext = context().postProcessingContext(style: .original, includeCapturedText: true)
        providerContext.screenNames = ["Okonkwo Adeyemi", "Priya", "a<b"]
        let request = remotePostProcessingRequestPrompt(text: "hello", vocabulary: ["priya"], context: providerContext)
        XCTAssertGreaterThan(request.range(of: "<screen_names>")!.lowerBound, request.range(of: "</transcript>")!.lowerBound)
        XCTAssertTrue(request.contains("Okonkwo Adeyemi"))
        XCTAssertTrue(request.contains("a&lt;b"))
        XCTAssertTrue(request.contains("may be unrelated"))
        XCTAssertFalse(request.contains("\nPriya\n"), "Custom vocabulary already lists it")
        let instructions = remotePostProcessingInstructions(prompt: "", vocabulary: ["priya"], context: providerContext)
        XCTAssertFalse(instructions.contains("Okonkwo"))
        XCTAssertFalse(instructions.contains("screen_names"))

        providerContext.screenNames = []
        XCTAssertFalse(remotePostProcessingRequestPrompt(text: "hello", vocabulary: [], context: providerContext)
            .contains("screen_names"))
    }

    func testAppleIntelligencePrewarmKeyIgnoresNames() throws {
        guard #available(macOS 26, *) else { throw XCTSkip("Requires Foundation Models") }
        var providerContext = context().postProcessingContext(style: .original, includeCapturedText: true)
        let plain = AIPostProcessingService.instructions(prompt: "", vocabulary: [], context: providerContext)
        providerContext.screenNames = ["Okonkwo Adeyemi"]
        XCTAssertEqual(AIPostProcessingService.instructions(prompt: "", vocabulary: [], context: providerContext), plain)
        let suffix = AIPostProcessingService.requestSuffix(vocabulary: [], context: providerContext)
        XCTAssertTrue(suffix.hasPrefix("</transcript>\n<screen_names>"))
        XCTAssertTrue(suffix.contains("Okonkwo Adeyemi"))
        XCTAssertEqual(AIPostProcessingService.requestSuffix(vocabulary: [], context: providerContext.withoutScreenNames),
                       "</transcript>")
    }

    func testSettingIsFrozenInTheSessionSnapshotAndNotMigrated() {
        let settings = Settings.shared
        let saved = settings.spellNamesFromActiveWindowEnabled
        defer { settings.spellNamesFromActiveWindowEnabled = saved }
        settings.spellNamesFromActiveWindowEnabled = true
        let captured = RemyCleanupRuntime.capture(settings: settings)
        settings.spellNamesFromActiveWindowEnabled = false
        XCTAssertTrue(captured.configuration.capture.spellNamesFromActiveWindow)
        XCTAssertFalse(RemyCleanupRuntime.capture(settings: settings).configuration.capture.spellNamesFromActiveWindow)
        XCTAssertFalse(RemyMigrationPreferences.migratableKeys.contains("spellNamesFromActiveWindowEnabled"))
    }
}
