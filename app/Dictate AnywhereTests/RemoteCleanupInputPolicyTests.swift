import Foundation
import os
import XCTest
@testable import Dictate_Anywhere

@MainActor final class RemoteCleanupInputPolicyTests: XCTestCase {
    private func configuration(mode: TranscriptPostProcessingMode, prompt: String = "Correct punctuation.",
                               vocabulary: [String] = []) -> RemyDictationConfiguration {
        .init(capture: .init(vocabulary: vocabulary), cleanup: .init(mode: mode, prompt: prompt))
    }
    func testEveryFrozenRemoteModeChunksLongSourceWithoutLosingContent() async throws {
        let source = (1...120).map { "Keep invoice \($0) unchanged and send the report tomorrow.\n\n" }.joined()
        for mode: TranscriptPostProcessingMode in [.ollama, .openRouter, .openAICompatible, .profile] {
            let calls = OSAllocatedUnfairLock(initialState: [String]())
            let instructions = remotePostProcessingInstructions(prompt: "Correct punctuation.", vocabulary: [])
            let runtime = RemyCleanupRuntime(configuration: configuration(mode: mode)) { text in
                try await RemoteCleanupProcessing.process(text: text, instructions: instructions,
                    vocabulary: [], context: nil) { candidate in
                    let assessment = try await RemoteCleanupProcessing.assessSingleRequest(text: candidate,
                        instructions: instructions, vocabulary: [], context: nil)
                    XCTAssertLessThanOrEqual(assessment.estimatedRequestAndOutputBudget, 8_192)
                    calls.withLock { $0.append(candidate) }
                    let data = try JSONSerialization.data(withJSONObject: ["action": "pasteCleanedText", "text": candidate])
                    return String(decoding: data, as: UTF8.self)
                }
            }
            let result = try await runtime.process(text: source)
            XCTAssertEqual(result, source, mode.rawValue)
            XCTAssertGreaterThan(calls.withLock { $0.count }, 1, mode.rawValue)
            XCTAssertEqual(runtime.remoteInputPolicy, .boundedChunking(conservativeContextBudget: 8_192))
        }
        XCTAssertEqual(RemoteCleanupProcessing.inputPolicy, .legacyChunking)
    }

    func testTwoMinuteDictationWithRealInstructionsAndAppContextFitsInBoundedRequests() async throws {
        // Synthetic ordinary dictation, similar in size to the reported failure.
        // Use the production instruction builder, not a tiny placeholder prompt.
        let source = """
        I want to update the plan for next week. Please move the design review from Thursday to Friday, and keep the meeting at three in the afternoon. Maya should receive the revised notes before lunch. Do not change the budget of fifteen thousand dollars, and leave the existing contract dates alone. We still need to check the new recording flow before we tell anyone that the release is ready.

        The first thing I noticed was that a short recording worked correctly, but a longer explanation stopped during cleanup. The words were still available in history, which was helpful, although the warning made it sound as if I had spoken for too long. A couple of minutes should be a normal amount of speech for this workflow. Please investigate the limit and make sure the complete transcript survives every step, including punctuation and the paragraph breaks.

        For the next check, use a realistic message with names, numbers, and a correction. Keep the final version of the correction, but do not summarize the surrounding explanation or turn a question into an answer. If any part of cleanup fails, preserve the original text so I can recover it. We should also verify cancellation while work is in progress and make sure a late response cannot replace the result after I have already chosen to use the raw transcript. Once those checks pass, write down what changed and what still needs to be tested with the actual provider.
        """
        XCTAssertTrue((1_300...1_700).contains(source.utf8.count))
        let prompt = Settings.recommendedTranscriptCleanupPrompt + "\n" +
            "Preserve names, numbers, negation, dates, units, paths, and identifiers. Never summarize or answer the transcript. Keep uncertain wording unchanged. Retain every distinct point and preserve the speaker's level of detail. Return only the complete cleaned transcript in the required output format."
        let captured = DictationContext(processIdentifier: 7, bundleIdentifier: "fixture.editor", appName: "Fixture Editor",
            category: .codingAgent, documentURL: nil, documentTitle: nil,
            fieldRole: "AXTextArea", fieldSubrole: nil, fieldPurpose: .unknown,
            textBeforeCursor: nil, selectedText: nil, textAfterCursor: nil,
            isSecureField: false, isContextExcluded: false)
        let config = RemyDictationConfiguration(capture: .init(), cleanup: .init(mode: .openAICompatible,
            prompt: prompt, baseURL: "https://fixture.test/v1",
            contextPolicy: .init(isEnabled: true)))
        let calls = OSAllocatedUnfairLock(initialState: 0)
        let runtime = RemyCleanupRuntime(configuration: config, contextualOperation: { text, context in
            XCTAssertEqual(context?.category, .codingAgent)
            let instructions = remotePostProcessingInstructions(prompt: prompt, vocabulary: [], context: context)
            XCTAssertThrowsError(try RemoteCleanupProcessing.assessSingleRequest(text: text,
                instructions: instructions, vocabulary: [], context: context), "This fixture must reproduce the previous rejection")
            return try await RemoteCleanupProcessing.process(text: text, instructions: instructions,
                vocabulary: [], context: context) { candidate in
                let assessment = try await RemoteCleanupProcessing.assessSingleRequest(text: candidate,
                    instructions: instructions, vocabulary: [], context: context)
                XCTAssertLessThanOrEqual(assessment.estimatedRequestAndOutputBudget, 8_192)
                calls.withLock { $0 += 1 }
                let data = try JSONSerialization.data(withJSONObject: ["action": "pasteCleanedText", "text": candidate])
                return String(decoding: data, as: UTF8.self)
            }
        })
        let output = try await runtime.process(text: source, context: captured)
        XCTAssertEqual(output, source)
        XCTAssertGreaterThan(calls.withLock { $0 }, 1)
    }

