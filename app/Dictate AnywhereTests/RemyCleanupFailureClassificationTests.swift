import Foundation
import XCTest
@testable import Dictate_Anywhere

@MainActor final class RemyCleanupFailureClassificationTests: XCTestCase {
    func testLateProviderErrorCannotReplaceUseRawMetadata() async {
        let clock = CleanupAttemptClock()
        let gate = CleanupAttemptWorker()
        let before = Date()
        let attempt = RemyCleanupAttempt(token: 1, correctedRaw: "raw", now: { clock.now },
            sleep: { try await clock.sleep($0) }, operation: {
                _ = await gate.run()
                throw OpenAICompatiblePostProcessingService.ServiceError.unexpectedStatus(401)
            })
        XCTAssertGreaterThanOrEqual(attempt.startedAt, before)
        XCTAssertLessThanOrEqual(attempt.startedAt, Date())
        XCTAssertNil(attempt.durationMilliseconds)
        await gate.waitUntilStarted()
        clock.now += .milliseconds(300)
        XCTAssertTrue(attempt.useRawNow())
        clock.now += .seconds(20)
        gate.finish("ignored")
        await attempt.joinWorker()
        XCTAssertEqual(attempt.failureCategory, .useRaw)
        XCTAssertEqual(attempt.durationMilliseconds, 300)
        XCTAssertEqual(attempt.decisionSequence, 1)
    }

    func testKnownFailureCategoriesDiscardPrivateErrorDetails() {
        let secret = "private-provider-body credential=synthetic-secret"
        let errors: [(Error, RemyCleanupFailure)] = [
            (URLError(.timedOut, userInfo: [NSLocalizedDescriptionKey: secret]), .timeout),
            (URLError(.cannotConnectToHost, userInfo: [NSURLErrorFailingURLStringErrorKey: secret]), .transport),
            (CancellationError(), .cancelled),
            (RemoteCleanupInputPolicyError.singleRequestBudgetExceeded, .requestBudgetExceeded),
            (CleanupResponseError.inputCannotFit, .inputTooLong),
            (CleanupResponseError.incompleteResponse, .incompleteResponse),
            (CleanupAnswerParseError.incomplete, .incompleteResponse),
            (CleanupAnswerParseError.refused, .requestRejected),
            (CleanupAnswerParseError.answerTooLarge, .responseTooLarge),
            (CleanupAnswerParseError.malformed, .invalidResponse),
            (CleanupBoundedHTTPTransport.TransportError.responseTooLarge, .responseTooLarge),
            (CleanupBoundedHTTPTransport.TransportError.crossOriginRedirect, .invalidConfiguration),
            (RemyCleanupRuntime.RuntimeError.profileCredentialUnavailable, .authentication),
            (RemyCleanupRuntime.RuntimeError.missingValidatedS1Model, .modelUnavailable),
            (RemyCleanupRuntime.RuntimeError.invalidProfile, .invalidConfiguration),
            (OpenAICompatiblePostProcessingService.ServiceError.unexpectedStatus(401), .authentication),
            (OpenRouterPostProcessingService.ServiceError.unexpectedStatus(403), .authentication),
            (OllamaPostProcessingService.ServiceError.unexpectedStatus(429), .rateLimited),
            (OpenAICompatiblePostProcessingService.ServiceError.unexpectedStatus(503), .serverUnavailable),
            (OpenRouterPostProcessingService.ServiceError.unexpectedStatus(504), .timeout),
            (OpenAICompatiblePostProcessingService.ServiceError.unexpectedStatus(400), .requestRejected),
            (OpenAICompatiblePostProcessingService.ServiceError.serverMessage(secret), .providerFailure),
            (OpenRouterPostProcessingService.ServiceError.missingAPIKey(secret), .authentication),
            (OllamaPostProcessingService.ServiceError.serverMessage(secret), .providerFailure),
            (S1MiniServiceError.outputLimitReached, .incompleteResponse),
            (S1MiniServiceError.transcriptTooLong(actual: 7, maximum: 1), .inputTooLong),
            (NSError(domain: secret, code: 503, userInfo: [NSLocalizedDescriptionKey: secret]), .providerFailure)
        ]
        for (error, expected) in errors {
            let category = RemyCleanupFailure.classify(error)
            XCTAssertEqual(category, expected)
            XCTAssertFalse(category.message.contains(secret))
            XCTAssertFalse(category.overlayTitle.contains(secret))
        }
        for category in RemyCleanupFailure.allCases {
            XCTAssertEqual(RemyCleanupFailure.classify(category), category)
            XCTAssertEqual(RemyCleanupFailure.message(for: category.rawValue), category.message)
        }
        XCTAssertEqual(RemyCleanupFailure.message(for: secret), RemyCleanupFailure.message(for: nil))
        XCTAssertFalse(RemyCleanupFailure.message(for: secret).contains(secret))
    }
}
