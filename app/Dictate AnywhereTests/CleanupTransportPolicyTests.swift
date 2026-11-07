import XCTest
@testable import Dictate_Anywhere

final class CleanupTransportPolicyTests: XCTestCase {
    func testCrossOriginAndSameOriginRedirectsAreBothRejected() throws {
        let base = try XCTUnwrap(URL(string: "https://ollama.com/v1/chat/completions"))
        for destination in ["https://other.example/v1", "http://ollama.com/v1", "https://ollama.com:8443/v1"] {
            XCTAssertEqual(CleanupTransportPolicy.redirect(from: base, to: try XCTUnwrap(URL(string: destination))), .rejectCrossOrigin)
        }
        XCTAssertEqual(CleanupTransportPolicy.redirect(from: base,
            to: try XCTUnwrap(URL(string: "https://OLLAMA.com:443/another-provider"))), .rejectRedirect)
    }
    func testFailureClassificationContainsNoResponseBodyOrCredential() {
        XCTAssertNil(CleanupTransportPolicy.failure(statusCode: 200))
        XCTAssertEqual(CleanupTransportPolicy.failure(statusCode: 401), .authentication)
        XCTAssertEqual(CleanupTransportPolicy.failure(statusCode: 403), .authentication)
        XCTAssertEqual(CleanupTransportPolicy.failure(statusCode: 429), .rateLimited)
        XCTAssertEqual(CleanupTransportPolicy.failure(statusCode: 503), .server)
        XCTAssertEqual(CleanupTransportPolicy.failure(statusCode: 302), .rejectedStatus)
        XCTAssertEqual(CleanupTransportPolicy.failure(statusCode: 404), .rejectedStatus)
        XCTAssertEqual(CleanupTransportPolicy.failure(statusCode: -1), .invalidResponse)
    }
}
