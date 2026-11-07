import Foundation
import XCTest
@testable import Dictate_Anywhere

@MainActor
final class RemyModelStoreTests: XCTestCase {
    private func fixture() throws -> (root: URL, paths: RemyStoragePaths) {
        let root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("RemyModelStoreTests-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return (root, RemyStoragePaths(applicationSupportDirectory: root, bundleIdentifier: "test.remy.models"))
    }

    private func expect(_ error: RemyModelStoreError, operation: () async throws -> Void,
                        file: StaticString = #filePath, line: UInt = #line) async {
        do { try await operation(); XCTFail("Expected \(error)", file: file, line: line) }
        catch let actual as RemyModelStoreError { XCTAssertEqual(actual, error, file: file, line: line) }
        catch { XCTFail("Unexpected error type: \(error)", file: file, line: line) }
    }

    func testInstallDoesNotActivateAndInstalledOnlyResolutionDoesNotDownload() async throws {
        let fixture = try fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let transport = FixtureModelTransport()
        let store = RemyModelStore(paths: fixture.paths, transport: transport)
        let manifest = ModelStoreFixture.manifest()
        let installed = try await store.install(manifest)
        await expect(.notActive) { _ = try await store.acquireInstalled(manifest) }
        let status = try await store.inspect(manifest)
        XCTAssertEqual(status, .installed(installed))
        XCTAssertNil(try RemyModelStore.activeDirectory(for: manifest, paths: fixture.paths))
        try await store.activate(manifest)
        XCTAssertEqual(try RemyModelStore.activeDirectory(for: manifest, paths: fixture.paths), installed.directory)
        let lease = try await store.acquireInstalled(manifest)
        XCTAssertEqual(lease.directory, installed.directory)
        await store.release(lease)
        let count = await transport.count
        XCTAssertEqual(count, 1)
    }

    func testCorruptAndIncompletePayloadsNeverActivate() async throws {
        for payload in [Data("corrupt".utf8), Data()] {
            let fixture = try fixture()
            defer { try? FileManager.default.removeItem(at: fixture.root) }
            let store = RemyModelStore(paths: fixture.paths, transport: FixtureModelTransport(payload: payload))
            let manifest = ModelStoreFixture.manifest()
            await expect(.integrityMismatch(manifest.files[0].path)) { _ = try await store.install(manifest) }
            let status = try await store.inspect(manifest)
            XCTAssertEqual(status, .missing)
            let staging = fixture.paths.modelsDirectory.appendingPathComponent(".staging")
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: staging.path), [])
        }
    }

    func testExtraFilesAndSymlinkPayloadsAreRejected() async throws {
        for mode in [FixtureModelTransport.Mode.extra, .symlink] {
            let fixture = try fixture()
            defer { try? FileManager.default.removeItem(at: fixture.root) }
            let store = RemyModelStore(paths: fixture.paths, transport: FixtureModelTransport(mode: mode))
            do { _ = try await store.install(ModelStoreFixture.manifest()); XCTFail("Unsafe inventory accepted") }
            catch { /* Both path confinement and exact inventory must reject. */ }
            let status = try await store.inspect(ModelStoreFixture.manifest())
            XCTAssertEqual(status, .missing)
        }
    }

    func testCancellationDuringInstallRetainsPreviousActiveBundle() async throws {
        let fixture = try fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let gate = FixtureModelTransferGate()
        let transport = FixtureModelTransport(gate: gate)
        let store = RemyModelStore(paths: fixture.paths, transport: transport)
        let first = ModelStoreFixture.manifest()
        _ = try await store.install(first)
        try await store.activate(first)
        await gate.arm()
        let second = ModelStoreFixture.manifest(revision: String(repeating: "c", count: 40))
        let task = Task { try await store.install(second) }
        await gate.waitUntilPaused()
        await expect(.busy) { _ = try await store.acquireInstalled(first) }
        task.cancel()
        await gate.release()
        do { _ = try await task.value; XCTFail("Cancelled installation completed") }
        catch is CancellationError {} catch { XCTFail("Unexpected cancellation error: \(error)") }
        let lease = try await store.acquireInstalled(first)
        XCTAssertEqual(lease.installed.revision, first.revision)
        await store.release(lease)
        let status = try await store.inspect(second)
        XCTAssertEqual(status, .missing)
    }

    func testLeasesBlockSwitchRollbackAndRemoval() async throws {
        let fixture = try fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let store = RemyModelStore(paths: fixture.paths, transport: FixtureModelTransport())
        let first = ModelStoreFixture.manifest()
        let second = ModelStoreFixture.manifest(revision: String(repeating: "c", count: 40))
        _ = try await store.install(first)
        _ = try await store.install(second)
        try await store.activate(first)
        let lease = try await store.acquireInstalled(first)
        await expect(.leasedRevision) { _ = try await store.activate(second) }
        await expect(.leasedRevision) { _ = try await store.rollback(modelID: first.modelID) }
        await expect(.leasedRevision) { try await store.remove(first) }
        await store.release(lease)
        await store.release(lease) // Repeated release cannot affect another lease.
        try await store.activate(second)
        let previous = try await store.rollback(modelID: first.modelID)
        XCTAssertEqual(previous.revision, first.revision)
        await expect(.activeRevision) { try await store.remove(first) }
        try await store.deactivate(modelID: first.modelID)
        try await store.remove(first)
        let status = try await store.inspect(first)
        XCTAssertEqual(status, .missing)
    }

    func testOfflineIntegrityRejectsPostInstallCorruption() async throws {
        let fixture = try fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let store = RemyModelStore(paths: fixture.paths, transport: FixtureModelTransport())
        let manifest = ModelStoreFixture.manifest()
        let installed = try await store.install(manifest)
        try await store.activate(manifest)
        try Data("corrupt".utf8).write(to: installed.directory.appendingPathComponent(manifest.files[0].path))
        await expect(.integrityMismatch(manifest.files[0].path)) { _ = try await store.acquireInstalled(manifest) }
    }

    func testExplicitAdoptionPreservesLegacyAndRejectsSharedOrSiblingSources() async throws {
        let fixture = try fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let transport = FixtureModelTransport()
        let store = RemyModelStore(paths: fixture.paths, transport: transport)
        let manifest = ModelStoreFixture.manifest()
        let legacy = fixture.paths.modelDirectory(named: manifest.modelID)
        let artifact = legacy.appendingPathComponent(manifest.files[0].path)
        try FileManager.default.createDirectory(at: artifact.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("weights".utf8).write(to: artifact)
        let installed = try await store.adoptLegacy(manifest, from: legacy)
        XCTAssertNotEqual(installed.directory, legacy)
        XCTAssertEqual(try Data(contentsOf: artifact), Data("weights".utf8))
        let count = await transport.count
        XCTAssertEqual(count, 0)
        let sibling = fixture.paths.modelDirectory(named: "some-other-model")
        await expect(.invalidLegacyLocation) { _ = try await store.adoptLegacy(manifest, from: sibling) }
        do { _ = try await store.adoptLegacy(manifest, from: fixture.root); XCTFail("Outside source accepted") }
        catch {}
    }

    func testExplicitRemovalRecoversOrphanWithMissingOrCorruptRegistration() async throws {
        for corruptMetadata in [false, true] {
            let fixture = try fixture()
            defer { try? FileManager.default.removeItem(at: fixture.root) }
            let store = RemyModelStore(paths: fixture.paths, transport: FixtureModelTransport())
            let manifest = ModelStoreFixture.manifest()
            let installed = try await store.install(manifest)
            let metadata = installed.directory.deletingLastPathComponent()
                .appendingPathComponent(manifest.revision + ".json")
            if corruptMetadata {
                try Data("invalid registration".utf8).write(to: metadata)
            } else {
                try FileManager.default.removeItem(at: metadata)
            }
            // Corrupt payload cannot be repaired by adoption/install verification.
            try Data("corrupt".utf8).write(to: installed.directory.appendingPathComponent(manifest.files[0].path))
            try await store.remove(manifest)
            XCTAssertFalse(FileManager.default.fileExists(atPath: installed.directory.path))
            XCTAssertFalse(FileManager.default.fileExists(atPath: metadata.path))
        }
    }

    func testRemovalProtectsActiveIdentityAndClearsRollbackDespiteChangedDigest() async throws {
        let fixture = try fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let store = RemyModelStore(paths: fixture.paths, transport: FixtureModelTransport())
        let manifest = ModelStoreFixture.manifest()
        _ = try await store.install(manifest)
        try await store.activate(manifest)
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: manifest.encoded()) as? [String: Any])
        object["license"] = "changed-metadata"
        let changed = try JSONDecoder().decode(RemyModelManifest.self, from: JSONSerialization.data(withJSONObject: object))
        try changed.validate()
        await expect(.activeRevision) { try await store.remove(changed) }
        try await store.deactivate(modelID: manifest.modelID)
        try await store.remove(changed)
        await expect(.noRollback) { _ = try await store.rollback(modelID: manifest.modelID) }
    }

    func testActivationWriteFailurePreservesWorkingPointer() async throws {
        let fixture = try fixture()
        defer { try? FileManager.default.removeItem(at: fixture.root) }
        let writer = FixtureActivationWriter()
        let store = RemyModelStore(paths: fixture.paths, transport: FixtureModelTransport(), activationWriter: writer.write)
        let first = ModelStoreFixture.manifest()
        let second = ModelStoreFixture.manifest(revision: String(repeating: "c", count: 40))
        _ = try await store.install(first)
        _ = try await store.install(second)
        try await store.activate(first)
        writer.setFailure(true)
        do { _ = try await store.activate(second); XCTFail("Activation unexpectedly succeeded") }
        catch {}
        let lease = try await store.acquireInstalled(first)
        XCTAssertEqual(lease.installed.revision, first.revision)
        await store.release(lease)
    }
}

