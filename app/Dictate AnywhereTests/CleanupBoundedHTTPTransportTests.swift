import Foundation
import os
import XCTest
@testable import Dictate_Anywhere

private nonisolated final class BoundedHTTPStub: URLProtocol, @unchecked Sendable {
    enum Reply: Sendable { case body(Int, [String: String], [Data]), redirect(URL), hold }
    nonisolated static let handler = OSAllocatedUnfairLock<(@Sendable (URLRequest) throws -> Reply)?>(initialState: nil)
    nonisolated static let stopped = OSAllocatedUnfairLock<(@Sendable () -> Void)?>(initialState: nil)
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        do {
            let callback = Self.handler.withLock { $0 }
            let reply = try XCTUnwrap(callback)(request)
            switch reply {
            case .body(let status, let headers, let chunks):
                let response = try XCTUnwrap(HTTPURLResponse(url: try XCTUnwrap(request.url), statusCode: status,
                                                           httpVersion: nil, headerFields: headers))
                client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
                for chunk in chunks { client?.urlProtocol(self, didLoad: chunk) }
                client?.urlProtocolDidFinishLoading(self)
            case .redirect(let url):
                let response = try XCTUnwrap(HTTPURLResponse(url: try XCTUnwrap(request.url), statusCode: 302,
                    httpVersion: nil, headerFields: ["Location": url.absoluteString]))
                var redirected = URLRequest(url: url)
                // Deliberately hostile fake: the delegate must refuse even if a
                // redirect request proposes carrying the original auth header.
                redirected.setValue(request.value(forHTTPHeaderField: "Authorization"), forHTTPHeaderField: "Authorization")
                client?.urlProtocol(self, wasRedirectedTo: redirected, redirectResponse: response)
            case .hold: break
            }
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() { Self.stopped.withLock { $0 }?() }
}

@MainActor
final class CleanupBoundedHTTPTransportTests: XCTestCase {
    private var session: URLSession!
    override func setUp() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [BoundedHTTPStub.self]
        session = URLSession(configuration: configuration)
    }
    override func tearDown() {
        session.invalidateAndCancel()
        BoundedHTTPStub.handler.withLock { $0 = nil }
        BoundedHTTPStub.stopped.withLock { $0 = nil }
    }
    private func request() -> URLRequest { URLRequest(url: URL(string: "https://cleanup.invalid/v1/test")!) }

    func testInjectedProtocolReceivesOriginalRequestAndExactlyBoundedBody() async throws {
        BoundedHTTPStub.handler.withLock { $0 = { request in
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer deliberate-test-value")
            return .body(200, [:], [Data("1234".utf8), Data("5678".utf8)])
        } }
        var request = request()
        request.setValue("Bearer deliberate-test-value", forHTTPHeaderField: "Authorization")
        let (data, response) = try await CleanupBoundedHTTPTransport.data(for: request, session: session, maximumBytes: 8)
        XCTAssertEqual(data, Data("12345678".utf8))
        XCTAssertEqual(response.statusCode, 200)
    }

    func testBodyWithoutContentLengthIsCancelledAtByteLimit() async {
        BoundedHTTPStub.handler.withLock { $0 = { _ in .body(200, [:], [Data(repeating: 65, count: 8), Data([66])]) } }
        do {
            _ = try await CleanupBoundedHTTPTransport.data(for: request(), session: session, maximumBytes: 8)
            XCTFail("Oversized response must fail")
        } catch { XCTAssertEqual(error as? CleanupBoundedHTTPTransport.TransportError, .responseTooLarge) }
    }

    func testOversizedDeclaredLengthFailsBeforeBody() async {
        BoundedHTTPStub.handler.withLock { $0 = { _ in .body(200, ["Content-Length": "1000"], []) } }
        do {
            _ = try await CleanupBoundedHTTPTransport.data(for: request(), session: session, maximumBytes: 8)
            XCTFail("Oversized header must fail")
        } catch { XCTAssertEqual(error as? CleanupBoundedHTTPTransport.TransportError, .responseTooLarge) }
    }

    func testCrossOriginRedirectCannotSendCredentialToSecondHost() async {
        let destinations = OSAllocatedUnfairLock(initialState: [String]())
        BoundedHTTPStub.handler.withLock { $0 = { request in
            destinations.withLock { $0.append(request.url?.host ?? "") }
            return .redirect(URL(string: "https://another.invalid/collect")!)
        } }
        var request = request()
        request.setValue("Bearer deliberate-test-value", forHTTPHeaderField: "Authorization")
        do {
            _ = try await CleanupBoundedHTTPTransport.data(for: request, session: session)
            XCTFail("Redirect must fail")
        } catch { XCTAssertEqual(error as? CleanupBoundedHTTPTransport.TransportError, .crossOriginRedirect) }
        XCTAssertEqual(destinations.withLock { $0 }, ["cleanup.invalid"])
    }

    func testSameOriginRedirectIsAlsoNotAutomaticallyFollowed() async {
        BoundedHTTPStub.handler.withLock { $0 = { _ in .redirect(URL(string: "https://cleanup.invalid/another-route")!) } }
        do {
            _ = try await CleanupBoundedHTTPTransport.data(for: request(), session: session)
            XCTFail("Same-origin redirect requires an explicit profile edit")
        } catch { XCTAssertEqual(error as? CleanupBoundedHTTPTransport.TransportError, .redirectRejected) }
    }

    func testCancellationStopsOutstandingTaskWithoutAnyResponse() async {
        let started = expectation(description: "transport started")
        let stopped = expectation(description: "transport stopped")
        BoundedHTTPStub.handler.withLock { $0 = { _ in started.fulfill(); return .hold } }
        BoundedHTTPStub.stopped.withLock { $0 = { stopped.fulfill() } }
        let session = self.session!
        let request = request()
        let task = Task { try await CleanupBoundedHTTPTransport.data(for: request, session: session) }
        await fulfillment(of: [started], timeout: 2)
        task.cancel()
        do { _ = try await task.value; XCTFail("Cancellation must throw") }
        catch { XCTAssertTrue(error is CancellationError) }
        await fulfillment(of: [stopped], timeout: 2)
    }

    func testAlreadyCancelledTaskDoesNotBeginARequest() async {
        let gate = AsyncTransportGate()
        let session = self.session!
        let request = request()
        BoundedHTTPStub.handler.withLock { $0 = { _ in XCTFail("No request after cancellation"); return .hold } }
        let task = Task {
            await gate.wait()
            return try await CleanupBoundedHTTPTransport.data(for: request, session: session)
        }
        task.cancel()
        await gate.release()
        do { _ = try await task.value; XCTFail("Cancellation must throw") }
        catch { XCTAssertTrue(error is CancellationError) }
    }

    func testOllamaAvailabilityUsesInjectedTransportForTagsAndShow() async throws {
        let paths = OSAllocatedUnfairLock(initialState: [String]())
        BoundedHTTPStub.handler.withLock { $0 = { request in
            let path = request.url?.path ?? ""
            paths.withLock { $0.append(path) }
            return .body(200, [:], [Data((path.hasSuffix("tags")
                ? #"{"models":[{"name":"manual:model"}]}"#
                : #"{"capabilities":["completion"]}"#).utf8)])
        } }
        let result = try await OllamaPostProcessingService.availability(baseURL: "https://cleanup.invalid",
            selectedModel: "manual:model", session: session)
        XCTAssertTrue(result.selectedModelIsInstalled)
        XCTAssertEqual(paths.withLock { $0 }, ["/api/tags", "/api/show"])
    }

    func testCompatibleGenerationRequiresExplicitFinishReasonAndExcludesReasoningParts() async throws {
        BoundedHTTPStub.handler.withLock { $0 = { _ in .body(200, [:], [Data(#"{"choices":[{"message":{"content":"unproven completion"}}]}"#.utf8)]) } }
        do {
            _ = try await OpenAICompatiblePostProcessingService.process(text: "Deliberate fixture", baseURL: "https://cleanup.invalid/v1",
                model: "manual-id", apiKey: "", prompt: "Edit", session: session)
            XCTFail("Missing completion must fail")
        } catch { XCTAssertEqual(error as? CleanupAnswerParseError, .incomplete) }
        BoundedHTTPStub.handler.withLock { $0 = { _ in
            .body(200, [:], [Data(#"{"choices":[{"finish_reason":"stop","message":{"content":[{"type":"reasoning","text":"private reasoning"},{"type":"text","text":"Answer"}]}}]}"#.utf8)])
        } }
        let result = try await OpenAICompatiblePostProcessingService.process(text: "Deliberate fixture", baseURL: "https://cleanup.invalid/v1",
            model: "manual-id", apiKey: "", prompt: "Edit", session: session)
        XCTAssertEqual(result, "Answer")
    }

    func testCompatibleCompleteMultipartAdapterPreservesBlockSeparators() async throws {
        BoundedHTTPStub.handler.withLock { $0 = { _ in
            .body(200, [:], [Data(#"{"choices":[{"finish_reason":"stop","message":{"content":[{"type":"text","text":"First block"},{"type":"reasoning","text":"not an answer"},{"type":"text","text":"Second block"}]}}]}"#.utf8)])
        } }
        let result = try await OpenAICompatiblePostProcessingService.process(text: "Deliberate fixture", baseURL: "https://cleanup.invalid/v1",
            model: "manual-id", apiKey: "", prompt: "Edit", session: session)
        XCTAssertEqual(result, "First block\nSecond block")
    }

    func testOllamaGenerationRejectsMissingDoneAndHTTPFailuresRemainTyped() async {
        BoundedHTTPStub.handler.withLock { $0 = { request in
            .body(200, [:], [Data((request.url?.path.hasSuffix("show") == true ? "{}" : #"{"response":"unproven completion"}"#).utf8)])
        } }
        do {
            _ = try await OllamaPostProcessingService.process(text: "Deliberate fixture", baseURL: "https://cleanup.invalid",
                model: "manual:model", prompt: "Edit", session: session)
            XCTFail("Missing completion must fail")
        } catch { XCTAssertEqual(error as? CleanupAnswerParseError, .malformed) }
        for status in [401, 429, 503] {
            BoundedHTTPStub.handler.withLock { $0 = { _ in .body(status, [:], []) } }
            do {
                _ = try await OpenAICompatiblePostProcessingService.availability(baseURL: "https://cleanup.invalid/v1",
                    apiKey: "", selectedModel: "manual-id", session: session)
                XCTFail("HTTP failure must fail")
            } catch {
                guard case OpenAICompatiblePostProcessingService.ServiceError.unexpectedStatus(let actual) = error else {
                    XCTFail("Unexpected sanitized failure type"); continue
                }
                XCTAssertEqual(actual, status)
            }
        }
    }
}

private actor AsyncTransportGate {
    private var continuation: CheckedContinuation<Void, Never>?
    private var released = false
    func wait() async {
        if released { return }
        await withCheckedContinuation { continuation = $0 }
    }
    func release() { released = true; continuation?.resume(); continuation = nil }
}