    func testUnbrokenOversizedSegmentFailsBeforeProviderOperation() async {
        var calls = 0
        let runtime = RemyCleanupRuntime(configuration: configuration(mode: .openAICompatible)) { _ in
            calls += 1; return "never called"
        }
        do {
            _ = try await runtime.process(text: String(repeating: "identifier", count: 2_000))
            XCTFail("An identifier must not be truncated or divided")
        } catch {
            guard case CleanupResponseError.inputCannotFit = error else {
                return XCTFail("Unexpected failure: \(type(of: error))")
            }
        }
        XCTAssertEqual(calls, 0)
    }

    func testBoundedPlanHonorsSmallerContextAndCompletionLimits() async throws {
        let source = (1...30).map { "Preserve record \($0). " }.joined()
        let chunks = try await RemoteCleanupProcessing.boundedChunks(text: source, instructions: "Correct punctuation.",
            vocabulary: [], context: nil, conservativeContextBudget: 8_192, contextLength: 1_500,
            maximumCompletionTokens: 220)
        XCTAssertEqual(chunks.map(\.original).joined(), source)
        XCTAssertGreaterThan(chunks.count, 1)
        for chunk in chunks {
            let assessment = try RemoteCleanupProcessing.assessSingleRequest(text: chunk.original,
                instructions: "Correct punctuation.", vocabulary: [], context: nil,
                contextLength: 1_500, maximumCompletionTokens: 220)
            XCTAssertLessThanOrEqual(assessment.estimatedRequestAndOutputBudget, 1_500)
        }
    }

