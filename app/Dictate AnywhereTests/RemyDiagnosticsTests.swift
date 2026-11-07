import Foundation
import XCTest
@testable import Dictate_Anywhere

final class RemyDiagnosticsTests: XCTestCase {
    func testSubsystemPreservesIndependentBundleIdentitiesAndUsesRemyFallback() {
        XCTAssertEqual(RemyDiagnostics.subsystem(for: nil), "io.github.brysonjl.remy")
        XCTAssertEqual(RemyDiagnostics.subsystem(for: ""), "io.github.brysonjl.remy")
        XCTAssertEqual(RemyDiagnostics.subsystem(for: "io.github.brysonjl.remy.dev"), "io.github.brysonjl.remy.dev")
        XCTAssertEqual(RemyDiagnostics.subsystem(for: "io.github.brysonjl.remy"), "io.github.brysonjl.remy")
    }

    func testCancellationAndTransportClassificationNeverIncludesErrorUserInfo() {
        let privatePayload = "synthetic-transcript-and-credential-sentinel"
        XCTAssertEqual(RemyDiagnostics.errorCategory(CancellationError()), "cancelled")
        let cases: [(URLError.Code, String)] = [(.cancelled, "cancelled"), (.timedOut, "timeout"), (.cannotConnectToHost, "transport")]
        for (code, category) in cases {
            let error = URLError(code, userInfo: [NSLocalizedDescriptionKey: privatePayload,
                                                 NSURLErrorFailingURLStringErrorKey: "https://example.invalid/" + privatePayload])
            XCTAssertEqual(RemyDiagnostics.errorCategory(error), category)
        }
    }

    func testUnknownProviderAndSystemPayloadsProduceOnlyStaticOtherCategory() {
        let sentinel = "synthetic-provider-response-transcript-credential"
        let errors: [Error] = [
            DiagnosticFixtureError(message: sentinel),
            NSError(domain: sentinel, code: 123, userInfo: [NSLocalizedDescriptionKey: sentinel]),
            NSError(domain: NSCocoaErrorDomain, code: NSFileReadNoPermissionError,
                    userInfo: [NSFilePathErrorKey: "/synthetic/" + sentinel]),
        ]
        for error in errors {
            XCTAssertEqual(RemyDiagnostics.errorCategory(error), "other")
        }
    }
}

private struct DiagnosticFixtureError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}
