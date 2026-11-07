import CryptoKit
import Foundation
import Darwin

/// Only explicit install/adopt calls use a transport. Inspection, verification,
/// activation and recognition leases are offline. No SDK registry is consulted.
actor RemyModelStore {
    struct InstalledRevision: Codable, Equatable, Sendable {
        let modelID: String
        let revision: String
        let manifestDigest: String
        let directory: URL
    }
    struct ModelLease: Equatable, Sendable {
        let id: UUID
        let installed: InstalledRevision
        var directory: URL { installed.directory }
    }
    struct InstallProgress: Equatable, Sendable {
        let completedBytes: Int64
        let totalBytes: Int64
    }
    enum InstallationStatus: Equatable, Sendable {
        case missing, installed(InstalledRevision), active(InstalledRevision)
    }
    private struct RevisionRecord: Codable, Equatable, Sendable {
        let modelID: String
        let revision: String
        let manifestDigest: String
    }
    private struct Activation: Codable, Sendable {
        var current: RevisionRecord?
        var previous: RevisionRecord?
    }

    private let paths: RemyStoragePaths
    private let transport: any RemyModelDownloadTransport
    private let files = FileManager.default
    private let activationWriter: @Sendable (Data, URL) throws -> Void
    private var operationID: UUID?
    private var leases: [UUID: InstalledRevision] = [:]

    init(paths: RemyStoragePaths = .current,
         transport: any RemyModelDownloadTransport = URLSessionRemyModelDownloadTransport(),
         activationWriter: @escaping @Sendable (Data, URL) throws -> Void = { data, url in try data.write(to: url, options: .atomic) }) {
        self.paths = paths
        self.transport = transport
        self.activationWriter = activationWriter
    }

    /// Cheap offline inventory bridge for synchronous UI/model selectors.
    /// This checks activation/manifest identity and confinement, NOT artifact
    /// integrity. Inference must still acquire a fully verified actor lease.
    nonisolated static func activeDirectory(for manifest: RemyModelManifest,
                                            paths: RemyStoragePaths = .current) throws -> URL? {
        try manifest.validate()
        let root = paths.modelsDirectory.appendingPathComponent("Installed", isDirectory: true)
            .appendingPathComponent(manifest.modelID, isDirectory: true)
        let pointerURL = root.appendingPathComponent("active.json")
        try paths.requireOwnedModelPath(pointerURL, allowRoot: false)
        guard FileManager.default.fileExists(atPath: pointerURL.path) else { return nil }
        let pointer = try JSONDecoder().decode(Activation.self, from: Data(contentsOf: pointerURL))
        guard let current = pointer.current else { return nil }
        guard current.modelID == manifest.modelID, current.revision == manifest.revision,
              current.manifestDigest == (try manifest.digest) else { throw RemyModelStoreError.notActive }
        let versions = root.appendingPathComponent("revisions", isDirectory: true)
        let metadata = versions.appendingPathComponent(manifest.revision + ".json")
        let directory = versions.appendingPathComponent(manifest.revision, isDirectory: true)
        try paths.requireOwnedModelPath(metadata, allowRoot: false)
        try paths.requireOwnedModelPath(directory, allowRoot: false)
        let registered = try RemyModelManifest.load(from: metadata)
        guard try registered.digest == manifest.digest else { throw RemyModelStoreError.invalidActivation }
        return directory
    }

    /// Presence is not integrity: callers must verify/acquire before inference.
    func inspect(_ manifest: RemyModelManifest) throws -> InstallationStatus {
        try manifest.validate()
        let directory = try revisionDirectory(manifest)
        guard files.fileExists(atPath: directory.path) else { return .missing }
        let stored = try registeredManifest(modelID: manifest.modelID, revision: manifest.revision)
        guard try stored.digest == manifest.digest else { throw RemyModelStoreError.invalidActivation }
        let installed = try installedRevision(manifest)
        if try activation(manifest.modelID).current == record(manifest) { return .active(installed) }
        return .installed(installed)
    }

    /// Downloads only the supplied immutable URLs. Installation never activates.
    func install(_ manifest: RemyModelManifest,
                 progress: @escaping @Sendable (InstallProgress) -> Void = { _ in }) async throws -> InstalledRevision {
        try requireIdleOperation()
        try manifest.validate()
        let operation = UUID()
        operationID = operation
        defer { operationID = nil }
        try Task.checkCancellation()
        let destination = try revisionDirectory(manifest)
        if files.fileExists(atPath: destination.path) {
            try verifyDirectory(destination, manifest: manifest)
            try register(manifest)
            return try installedRevision(manifest)
        }
        try createDirectory(paths.modelsDirectory)
        try checkSpace(bytes: manifest.totalBytes)
        let stage = try newStage(operation)
        defer { try? files.removeItem(at: stage) }
        var completed: Int64 = 0
        for artifact in manifest.files {
            try Task.checkCancellation()
            let target = stage.appendingPathComponent(artifact.path)
            try createDirectory(target.deletingLastPathComponent())
            try confine(target)
            let base = completed
            let total = manifest.totalBytes
            try await transport.download(artifact, to: target) { count in
                progress(InstallProgress(completedBytes: base + min(max(count, 0), artifact.size), totalBytes: total))
            }
            try Task.checkCancellation()
            try verifyFile(target, artifact: artifact)
            completed += artifact.size
            progress(InstallProgress(completedBytes: completed, totalBytes: total))
        }
        try verifyDirectory(stage, manifest: manifest)
        try Task.checkCancellation()
        try publish(stage, manifest: manifest)
        return try installedRevision(manifest)
    }

    /// Import only an explicitly selected Remy-owned legacy directory. Never
    /// moves, repairs or removes the existing bundle (or any FluidAudio cache).
    func adoptLegacy(_ manifest: RemyModelManifest, from source: URL) throws -> InstalledRevision {
        try requireIdleOperation()
        try manifest.validate()
        try confine(source)
        guard source.standardizedFileURL.deletingLastPathComponent() == paths.modelsDirectory.standardizedFileURL,
              source.lastPathComponent == manifest.modelID else { throw RemyModelStoreError.invalidLegacyLocation }
        let operation = UUID()
        operationID = operation
        defer { operationID = nil }
        try verifyDirectory(source, manifest: manifest)
        let destination = try revisionDirectory(manifest)
        if files.fileExists(atPath: destination.path) {
            try verifyDirectory(destination, manifest: manifest)
            try register(manifest)
            return try installedRevision(manifest)
        }
        try checkSpace(bytes: manifest.totalBytes)
        let stage = try newStage(operation)
        defer { try? files.removeItem(at: stage) }
        for artifact in manifest.files {
            try Task.checkCancellation()
            let target = stage.appendingPathComponent(artifact.path)
            try createDirectory(target.deletingLastPathComponent())
            try confine(source.appendingPathComponent(artifact.path))
            try files.copyItem(at: source.appendingPathComponent(artifact.path), to: target)
        }
        try verifyDirectory(stage, manifest: manifest)
        try Task.checkCancellation()
        try publish(stage, manifest: manifest)
        return try installedRevision(manifest)
    }

    @discardableResult
    func verify(_ manifest: RemyModelManifest) throws -> InstalledRevision {
        try requireIdleOperation()
        try manifest.validate()
        let stored = try registeredManifest(modelID: manifest.modelID, revision: manifest.revision)
        guard try stored.digest == manifest.digest else { throw RemyModelStoreError.invalidActivation }
        try verifyDirectory(revisionDirectory(manifest), manifest: manifest)
        return try installedRevision(manifest)
    }

    /// Complete bundle publication precedes this one regular-file atomic switch.
    @discardableResult
    func activate(_ manifest: RemyModelManifest) throws -> InstalledRevision {
        try requireIdleOperation()
        guard leases.isEmpty else { throw RemyModelStoreError.leasedRevision }
        let installed = try verify(manifest)
        let next = try record(manifest)
        var pointer = try activation(manifest.modelID)
        if pointer.current == next { return installed }
        pointer.previous = pointer.current
        pointer.current = next
        try Task.checkCancellation()
        try writeActivation(pointer, modelID: manifest.modelID)
        return installed
    }

    /// Revocable only by explicit release: an active recognizer owns its bundle
    /// until engine unload/finish has joined. Do not release immediately on cancel.
    func acquireInstalled(_ manifest: RemyModelManifest) throws -> ModelLease {
        try requireIdleOperation()
        try manifest.validate()
        guard try activation(manifest.modelID).current == record(manifest) else { throw RemyModelStoreError.notActive }
        let installed = try verify(manifest)
        let lease = ModelLease(id: UUID(), installed: installed)
        leases[lease.id] = installed
        return lease
    }

    func release(_ lease: ModelLease) {
        guard leases[lease.id] == lease.installed else { return }
        leases.removeValue(forKey: lease.id)
    }

    @discardableResult
    func rollback(modelID: String) throws -> InstalledRevision {
        try requireIdleOperation()
        guard leases.isEmpty else { throw RemyModelStoreError.leasedRevision }
        var pointer = try activation(modelID)
        guard let previous = pointer.previous else { throw RemyModelStoreError.noRollback }
        let manifest = try registeredManifest(modelID: modelID, revision: previous.revision)
        guard try record(manifest) == previous else { throw RemyModelStoreError.invalidActivation }
        let installed = try verify(manifest)
        pointer.previous = pointer.current
        pointer.current = previous
        try Task.checkCancellation()
        try writeActivation(pointer, modelID: modelID)
        return installed
    }

    /// Explicitly deselect before removing an active revision. This never
    /// silently activates a different model or deletes another app's assets.
    func deactivate(modelID: String) throws {
        try Task.checkCancellation()
        try requireIdleOperation()
        guard leases.isEmpty else { throw RemyModelStoreError.leasedRevision }
        let pointer = try activation(modelID)
        try writeActivation(Activation(current: nil, previous: pointer.current ?? pointer.previous), modelID: modelID)
    }

    func remove(_ manifest: RemyModelManifest) throws {
        try Task.checkCancellation()
        try requireIdleOperation()
        try manifest.validate()
        let target = try installedRevision(manifest)
        guard !leases.values.contains(where: { $0.directory == target.directory }) else { throw RemyModelStoreError.leasedRevision }
        var pointer = try activation(manifest.modelID)
        // Protection is based on physical revision identity, never on caller
        // metadata digest: a changed manifest must not bypass the active guard.
        func matchesIdentity(_ record: RevisionRecord?) -> Bool {
            record?.modelID == manifest.modelID && record?.revision == manifest.revision
        }
        guard !matchesIdentity(pointer.current) else { throw RemyModelStoreError.activeRevision }
        // An interrupted publish can leave a bundle without registration, or
        // corrupt registration. Explicit removal must still recover that owned,
        // inactive revision without trusting or decoding its metadata.
        // Remove rollback references first. Interrupted removal cannot leave an
        // activation pointer naming a deleted rollback bundle.
        if matchesIdentity(pointer.previous) {
            pointer.previous = nil
            try writeActivation(pointer, modelID: manifest.modelID)
        }
        try confine(target.directory)
        if files.fileExists(atPath: target.directory.path) {
            try files.removeItem(at: target.directory)
        }
        let metadata = try manifestURL(modelID: manifest.modelID, revision: manifest.revision)
        try confine(metadata)
        if files.fileExists(atPath: metadata.path) {
            try files.removeItem(at: metadata)
        }
    }

    private func requireIdleOperation() throws {
        guard operationID == nil else { throw RemyModelStoreError.busy }
    }

    private func confine(_ url: URL) throws {
        try paths.requireOwnedModelPath(url, allowRoot: url.standardizedFileURL == paths.modelsDirectory.standardizedFileURL)
    }

    private func modelRoot(_ modelID: String) throws -> URL {
        guard RemyModelManifest.isComponent(modelID) else { throw RemyModelStoreError.invalidActivation }
        let result = paths.modelsDirectory.appendingPathComponent("Installed", isDirectory: true).appendingPathComponent(modelID, isDirectory: true)
        try confine(result)
        return result
    }

    private func revisionDirectory(_ manifest: RemyModelManifest) throws -> URL {
        let result = try modelRoot(manifest.modelID).appendingPathComponent("revisions", isDirectory: true).appendingPathComponent(manifest.revision, isDirectory: true)
        try confine(result)
        return result
    }

    private func manifestURL(modelID: String, revision: String) throws -> URL {
        guard revision.count == 40, revision.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else {
            throw RemyModelStoreError.invalidActivation
        }
        let result = try modelRoot(modelID).appendingPathComponent("revisions", isDirectory: true).appendingPathComponent(revision + ".json")
        try confine(result)
        return result
    }

    private func installedRevision(_ manifest: RemyModelManifest) throws -> InstalledRevision {
        InstalledRevision(modelID: manifest.modelID, revision: manifest.revision, manifestDigest: try manifest.digest,
                          directory: try revisionDirectory(manifest))
    }

    private func record(_ manifest: RemyModelManifest) throws -> RevisionRecord {
        RevisionRecord(modelID: manifest.modelID, revision: manifest.revision, manifestDigest: try manifest.digest)
    }

    private func createDirectory(_ url: URL) throws {
        try confine(url)
        try files.createDirectory(at: url, withIntermediateDirectories: true)
        try confine(url)
    }

    private func newStage(_ operation: UUID) throws -> URL {
        let stage = paths.modelsDirectory.appendingPathComponent(".staging", isDirectory: true).appendingPathComponent(operation.uuidString, isDirectory: true)
        try confine(stage)
        guard !files.fileExists(atPath: stage.path) else { throw RemyModelStoreError.invalidInventory }
        try createDirectory(stage)
        return stage
    }

    private func publish(_ stage: URL, manifest: RemyModelManifest) throws {
        let destination = try revisionDirectory(manifest)
        try createDirectory(destination.deletingLastPathComponent())
        try confine(stage)
        try confine(destination)
        guard !files.fileExists(atPath: destination.path) else { throw RemyModelStoreError.invalidInventory }
        // Exclusive same-volume rename cannot replace even an empty revision
        // directory created concurrently outside this actor.
        guard Darwin.renamex_np(stage.path, destination.path, UInt32(RENAME_EXCL)) == 0 else {
            throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
        }
        try register(manifest)
    }

    private func register(_ manifest: RemyModelManifest) throws {
        let url = try manifestURL(modelID: manifest.modelID, revision: manifest.revision)
        if files.fileExists(atPath: url.path) {
            let previous = try RemyModelManifest.load(from: url)
            guard previous == manifest else { throw RemyModelStoreError.invalidActivation }
        } else {
            try confine(url)
            try manifest.encoded().write(to: url, options: .atomic)
        }
    }

    private func registeredManifest(modelID: String, revision: String) throws -> RemyModelManifest {
        let url = try manifestURL(modelID: modelID, revision: revision)
        guard files.fileExists(atPath: url.path) else { throw RemyModelStoreError.notInstalled }
        let manifest = try RemyModelManifest.load(from: url)
        guard manifest.modelID == modelID, manifest.revision == revision else { throw RemyModelStoreError.invalidActivation }
        return manifest
    }

    private func activation(_ modelID: String) throws -> Activation {
        let url = try modelRoot(modelID).appendingPathComponent("active.json")
        try confine(url)
        guard files.fileExists(atPath: url.path) else { return Activation(current: nil, previous: nil) }
        let pointer = try JSONDecoder().decode(Activation.self, from: Data(contentsOf: url))
        for record in [pointer.current, pointer.previous].compactMap({ $0 }) {
            guard record.modelID == modelID else { throw RemyModelStoreError.invalidActivation }
            _ = try manifestURL(modelID: record.modelID, revision: record.revision)
        }
        return pointer
    }

    private func writeActivation(_ pointer: Activation, modelID: String) throws {
        let root = try modelRoot(modelID)
        try createDirectory(root)
        let url = root.appendingPathComponent("active.json")
        try confine(url)
        try activationWriter(JSONEncoder().encode(pointer), url)
    }

    private func verifyFile(_ url: URL, artifact: RemyModelManifest.Artifact) throws {
        try Task.checkCancellation()
        try confine(url)
        let attributes = try files.attributesOfItem(atPath: url.path)
        guard attributes[.type] as? FileAttributeType == .typeRegular,
              (attributes[.size] as? NSNumber)?.int64Value == artifact.size else {
            throw RemyModelStoreError.integrityMismatch(artifact.path)
        }
        var hasher = SHA256()
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var readBytes: Int64 = 0
        while let data = try handle.read(upToCount: 1024 * 1024), !data.isEmpty {
            try Task.checkCancellation()
            guard Int64(data.count) <= artifact.size - readBytes else {
                throw RemyModelStoreError.integrityMismatch(artifact.path)
            }
            readBytes += Int64(data.count)
            hasher.update(data: data)
        }
        guard readBytes == artifact.size else { throw RemyModelStoreError.integrityMismatch(artifact.path) }
        let digest = hasher.finalize().map { String(format: "%02x", $0) }.joined()
        guard digest == artifact.sha256 else { throw RemyModelStoreError.integrityMismatch(artifact.path) }
    }

    private func verifyDirectory(_ directory: URL, manifest: RemyModelManifest) throws {
        try confine(directory)
        guard try directory.resourceValues(forKeys: [.isDirectoryKey]).isDirectory == true else { throw RemyModelStoreError.invalidInventory }
        var expected = Set(manifest.files.map(\.path))
        for artifact in manifest.files {
            var parent = (artifact.path as NSString).deletingLastPathComponent
            while !parent.isEmpty {
                expected.insert(parent)
                parent = (parent as NSString).deletingLastPathComponent
            }
        }
        guard let enumerator = files.enumerator(at: directory, includingPropertiesForKeys: [.isRegularFileKey, .isDirectoryKey, .isSymbolicLinkKey],
                                                options: [], errorHandler: { _, _ in false }) else { throw RemyModelStoreError.invalidInventory }
        var actual = Set<String>()
        for case let item as URL in enumerator {
            try Task.checkCancellation()
            try confine(item)
            let values = try item.resourceValues(forKeys: [.isRegularFileKey, .isDirectoryKey, .isSymbolicLinkKey])
            guard values.isSymbolicLink != true, values.isRegularFile == true || values.isDirectory == true else { throw RemyModelStoreError.invalidInventory }
            // Directory URLs may preserve a trailing slash. Compare logical
            // path components, not raw character offsets or URL spelling.
            let rootComponents = directory.standardizedFileURL.pathComponents
            let itemComponents = item.standardizedFileURL.pathComponents
            guard itemComponents.starts(with: rootComponents), itemComponents.count > rootComponents.count else {
                throw RemyModelStoreError.invalidInventory
            }
            actual.insert(itemComponents.dropFirst(rootComponents.count).joined(separator: "/"))
        }
        guard actual == expected else { throw RemyModelStoreError.invalidInventory }
        for artifact in manifest.files { try verifyFile(directory.appendingPathComponent(artifact.path), artifact: artifact) }
    }

    private func checkSpace(bytes: Int64) throws {
        let available = try files.attributesOfFileSystem(forPath: paths.modelsDirectory.path)[.systemFreeSize] as? NSNumber
        guard let available, available.int64Value >= bytes,
              available.int64Value - bytes >= 64 * 1024 * 1024 else { throw RemyModelStoreError.insufficientSpace }
    }
}
