import Foundation
import os
import XCTest
@testable import Dictate_Anywhere

@MainActor final class RemoteCleanupRetryPolicyTests: XCTestCase {
    func testCompatibleFrozenPolicySendsOneGenerationOnSchemaRejection() async throws {
        try await verify(provider: .compatible)
    }
    func testOllamaFrozenPolicySendsOneGenerationOnSchemaRejection() async throws {
        try await verify(provider: .ollama)
    }
    func testOpenRouterFrozenPolicySendsOneGenerationOnSchemaRejection() async throws {
        try await verify(provider: .router)
    }
    private nonisolated enum Provider: Equatable, Sendable { case compatible, ollama, router }
    private func verify(provider: Provider) async throws {
        for policy: RemoteCleanupInputPolicy in [.singleRequestOnly(conservativeContextBudget: 8_192),
                                                  .boundedChunking(conservativeContextBudget: 8_192), .legacyChunking] {
            let config = URLSessionConfiguration.ephemeral
            config.protocolClasses = [RetryPolicyHTTPStub.self]
            let session = URLSession(configuration: config)
            defer { session.invalidateAndCancel(); RetryPolicyHTTPStub.handler.withLock { $0 = nil } }
            let generationCalls = OSAllocatedUnfairLock(initialState: 0)
            RetryPolicyHTTPStub.handler.withLock { $0 = { request in
                // Readiness metadata is not a transcript generation attempt.
                if request.url?.path.hasSuffix("models") == true {
                    return (200, Data(#"{"data":[]}"#.utf8))
                }
                let count = generationCalls.withLock { $0 += 1; return $0 }
                if count == 1 {
                    let body = provider == .ollama
                        ? #"{"error":"format is not supported for cloud models"}"#
                        : #"{"error":{"message":"response_format json_schema is not supported"}}"#
                    return (400, Data(body.utf8))
                }
                if provider == .ollama {
                    return (200, Data(#"{"done":true,"done_reason":"stop","response":"{\"action\":\"pasteCleanedText\",\"text\":\"Hello.\"}"}"#.utf8))
                }
                return (200, Data(#"{"choices":[{"finish_reason":"stop","message":{"content":"{\"action\":\"pasteCleanedText\",\"text\":\"Hello.\"}"}}]}"#.utf8))
            } }
            do {
                let output = try await RemoteCleanupProcessing.$inputPolicy.withValue(policy) {
                    switch provider {
                    case .compatible:
                        return try await OpenAICompatiblePostProcessingService.process(text: "hello", baseURL: "https://fixture.test/v1",
                            model: "fixture", apiKey: "synthetic", prompt: "Correct punctuation.", session: session)
                    case .ollama:
                        return try await OllamaPostProcessingService.process(text: "hello", baseURL: "https://fixture.test",
                            model: "fixture", prompt: "Correct punctuation.", session: session,
                            profileCapabilities: .init(transport: .completeJSON, discovery: false,
                                structuredOutput: true, thinkingControl: false))
                    case .router:
                        return try await OpenRouterPostProcessingService.process(text: "hello", model: "fixture",
                            prompt: "Correct punctuation.", apiKey: "synthetic", apiKeyEnvironmentVariable: "", session: session)
                    }
                }
                if policy != .legacyChunking { XCTFail("Frozen attempt silently retried rejected schema") }
                else { XCTAssertEqual(output, "Hello.") }
            } catch {
                if policy == .legacyChunking { XCTFail("Legacy adaptation unexpectedly failed: \(type(of: error))") }
            }
            XCTAssertEqual(generationCalls.withLock { $0 }, policy == .legacyChunking ? 2 : 1)
        }
    }
}

private final class RetryPolicyHTTPStub: URLProtocol, @unchecked Sendable {
    nonisolated static let handler = OSAllocatedUnfairLock<(@Sendable (URLRequest) throws -> (Int, Data))?>(initialState: nil)
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        do {
            let (status, data) = try XCTUnwrap(Self.handler.withLock { $0 })(request)
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: status,
                httpVersion: nil, headerFields: ["Content-Type": "application/json"])!, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {}
}
