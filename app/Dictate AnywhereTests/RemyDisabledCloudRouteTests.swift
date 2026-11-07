import Foundation
import XCTest
@testable import Dictate_Anywhere

@MainActor
final class RemyDisabledCloudRouteTests: XCTestCase {
    func testDisabledCloudEngineNeverReportsReady() {
        // isReady must short-circuit before looking up any configured key.
        XCTAssertFalse(AssemblyAIEngine().isReady)
    }

    func testPrepareRejectsDisabledRouteBeforeCredentialRead() async {
        let engine = AssemblyAIEngine()
        await assertUnavailable { try await engine.prepare() }
        XCTAssertFalse(engine.isReady)
    }

    func testRecordingRejectsDisabledRouteBeforeMicrophoneSetup() async {
        let engine = AssemblyAIEngine()
        await assertUnavailable { try await engine.startRecording(deviceID: nil) }
        XCTAssertFalse(engine.isReady)
        XCTAssertTrue(engine.audioSamples.isEmpty)
        XCTAssertTrue(engine.currentTranscript.isEmpty)
    }

    func testFileTranscriptionRejectsDisabledRouteBeforeOpeningMissingFile() async {
        let engine = AssemblyAIEngine()
        let missing = URL(fileURLWithPath: "/remy-disabled-cloud-test-\(UUID().uuidString)/missing.wav")
        // A missing fixture would throw a file error if file reading preceded
        // policy enforcement. No file, audio capture, credential or server is used.
        await assertUnavailable { _ = try await engine.transcribeRecording(at: missing) }
        XCTAssertTrue(engine.currentTranscript.isEmpty)
    }

    func testErrorStatusDiagnosticLabelOmitsAssociatedPayload() {
        let status = AppState.DictationStatus.error("synthetic transcript and credential sentinel")
        XCTAssertEqual(status.diagnosticLabel, "error")
    }

    private func assertUnavailable(
        file: StaticString = #filePath, line: UInt = #line,
        operation: () async throws -> Void
    ) async {
        do {
            try await operation()
            XCTFail("Disabled cloud speech route unexpectedly succeeded", file: file, line: line)
        } catch {
            XCTAssertEqual(error as? AssemblyAIEngineError, .unavailableInRemy,
                           "Expected policy rejection before route work", file: file, line: line)
        }
    }
}