    func testPromptAndVocabularyAreIncludedInPreflightNotOnlySourceLength() async {
        for config in [configuration(mode: .openAICompatible, prompt: String(repeating: "instruction ", count: 1_000)),
                       configuration(mode: .ollama, vocabulary: [String(repeating: "term", count: 3_000)])] {
            var calls = 0
            let runtime = RemyCleanupRuntime(configuration: config) { _ in calls += 1; return "never called" }
            do { _ = try await runtime.process(text: "short"); XCTFail("Oversized instructions accepted") }
            catch { XCTAssertEqual(error as? RemoteCleanupInputPolicyError, .singleRequestBudgetExceeded) }
            XCTAssertEqual(calls, 0)
        }
    }
    func testShortRuntimeInputPreservesParagraphsInOneRequestAndScopeDoesNotLeak() async throws {
        let source = "  First paragraph.\n\nSecond paragraph.\n\nThird.\n"
        let calls = OSAllocatedUnfairLock(initialState: [String]())
        let runtime = RemyCleanupRuntime(configuration: configuration(mode: .openAICompatible)) { text in
            try await RemoteCleanupProcessing.process(text: text, instructions: "Correct punctuation.",
                vocabulary: [], context: nil, maximumConcurrentRequests: 2) { candidate in
                calls.withLock { $0.append(candidate) }
                let data = try JSONSerialization.data(withJSONObject: ["action": "pasteCleanedText", "text": candidate])
                return String(decoding: data, as: UTF8.self)
            }
        }
        let result = try await runtime.process(text: source)
        XCTAssertEqual(result, source)
        XCTAssertEqual(calls.withLock { $0 }, [source.trimmingCharacters(in: .whitespacesAndNewlines)])
        XCTAssertEqual(RemoteCleanupProcessing.inputPolicy, .legacyChunking)
    }
    func testSmallerReportedContextOrCompletionLimitRejectsWithoutGenerate() async {
        for policy: RemoteCleanupInputPolicy in [.singleRequestOnly(conservativeContextBudget: 8_192),
                                                .boundedChunking(conservativeContextBudget: 8_192)] {
            for (context, completion) in [(500, Optional<Int>.none), (8_192, Optional(1))] {
                let calls = OSAllocatedUnfairLock(initialState: 0)
                do {
                    _ = try await RemoteCleanupProcessing.$inputPolicy.withValue(policy) {
                        try await RemoteCleanupProcessing.process(text: "small source", instructions: "Correct punctuation.",
                            vocabulary: [], context: nil, contextLength: context, maximumCompletionTokens: completion) { _ in
                            calls.withLock { $0 += 1 }; return "never called"
                        }
                    }
                    XCTFail("Unsupported request budget accepted")
                } catch { XCTAssertEqual(error as? RemoteCleanupInputPolicyError, .singleRequestBudgetExceeded) }
                XCTAssertEqual(calls.withLock { $0 }, 0)
            }
        }
    }
    func testExplicitSingleRequestPolicyStillRejectsOversizedSourceWithoutGeneration() async {
        let calls = OSAllocatedUnfairLock(initialState: 0)
        do {
            _ = try await RemoteCleanupProcessing.$inputPolicy.withValue(.singleRequestOnly(conservativeContextBudget: 8_192)) {
                try await RemoteCleanupProcessing.process(text: String(repeating: "source ", count: 2_000),
                    instructions: "Correct punctuation.", vocabulary: [], context: nil) { _ in
                    calls.withLock { $0 += 1 }; return "never called"
                }
            }
            XCTFail("Explicit single-request callers must not acquire chunking")
        } catch {
            XCTAssertEqual(error as? RemoteCleanupInputPolicyError, .singleRequestBudgetExceeded)
        }
        XCTAssertEqual(calls.withLock { $0 }, 0)
        XCTAssertEqual(RemoteCleanupProcessing.inputPolicy, .legacyChunking)
    }

    func testUnknownContextAssessmentIsExplicitEstimateAndCannotExpandCeiling() throws {
        let assessment = try RemoteCleanupProcessing.assessSingleRequest(text: "hello", instructions: "Correct punctuation.",
            vocabulary: [], context: nil, conservativeContextBudget: 100_000, contextLength: 100_000)
        XCTAssertEqual(assessment.validation, .conservativeByteEstimate)
        XCTAssertEqual(assessment.contextBudget, 8_192)
        XCTAssertEqual(assessment.sourceBytes, 5)
        XCTAssertGreaterThan(assessment.estimatedRequestAndOutputBudget, assessment.sourceBytes)
        XCTAssertThrowsError(try RemoteCleanupProcessing.assessSingleRequest(text: "hello", instructions: "",
            vocabulary: [], context: nil, conservativeContextBudget: 0))
    }
    func testLocalRuntimeDoesNotAcquireRemoteInputRestriction() async throws {
        let source = String(repeating: "retained raw ", count: 1_000)
        let runtime = RemyCleanupRuntime(configuration: configuration(mode: .none)) { $0 }
        let result = try await runtime.process(text: source)
        XCTAssertEqual(result, source)
        XCTAssertEqual(runtime.remoteInputPolicy, .legacyChunking)
    }
}
