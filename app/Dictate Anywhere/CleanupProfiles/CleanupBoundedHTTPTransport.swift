import Foundation

/// Complete-body transport for existing stream:false adapters. A per-request
/// delegate enforces bounds while bytes arrive and rejects redirects before any
/// redirected request can carry credentials. The caller's configuration keeps
/// injected URLProtocol test transports and timeout settings usable.
///
/// Injection boundary: configuration is cloned into a new, owned client per
/// request. Caller delegates (including custom TLS pinning/auth), task identity
/// and connection pools are NOT reused or supported by this API. Callers needing
/// those guarantees must use a reviewed transport implementation; do not assume
/// passing a custom-delegate session preserves its delegate behavior. Platform
/// TLS validation remains enabled, and no stored HTTP credentials are consulted.
nonisolated enum CleanupBoundedHTTPTransport {
    enum TransportError: Error, Equatable {
        case invalidRequest, invalidResponse, responseTooLarge, redirectRejected, crossOriginRedirect
    }
    static func data(for request: URLRequest, session: URLSession,
                     maximumBytes: Int = 2_097_152) async throws -> (Data, HTTPURLResponse) {
        guard maximumBytes > 0, request.timeoutInterval.isFinite, request.timeoutInterval > 0,
              let url = request.url,
              ["http", "https"].contains(url.scheme?.lowercased() ?? ""),
              let parts = URLComponents(url: url, resolvingAgainstBaseURL: false),
              parts.host != nil, parts.user == nil, parts.password == nil else { throw TransportError.invalidRequest }
        let exchange = Exchange(configuration: session.configuration, maximumBytes: maximumBytes)
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                exchange.start(request, continuation: continuation)
            }
        } onCancel: {
            exchange.cancel()
        }
    }

    private nonisolated final class Exchange: NSObject, URLSessionDataDelegate, @unchecked Sendable {
        private let lock = NSLock()
        private let configuration: URLSessionConfiguration
        private let maximumBytes: Int
        private var continuation: CheckedContinuation<(Data, HTTPURLResponse), Error>?
        private var ownedSession: URLSession?
        private var task: URLSessionDataTask?
        private var response: HTTPURLResponse?
        private var body = Data()
        private var finished = false

        init(configuration: URLSessionConfiguration, maximumBytes: Int) {
            // Session returns a configuration copy; do not mutate its live caller.
            self.configuration = configuration
            self.maximumBytes = maximumBytes
            configuration.urlCache = nil
            configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
            configuration.httpCookieStorage = nil
            configuration.httpShouldSetCookies = false
            configuration.urlCredentialStorage = nil
        }

        func start(_ request: URLRequest, continuation: CheckedContinuation<(Data, HTTPURLResponse), Error>) {
            let didStart = lock.withLock {
                guard !finished else { return false }
                self.continuation = continuation
                // Bound total resource time, not only idle time between bytes.
                // Retain a stricter injected-session limit when one is present.
                let inherited = configuration.timeoutIntervalForResource
                configuration.timeoutIntervalForResource = inherited.isFinite && inherited > 0
                    ? min(inherited, request.timeoutInterval) : request.timeoutInterval
                let queue = OperationQueue()
                queue.maxConcurrentOperationCount = 1
                let session = URLSession(configuration: configuration, delegate: self, delegateQueue: queue)
                ownedSession = session
                task = session.dataTask(with: request)
                task?.resume()
                return true
            }
            if !didStart { continuation.resume(throwing: CancellationError()) }
        }

        func cancel() { finish(.failure(CancellationError())) }

        private func finish(_ result: Result<(Data, HTTPURLResponse), Error>) {
            let resources = lock.withLock { () -> (CheckedContinuation<(Data, HTTPURLResponse), Error>?, URLSession?) in
                guard !finished else { return (nil, nil) }
                finished = true
                let resources = (continuation, ownedSession)
                continuation = nil; ownedSession = nil; task = nil
                body.removeAll(keepingCapacity: false)
                return resources
            }
            resources.1?.invalidateAndCancel()
            resources.0?.resume(with: result)
        }

        func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                        didReceive response: URLResponse,
                        completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
            guard let http = response as? HTTPURLResponse else {
                completionHandler(.cancel)
                finish(.failure(TransportError.invalidResponse))
                return
            }
            guard response.expectedContentLength <= Int64(maximumBytes) else {
                completionHandler(.cancel)
                finish(.failure(TransportError.responseTooLarge))
                return
            }
            let active = lock.withLock {
                guard !finished else { return false }
                self.response = http
                return true
            }
            completionHandler(active ? .allow : .cancel)
        }

        func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
            let oversized = lock.withLock {
                guard !finished else { return false }
                guard data.count <= maximumBytes - body.count else { return true }
                body.append(data)
                return false
            }
            if oversized { finish(.failure(TransportError.responseTooLarge)) }
        }

        func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
            let result = lock.withLock { () -> Result<(Data, HTTPURLResponse), Error> in
                if let error { return .failure(error) }
                guard let response else { return .failure(TransportError.invalidResponse) }
                return .success((body, response))
            }
            finish(result)
        }

        func urlSession(_ session: URLSession, task: URLSessionTask,
                        willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                        completionHandler: @escaping (URLRequest?) -> Void) {
            let decision: CleanupRedirectDecision
            if let source = response.url, let destination = request.url {
                decision = CleanupTransportPolicy.redirect(from: source, to: destination)
            } else { decision = .rejectCrossOrigin }
            completionHandler(nil)
            finish(.failure(decision == .rejectCrossOrigin ? TransportError.crossOriginRedirect : TransportError.redirectRejected))
        }

        func urlSession(_ session: URLSession, task: URLSessionTask,
                        didReceive challenge: URLAuthenticationChallenge,
                        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
            // Preserve platform TLS trust. Never consult stored HTTP credentials
            // or open an interactive auth flow for a profile request.
            if challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust {
                completionHandler(.performDefaultHandling, nil)
            } else { completionHandler(.cancelAuthenticationChallenge, nil) }
        }
    }
}
