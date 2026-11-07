import Foundation
import Darwin

/// The store supplies an immutable artifact and a fresh file inside owned staging.
/// Tests inject a transport; ordinary model resolution never invokes this API.
nonisolated protocol RemyModelDownloadTransport: Sendable {
    func download(_ artifact: RemyModelManifest.Artifact, to destination: URL,
                  progress: @escaping @Sendable (Int64) -> Void) async throws
}

nonisolated struct URLSessionRemyModelDownloadTransport: RemyModelDownloadTransport {
    func download(_ artifact: RemyModelManifest.Artifact, to destination: URL,
                  progress: @escaping @Sendable (Int64) -> Void) async throws {
        let operation = RemyModelTransfer(destination: destination, limit: artifact.size, progress: progress)
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            try await operation.run(url: artifact.url)
        } onCancel: { operation.cancel() }
    }
}

/// Data delegates stream to disk in URLSession chunks instead of retaining a
/// complete weight in RAM. The lock covers bounded writes and cancellation.
private nonisolated final class RemyModelTransfer: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    enum TransferError: Error { case unsafeURL, httpStatus(Int), oversized, incomplete }
    private let lock = NSLock()
    private let destination: URL
    private let limit: Int64
    private let progress: @Sendable (Int64) -> Void
    private var continuation: CheckedContinuation<Void, Error>?
    private var session: URLSession?
    private var task: URLSessionDataTask?
    private var file: FileHandle?
    private var count: Int64 = 0
    private var cancelled = false
    private var completed = false

    init(destination: URL, limit: Int64, progress: @escaping @Sendable (Int64) -> Void) {
        self.destination = destination
        self.limit = limit
        self.progress = progress
    }

    func run(url: URL) async throws {
        guard Self.isHTTPS(url) else { throw TransferError.unsafeURL }
        try await withCheckedThrowingContinuation { continuation in
            do {
                try lock.withLock {
                    guard !cancelled else { throw CancellationError() }
                    let descriptor = Darwin.open(destination.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, mode_t(0o600))
                    guard descriptor >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
                    file = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
                    self.continuation = continuation
                    let configuration = URLSessionConfiguration.ephemeral
                    configuration.urlCache = nil
                    configuration.urlCredentialStorage = nil
                    configuration.httpCookieStorage = nil
                    configuration.httpShouldSetCookies = false
                    configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
                    configuration.timeoutIntervalForRequest = 60
                    configuration.timeoutIntervalForResource = 3600
                    let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
                    self.session = session
                    let task = session.dataTask(with: url)
                    self.task = task
                    task.resume()
                }
            } catch {
                continuation.resume(throwing: error)
            }
        }
    }

    func cancel() {
        lock.withLock { cancelled = true }
        finish(error: CancellationError())
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive response: URLResponse,
                    completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void) {
        guard let http = response as? HTTPURLResponse, Self.isHTTPS(response.url) else {
            completionHandler(.cancel); finish(error: TransferError.unsafeURL); return
        }
        guard (200..<300).contains(http.statusCode) else {
            completionHandler(.cancel); finish(error: TransferError.httpStatus(http.statusCode)); return
        }
        guard response.expectedContentLength <= limit else {
            completionHandler(.cancel); finish(error: TransferError.oversized); return
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        do {
            let written: Int64? = try lock.withLock {
                guard !completed, !cancelled else { return nil }
                guard Int64(data.count) <= limit - count else { throw TransferError.oversized }
                guard let file else { throw TransferError.incomplete }
                try file.write(contentsOf: data)
                count += Int64(data.count)
                return count
            }
            if let written { progress(written) }
        } catch { finish(error: error) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        finish(error: error)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        guard Self.isHTTPS(request.url) else {
            completionHandler(nil); finish(error: TransferError.unsafeURL); return
        }
        // Hugging Face uses HTTPS CDN redirects. Never carry credentials/cookies.
        var clean = request
        clean.setValue(nil, forHTTPHeaderField: "Authorization")
        clean.setValue(nil, forHTTPHeaderField: "Cookie")
        completionHandler(clean)
    }

    private func finish(error: Error?) {
        let result: (CheckedContinuation<Void, Error>, URLSession?, Error?)? = lock.withLock {
            guard !completed, let continuation else { return nil }
            completed = true
            var finalError = error
            if cancelled { finalError = CancellationError() }
            if finalError == nil, count != limit { finalError = TransferError.incomplete }
            do { try file?.close() } catch { if finalError == nil { finalError = error } }
            file = nil
            self.continuation = nil
            let session = self.session
            self.session = nil
            self.task = nil
            return (continuation, session, finalError)
        }
        guard let (continuation, session, error) = result else { return }
        session?.invalidateAndCancel()
        if let error { continuation.resume(throwing: error) }
        else { continuation.resume() }
    }

    private static func isHTTPS(_ url: URL?) -> Bool {
        guard let url else { return false }
        return url.scheme == "https" && url.host != nil && url.user == nil && url.password == nil
    }
}