private actor FixtureModelTransport: RemyModelDownloadTransport {
    enum Mode: Sendable { case normal, extra, symlink }
    private let payload: Data
    private let mode: Mode
    private let gate: FixtureModelTransferGate?
    private(set) var count = 0
    init(payload: Data = Data("weights".utf8), mode: Mode = .normal, gate: FixtureModelTransferGate? = nil) {
        self.payload = payload; self.mode = mode; self.gate = gate
    }
    func download(_ artifact: RemyModelManifest.Artifact, to destination: URL,
                  progress: @escaping @Sendable (Int64) -> Void) async throws {
        count += 1
        if let gate { await gate.pauseIfArmed() }
        if mode == .symlink {
            try FileManager.default.createSymbolicLink(at: destination, withDestinationURL: destination.deletingLastPathComponent())
        } else {
            try payload.write(to: destination)
        }
        if mode == .extra { try Data().write(to: destination.deletingLastPathComponent().appendingPathComponent("extra.bin")) }
        progress(Int64(payload.count))
    }
}

private actor FixtureModelTransferGate {
    private var armed = false
    private var pauseWaiter: CheckedContinuation<Void, Never>?
    private var startedWaiter: CheckedContinuation<Void, Never>?
    private var paused = false
    func arm() { armed = true }
    func pauseIfArmed() async {
        guard armed else { return }
        paused = true
        startedWaiter?.resume(); startedWaiter = nil
        await withCheckedContinuation { pauseWaiter = $0 }
    }
    func waitUntilPaused() async {
        guard !paused else { return }
        await withCheckedContinuation { startedWaiter = $0 }
    }
    func release() { pauseWaiter?.resume(); pauseWaiter = nil }
}

private nonisolated final class FixtureActivationWriter: @unchecked Sendable {
    private let lock = NSLock()
    private var shouldFail = false
    func setFailure(_ value: Bool) { lock.withLock { shouldFail = value } }
    func write(_ data: Data, _ url: URL) throws {
        if lock.withLock({ shouldFail }) { throw CocoaError(.fileWriteNoPermission) }
        try data.write(to: url, options: .atomic)
    }
}
