import Foundation
import XCTest
@testable import Dictate_Anywhere

@MainActor final class RemyLongCaptureFailureTests: XCTestCase {
    private struct SyntheticFailure: Error { let privateMessage = "never-expose-synthetic-provider-body" }

    func testFailedFinalDecodeKeepsPreviewButExplicitlyRejectsSuccess() async {
        let engine = ParakeetEngine()
        let result = await engine.finalizeCapturedAudioForTesting(preview:"available incomplete preview") {
            throw SyntheticFailure()
        }
        XCTAssertEqual(result,"available incomplete preview")
        XCTAssertNotNil(engine.lastTranscriptionError)
        XCTAssertFalse(engine.lastTranscriptionError?.contains("never-expose") == true)
    }

    func testFailedFinalDecodeWithEmptyPreviewStillExposesFailure() async {
        let engine = ParakeetEngine()
        let result = await engine.finalizeCapturedAudioForTesting(preview:"") { throw SyntheticFailure() }
        XCTAssertTrue(result.isEmpty)
        XCTAssertNotNil(engine.lastTranscriptionError) // AppState must avoid the successful no-text discard path.
    }

    func testPendingOverflowRejectsBatchAndStreamingFinalizationBeforeDecode() async {
        for model in [ParakeetModelChoice.compactEnglish,.nemotronMultilingual] {
            let engine = ParakeetEngine()
            engine.captureSamplesForTesting([Float](repeating:0.05,count:16_000 * 120 + 1),model:model)
            var decoded = false
            let result = await engine.finalizeCapturedAudioForTesting(preview:"partial before overflow") {
                decoded = true
                return "must not be accepted as complete"
            }
            XCTAssertFalse(decoded)
            XCTAssertEqual(result,"partial before overflow")
            XCTAssertNotNil(engine.lastTranscriptionError)
            XCTAssertEqual(engine.capturedSampleCountsForTesting.pending,16_000 * 120)
        }
    }

    func testNewGenerationResetsFailureAndOverflowButFinalFailureSurvivesUntilReset() async {
        let engine = ParakeetEngine()
        engine.captureSamplesForTesting([Float](repeating:0,count:16_000 * 120 + 1),model:.compactEnglish)
        _ = await engine.finalizeCapturedAudioForTesting(preview:"old preview") { "old final" }
        XCTAssertNotNil(engine.lastTranscriptionError)
        engine.resetCaptureGenerationForTesting()
        XCTAssertNil(engine.lastTranscriptionError)
        XCTAssertEqual(engine.capturedSampleCountsForTesting.pending,0)
        let result = await engine.finalizeCapturedAudioForTesting(preview:"new preview") { "new complete final" }
        XCTAssertEqual(result,"new complete final")
        XCTAssertNil(engine.lastTranscriptionError)
    }

    func testEarlierIngestionFailureCannotBecomeSuccessfulNominalFinish() async {
        let engine = ParakeetEngine()
        engine.recordCaptureProcessingFailureForTesting(SyntheticFailure())
        var finalized = false
        let result = await engine.finalizeCapturedAudioForTesting(preview:"available partial") {
            finalized = true
            return "nominal finish must not conceal lost input"
        }
        XCTAssertFalse(finalized)
        XCTAssertEqual(result,"available partial")
        XCTAssertNotNil(engine.lastTranscriptionError)
        XCTAssertFalse(engine.lastTranscriptionError?.contains("never-expose") == true)
        engine.resetCaptureGenerationForTesting()
        let fresh = await engine.finalizeCapturedAudioForTesting(preview:"new preview") { "new complete session" }
        XCTAssertEqual(fresh,"new complete session")
        XCTAssertNil(engine.lastTranscriptionError)
    }

    func testFailureLatchedWhileFinalDecoderIsRunningRejectsNominalResult() async {
        let engine = ParakeetEngine()
        let result = await engine.finalizeCapturedAudioForTesting(preview:"partial retained") {
            engine.recordCaptureProcessingFailureForTesting(SyntheticFailure())
            return "incomplete final result"
        }
        XCTAssertEqual(result,"partial retained")
        XCTAssertNotNil(engine.lastTranscriptionError)
    }

    func testNormalStopLoopCancellationDoesNotPoisonCompletedRecognition() async {
        let engine = ParakeetEngine()
        let stoppedLoop = Task { engine.recordCaptureProcessingFailureForTesting(CancellationError()) }
        stoppedLoop.cancel()
        await stoppedLoop.value
        let result = await engine.finalizeCapturedAudioForTesting(preview:"preview") { "complete final" }
        XCTAssertEqual(result,"complete final")
        XCTAssertNil(engine.lastTranscriptionError)
    }

    func testUnexpectedDecoderCancellationWithoutStopIsAnExplicitFailure() async {
        let engine = ParakeetEngine()
        engine.recordCaptureProcessingFailureForTesting(CancellationError())
        let result = await engine.finalizeCapturedAudioForTesting(preview:"partial") { "nominal finish" }
        XCTAssertEqual(result,"partial")
        XCTAssertNotNil(engine.lastTranscriptionError)
    }

    func testCancellationRetainsNoncooperativeCompletedFinalForRecoveryOnly() async {
        let engine = ParakeetEngine()
        let gate = LongCaptureFinalGate(started:expectation(description:"fake final decoder started"))
        defer { gate.release() }
        let task = Task {
            await engine.finalizeCapturedAudioForTesting(preview:"preview retained") {
                await gate.wait()
                return "late decoder success"
            }
        }
        await fulfillment(of:[gate.started],timeout:2)
        task.cancel()
        gate.release()
        let result = await task.value
        XCTAssertEqual(result,"late decoder success")
        XCTAssertNil(engine.lastTranscriptionError)
        // This engine result grants no history or output authority. AppState's
        // cancellation/token regression tests verify only its recovery checkpoint survives.
    }
}

@MainActor private final class LongCaptureFinalGate {
    let started: XCTestExpectation
    private var continuation: CheckedContinuation<Void,Never>?
    private var released = false
    init(started:XCTestExpectation) { self.started = started }
    func wait() async {
        started.fulfill()
        if released { return }
        await withCheckedContinuation { continuation = $0 }
    }
    func release() { released = true; continuation?.resume(); continuation = nil }
}
