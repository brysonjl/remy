import Foundation
import os
import XCTest
@testable import Dictate_Anywhere

@MainActor final class CleanupProfileRuntimeTests: XCTestCase {
    private func profile(adapter: CleanupAdapterKind = .openAICompatible,
                         transport: CleanupTransportKind = .completeJSON,
                         policy: CleanupRoutePolicy = .ordinary,
                         credential: Bool = true,
                         baseURL: String = "http://127.0.0.1:12345/v1") throws -> CleanupProfile {
        let endpoint = try CleanupEndpointIdentity(baseURL: baseURL)
        return .init(id: UUID(), revision: 7, name: "Fixture", adapter: adapter,
            baseURL: endpoint.canonicalURL, model: "fixture-model", prompt: "Correct punctuation.",
            promptVersion: 3, timeoutSeconds: 17,
            capabilities: .init(transport: transport, discovery: false, structuredOutput: false, thinkingControl: false),
            routePolicy: policy, allowInsecureRemoteHTTP: false,
            credential: credential ? .init(id: UUID(), endpoint: endpoint) : nil)
    }
    private func configuration(_ profile: CleanupProfile?) -> RemyDictationConfiguration {
        .init(capture: .init(), cleanup: .init(mode: .profile, prompt: profile?.prompt ?? "",
            baseURL: profile?.baseURL ?? "", model: profile?.model ?? "", profile: profile))
    }
    private func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [ProfileRuntimeHTTPStub.self]
        let session = URLSession(configuration: config)
        addTeardownBlock {
            session.invalidateAndCancel()
            ProfileRuntimeHTTPStub.handler.withLock { $0 = nil }
            ProfileRuntimeHTTPStub.status.withLock { $0 = 200 }
        }
        return session
    }

    func testCompatibleRuntimeFreezesCredentialAndProfileAndUsesConservativeRequest() async throws {
        let profile = try profile()
        let config = configuration(profile)
        let credentials = RuntimeProfileCredentials()
        let calls = OSAllocatedUnfairLock(initialState: 0)
        ProfileRuntimeHTTPStub.handler.withLock { $0 = { request in
            calls.withLock { $0 += 1 }
            XCTAssertEqual(request.url?.path, "/v1/chat/completions")
            XCTAssertEqual(request.timeoutInterval, 17)
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer captured-synthetic")
            let payload = try ProfileRuntimeHTTPStub.payload(request)
            XCTAssertEqual(payload["model"] as? String, "fixture-model")
            for key in ["response_format", "temperature", "top_p", "seed", "reasoning_effort"] { XCTAssertNil(payload[key]) }
            return Data(#"{"choices":[{"finish_reason":"stop","message":{"content":"{\"action\":\"pasteCleanedText\",\"text\":\"Hello.\"}"}}]}"#.utf8)
        } }
        let runtime = RemyCleanupRuntime.makeProfile(configuration: config, credentials: credentials, session: session())
        XCTAssertEqual(credentials.resolutions, 1)
        credentials.value = "changed-synthetic"
        let output = try await runtime.process(text: "hello")
        XCTAssertEqual(output, "Hello.")
        XCTAssertEqual(credentials.resolutions, 1)
        XCTAssertEqual(credentials.endpoint, profile.credential?.endpoint)
        XCTAssertEqual(config.cleanup.profile?.revision, 7)
        XCTAssertEqual(calls.withLock { $0 }, 1)
    }

    func testOllamaProfileWithoutDiscoveryOrThinkingCapabilityOnlyGenerates() async throws {
        let profile = try profile(adapter: .ollama)
        ProfileRuntimeHTTPStub.handler.withLock { $0 = { request in
            XCTAssertTrue(request.url!.path.hasSuffix("generate"), "No metadata discovery is authorized")
            XCTAssertEqual(request.timeoutInterval, 17)
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer captured-synthetic")
            let payload = try ProfileRuntimeHTTPStub.payload(request)
            XCTAssertNil(payload["think"])
            XCTAssertNil(payload["format"])
            return Data(#"{"done":true,"done_reason":"stop","response":"{\"action\":\"pasteCleanedText\",\"text\":\"Hello.\"}"}"#.utf8)
        } }
        let runtime = RemyCleanupRuntime.makeProfile(configuration: configuration(profile),
            credentials: RuntimeProfileCredentials(), session: session())
        let output = try await runtime.process(text: "hello")
        XCTAssertEqual(output, "Hello.")
    }

    func testProfileAdaptersReceiveAppContextAndOnlyAuthorizedSurroundingText() async throws {
        let captured = DictationContext(processIdentifier: 7, bundleIdentifier: "com.apple.Terminal", appName: "Terminal",
            category: .terminalAgent, documentURL: "file:///private/project", documentTitle: "PrivateProjectTitle",
            fieldRole: "AXTextArea", fieldSubrole: nil, fieldPurpose: .unknown,
            textBeforeCursor: "PrivatePrecedingText", selectedText: nil, textAfterCursor: nil,
            isSecureField: false, isContextExcluded: false)
        for adapter in [CleanupAdapterKind.openAICompatible, .ollama] {
            for (endpoint, share, includesText) in [
                ("http://127.0.0.1:12345/v1", false, true),
                ("https://fixture.example.test/v1", false, false),
                ("https://fixture.example.test/v1", true, true)
            ] {
                let profile = try profile(adapter: adapter, credential: false, baseURL: endpoint)
                let config = RemyDictationConfiguration(capture: .init(), cleanup: .init(mode: .profile,
                    profile: profile, contextPolicy: .init(isEnabled: true, shareCapturedTextRemotely: share,
                        writingStyles: [.terminalAgent: .original])))
                ProfileRuntimeHTTPStub.handler.withLock { $0 = { request in
                    let payload = try ProfileRuntimeHTTPStub.payload(request)
                    let body = String(decoding: try JSONSerialization.data(withJSONObject: payload), as: UTF8.self)
                    XCTAssertTrue(body.contains("Terminal"))
                    XCTAssertTrue(body.contains(DictationContextCategory.terminalAgent.displayName))
                    XCTAssertEqual(body.contains("PrivatePrecedingText"), includesText)
                    XCTAssertEqual(body.contains("PrivateProjectTitle"), includesText)
                    if adapter == .openAICompatible {
                        return Data(#"{"choices":[{"finish_reason":"stop","message":{"content":"{\"action\":\"pasteCleanedText\",\"text\":\"Hello.\"}"}}]}"#.utf8)
                    }
                    return Data(#"{"done":true,"done_reason":"stop","response":"{\"action\":\"pasteCleanedText\",\"text\":\"Hello.\"}"}"#.utf8)
                } }
                let runtime = RemyCleanupRuntime.makeProfile(configuration: config, credentials: nil, session: session())
                let output = try await runtime.process(text: "hello", context: captured)
                XCTAssertEqual(output, "Hello.")
            }
        }
    }

    func testUnsupportedTransportAndUnknownStrictLocalRouteFailBeforeCredentialReadOrNetwork() async throws {
        let credentials = RuntimeProfileCredentials()
        let session = session()
        ProfileRuntimeHTTPStub.handler.withLock { $0 = { _ in
            XCTFail("Rejected profile must not perform HTTP")
            throw URLError(.cancelled)
        } }
        for profile in [try profile(transport: .sse), try profile(adapter: .ollama, transport: .ndjson),
                        try profile(policy: .strictLocalOnly)] {
            let runtime = RemyCleanupRuntime.makeProfile(configuration: configuration(profile), credentials: credentials, session: session)
            do { _ = try await runtime.process(text: "hello"); XCTFail("Invalid profile accepted") }
            catch { XCTAssertFalse(String(describing: error).contains("captured-synthetic")) }
        }
        XCTAssertEqual(credentials.resolutions, 0)
    }

    func testMissingOrFailedScopedCredentialNeverFallsBack() async throws {
        let credentials = RuntimeProfileCredentials()
        credentials.value = nil
        let profile = try profile()
        let runtime = RemyCleanupRuntime.makeProfile(configuration: configuration(profile), credentials: credentials, session: session())
        credentials.value = "later-synthetic"
        do { _ = try await runtime.process(text: "hello"); XCTFail("Missing captured credential accepted") }
        catch { XCTAssertEqual(error as? RemyCleanupRuntime.RuntimeError, .profileCredentialUnavailable) }
        XCTAssertEqual(credentials.resolutions, 1)
        credentials.fail = true
        let failed = RemyCleanupRuntime.makeProfile(configuration: configuration(profile), credentials: credentials, session: session())
        do { _ = try await failed.process(text: "hello"); XCTFail("Credential backend failure accepted") }
        catch { XCTAssertFalse(String(describing: error).contains("secret-bearing-backend-message")) }
    }

    func testProfileHTTPFailuresKeepStatusCategoryWithoutBodyOrCredentialAndDoNotRetry() async throws {
        let secret = "synthetic-private-error-body-and-key"
        for adapter in [CleanupAdapterKind.openAICompatible, .ollama] {
            for (status, expected): (Int, RemyCleanupFailure) in [
                (401, .authentication), (429, .rateLimited), (503, .serverUnavailable),
                (413, .inputTooLong), (400, .requestRejected)
            ] {
                let calls = OSAllocatedUnfairLock(initialState: 0)
                ProfileRuntimeHTTPStub.status.withLock { $0 = status }
                ProfileRuntimeHTTPStub.handler.withLock { $0 = { _ in
                    calls.withLock { $0 += 1 }
                    let body: [String: Any] = adapter == .ollama ? ["error": secret] : ["error": ["message": secret]]
                    return try JSONSerialization.data(withJSONObject: body)
                } }
                let runtime = RemyCleanupRuntime.makeProfile(configuration: configuration(try profile(adapter: adapter)),
                    credentials: RuntimeProfileCredentials(), session: session())
                do {
                    _ = try await runtime.process(text: "hello")
                    XCTFail("HTTP failure must not be reported as successful cleanup")
                } catch {
                    XCTAssertEqual(error as? RemyCleanupFailure, expected)
                    XCTAssertFalse(String(describing: error).contains(secret))
                    XCTAssertFalse(String(describing: error).contains("captured-synthetic"))
                }
                XCTAssertEqual(calls.withLock { $0 }, 1)
            }
        }
        ProfileRuntimeHTTPStub.status.withLock { $0 = 200 }
    }

    func testProfileTransportFailureIsClassifiedWithoutRetainingPrivateURL() async throws {
        let secret = "https://private-endpoint.test/?key=synthetic-secret"
        ProfileRuntimeHTTPStub.handler.withLock { $0 = { _ in
            throw URLError(.cannotConnectToHost, userInfo: [NSURLErrorFailingURLStringErrorKey: secret])
        } }
        let runtime = RemyCleanupRuntime.makeProfile(configuration: configuration(try profile()),
            credentials: RuntimeProfileCredentials(), session: session())
        do {
            _ = try await runtime.process(text: "hello")
            XCTFail("Transport failure must be recorded")
        } catch {
            XCTAssertEqual(error as? RemyCleanupFailure, .transport)
            XCTAssertFalse(String(describing: error).contains(secret))
        }
    }

    func testProfileUnchangedResponseIsSuccessful() async throws {
        ProfileRuntimeHTTPStub.handler.withLock { $0 = { _ in
            Data(#"{"choices":[{"finish_reason":"stop","message":{"content":"{\"action\":\"pasteTranscriptAsIs\",\"text\":null}"}}]}"#.utf8)
        } }
        let runtime = RemyCleanupRuntime.makeProfile(configuration: configuration(try profile()),
            credentials: RuntimeProfileCredentials(), session: session())
        let result = try await runtime.process(text: "Already clean.")
        XCTAssertEqual(result, "Already clean.")
    }
}

@MainActor private final class RuntimeProfileCredentials: CleanupProfileCredentialStore {
    var value: String? = "captured-synthetic"
    var resolutions = 0
    var endpoint: CleanupEndpointIdentity?
    var fail = false
    func stageLegacyBinding(from source: CleanupLegacyCredentialSource, to destination: CleanupCredentialReference) throws {
        XCTFail("Runtime must never migrate credentials")
    }
    func resolve(_ reference: CleanupCredentialReference, for endpoint: CleanupEndpointIdentity) throws -> String? {
        resolutions += 1; self.endpoint = endpoint
        if fail { throw NSError(domain: "secret-bearing-backend-message", code: 1) }
        return value
    }
}

private final class ProfileRuntimeHTTPStub: URLProtocol, @unchecked Sendable {
    nonisolated static let handler = OSAllocatedUnfairLock<(@Sendable (URLRequest) throws -> Data)?>(initialState: nil)
    nonisolated static let status = OSAllocatedUnfairLock(initialState: 200)
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        do {
            let data = try XCTUnwrap(Self.handler.withLock { $0 })(request)
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: Self.status.withLock { $0 },
                httpVersion: nil, headerFields: ["Content-Type": "application/json"])!, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {}
    nonisolated static func payload(_ request: URLRequest) throws -> [String: Any] {
        var data = request.httpBody ?? Data()
        if let stream = request.httpBodyStream {
            stream.open(); defer { stream.close() }
            var buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable {
                let count = stream.read(&buffer, maxLength: buffer.count)
                if count <= 0 { break }
                data.append(contentsOf: buffer.prefix(count))
            }
        }
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }
}
