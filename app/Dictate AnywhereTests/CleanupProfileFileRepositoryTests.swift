import XCTest
import Foundation
import Darwin
@testable import Dictate_Anywhere

@MainActor
final class CleanupProfileFileRepositoryTests: XCTestCase {
    private var fixture: URL!
    private var root: URL { fixture.appendingPathComponent("profiles", isDirectory: true) }
    private var file: URL { root.appendingPathComponent("profiles.json") }
    override func setUpWithError() throws {
        fixture = FileManager.default.temporaryDirectory.appendingPathComponent("remy-profiles-\(UUID())", isDirectory: true)
        try FileManager.default.createDirectory(at: fixture, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
    }
    override func tearDownWithError() throws { try FileManager.default.removeItem(at: fixture) }
    private func profile(id: UUID = UUID(), revision: UInt64 = 1, name: String = "Selected cloud",
                         prompt: String = "Preserve exact words.\n中文 👩🏽‍💻 e\u{301}",
                         credentialEndpoint: String = "https://ollama.com/v1") throws -> CleanupProfile {
        .init(id: id, revision: revision, name: name, adapter: .openAICompatible,
              baseURL: "https://ollama.com/v1", model: "gemma4:31b", prompt: prompt, promptVersion: 1,
              timeoutSeconds: 8, capabilities: .init(transport: .completeJSON, discovery: false,
                structuredOutput: false, thinkingControl: false), routePolicy: .ordinary,
              allowInsecureRemoteHTTP: false,
              credential: .init(id: UUID(), endpoint: try CleanupEndpointIdentity(baseURL: credentialEndpoint)))
    }
    private func collection(_ profile: CleanupProfile, revision: UInt64 = 1) -> CleanupProfileCollection {
        .init(revision: revision, profiles: [profile], selectedID: profile.id, completedLegacyMigrations: ["legacy-v1"])
    }
    private func overwrite(_ data: Data) throws {
        try data.write(to: file)
        guard chmod(file.path, 0o600) == 0 else { throw NSError(domain: "Fixture", code: 1) }
    }

    func testRestartRoundTripPreservesExactCurrentURLModelPromptAndPrivateModes() throws {
        let value = collection(try profile())
        let first = try CleanupProfileFileRepository(rootURL: root)
        XCTAssertEqual(try first.load(), .empty)
        try first.commit(value, expectedRevision: 0)
        let second = try CleanupProfileFileRepository(rootURL: root)
        let loaded = try second.load()
        XCTAssertEqual(loaded, value)
        XCTAssertEqual(Array(loaded.profiles[0].prompt.utf8), Array(value.profiles[0].prompt.utf8))
        XCTAssertEqual(loaded.profiles[0].baseURL, "https://ollama.com/v1")
        XCTAssertEqual(loaded.profiles[0].model, "gemma4:31b")
        for (url, mode) in [(root, 0o700), (file, 0o600), (root.appendingPathComponent(".profiles.lock"), 0o600)] {
            let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
            XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, mode)
        }
    }

    func testTwoInstancesRejectStaleCASWithoutLosingTheNewerCommit() throws {
        let first = try CleanupProfileFileRepository(rootURL: root), second = try CleanupProfileFileRepository(rootURL: root)
        let old = try profile()
        try first.commit(collection(old), expectedRevision: 0)
        XCTAssertEqual(try second.load().revision, 1)
        let updated = try profile(id: old.id, revision: 2, name: "New name")
        try second.commit(collection(updated, revision: 2), expectedRevision: 1)
        let newer = try Data(contentsOf: file)
        XCTAssertThrowsError(try first.commit(collection(updated, revision: 2), expectedRevision: 1)) {
            XCTAssertEqual($0 as? CleanupProfileFileRepository.RepositoryError, .conflict)
        }
        XCTAssertEqual(try Data(contentsOf: file), newer)
    }

    func testPartialInjectedWriteFailureAndSilentShortWriteRetainOldFile() throws {
        let repository = try CleanupProfileFileRepository(rootURL: root)
        let old = try profile()
        try repository.commit(collection(old), expectedRevision: 0)
        let before = try Data(contentsOf: file)
        let update = collection(try profile(id: old.id, revision: 2), revision: 2)
        enum FixtureFailure: Error { case failed }
        for shouldThrow in [true, false] {
            let broken = try CleanupProfileFileRepository(rootURL: root, atomicWriter: { _, descriptor in
                _ = Data("partial".utf8).withUnsafeBytes { Darwin.write(descriptor, $0.baseAddress, $0.count) }
                if shouldThrow { throw FixtureFailure.failed }
            })
            XCTAssertThrowsError(try broken.commit(update, expectedRevision: 1))
            XCTAssertEqual(try Data(contentsOf: file), before)
            XCTAssertEqual(try repository.load().revision, 1)
        }
        XCTAssertFalse(try FileManager.default.contentsOfDirectory(atPath: root.path).contains { $0.hasSuffix(".tmp") })
    }

    func testReplacedStagingNameCannotPublishSymlinkOrDifferentBytes() throws {
        let normal = try CleanupProfileFileRepository(rootURL: root)
        let old = try profile()
        try normal.commit(collection(old), expectedRevision: 0)
        let before = try Data(contentsOf: file)
        let update = collection(try profile(id: old.id, revision: 2), revision: 2)
        let stagingRoot = root
        let outside = fixture.appendingPathComponent("outside-sentinel")
        let sentinel = Data("unrelated private bytes".utf8)
        try sentinel.write(to: outside)
        for useSymlink in [false, true] {
            let retained = fixture.appendingPathComponent("retained-stage-\(UUID())")
            let replaced = try CleanupProfileFileRepository(rootURL: root, atomicWriter: { data, descriptor in
                let stage = try XCTUnwrap(FileManager.default.contentsOfDirectory(at: stagingRoot,
                    includingPropertiesForKeys: nil).first { $0.lastPathComponent.hasSuffix(".tmp") })
                try FileManager.default.moveItem(at: stage, to: retained)
                if useSymlink {
                    try FileManager.default.createSymbolicLink(at: stage, withDestinationURL: outside)
                } else {
                    try sentinel.write(to: stage)
                    XCTAssertEqual(chmod(stage.path, 0o600), 0)
                }
                // The validated descriptor still receives every expected byte;
                // only the directory entry that rename would publish differs.
                let written = data.withUnsafeBytes { Darwin.write(descriptor, $0.baseAddress, $0.count) }
                XCTAssertEqual(written, data.count)
            })
            XCTAssertThrowsError(try replaced.commit(update, expectedRevision: 1)) {
                XCTAssertEqual($0 as? CleanupProfileFileRepository.RepositoryError, .unsafeFile)
            }
            XCTAssertEqual(try Data(contentsOf: file), before)
            XCTAssertEqual(try normal.load().revision, 1)
            XCTAssertEqual(try Data(contentsOf: outside), sentinel)
            XCTAssertFalse(try FileManager.default.contentsOfDirectory(atPath: root.path).contains { $0.hasSuffix(".tmp") })
        }
    }

    func testMalformedAndNewerSchemaNeverGetOverwritten() throws {
        let repository = try CleanupProfileFileRepository(rootURL: root)
        _ = try repository.load()
        for payload in [Data("not JSON".utf8), Data(#"{"schemaVersion":99}"#.utf8)] {
            try overwrite(payload)
            XCTAssertThrowsError(try repository.load())
            XCTAssertThrowsError(try repository.commit(collection(try profile()), expectedRevision: 0))
            XCTAssertEqual(try Data(contentsOf: file), payload)
        }
    }

    func testDuplicateSelectedEndpointAndRevisionViolationsDoNotOverwrite() throws {
        let repository = try CleanupProfileFileRepository(rootURL: root)
        let old = try profile()
        try repository.commit(collection(old), expectedRevision: 0)
        let before = try Data(contentsOf: file)
        let invalid = [
            CleanupProfileCollection(revision: 2, profiles: [old, old], selectedID: old.id, completedLegacyMigrations: ["legacy-v1"]),
            .init(revision: 2, profiles: [old], selectedID: UUID(), completedLegacyMigrations: ["legacy-v1"]),
            collection(try profile(id: old.id, revision: 2, credentialEndpoint: "https://another.invalid/v1"), revision: 2),
            collection(try profile(id: old.id, revision: 0), revision: 2),
            collection(try profile(id: old.id, revision: 1, name: "Changed without increment"), revision: 2),
            collection(try profile(id: old.id, revision: 3), revision: 2),
            .init(revision: 2, profiles: [old], selectedID: old.id, completedLegacyMigrations: [])
        ]
        for value in invalid {
            XCTAssertThrowsError(try repository.commit(value, expectedRevision: 1))
            XCTAssertEqual(try Data(contentsOf: file), before)
        }
    }

    func testOversizedEncodingRetainsOldFile() throws {
        let repository = try CleanupProfileFileRepository(rootURL: root)
        let old = try profile()
        try repository.commit(collection(old), expectedRevision: 0)
        let before = try Data(contentsOf: file)
        let profiles = try (0..<17).map { _ in try profile(prompt: String(repeating: "a", count: 65_536)) }
        XCTAssertThrowsError(try repository.commit(.init(revision: 2, profiles: profiles, selectedID: nil,
            completedLegacyMigrations: ["legacy-v1"]), expectedRevision: 1))
        XCTAssertEqual(try Data(contentsOf: file), before)
    }

    func testRootAndAncestorSymlinksAreRefusedWithoutChangingOutsideFiles() throws {
        let outside = fixture.appendingPathComponent("outside", isDirectory: true)
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        let sentinel = outside.appendingPathComponent("sentinel")
        try Data("sentinel".utf8).write(to: sentinel)
        try FileManager.default.createSymbolicLink(at: root, withDestinationURL: outside)
        XCTAssertThrowsError(try CleanupProfileFileRepository(rootURL: root).load())
        let descendant = root.appendingPathComponent("new-child", isDirectory: true)
        XCTAssertThrowsError(try CleanupProfileFileRepository(rootURL: descendant).load())
        XCTAssertEqual(try Data(contentsOf: sentinel), Data("sentinel".utf8))
        XCTAssertFalse(FileManager.default.fileExists(atPath: outside.appendingPathComponent("new-child").path))
    }

    func testSymlinkHardlinkFIFOAndPublicFileModesAreRefused() throws {
        let repository = try CleanupProfileFileRepository(rootURL: root)
        _ = try repository.load()
        let outside = fixture.appendingPathComponent("outside.json")
        try Data("sentinel".utf8).write(to: outside)
        XCTAssertEqual(chmod(outside.path, 0o600), 0)
        try FileManager.default.createSymbolicLink(at: file, withDestinationURL: outside)
        XCTAssertThrowsError(try repository.load())
        try FileManager.default.removeItem(at: file)
        XCTAssertEqual(link(outside.path, file.path), 0)
        XCTAssertThrowsError(try repository.load())
        try FileManager.default.removeItem(at: file)
        XCTAssertEqual(mkfifo(file.path, 0o600), 0)
        XCTAssertThrowsError(try repository.load())
        try FileManager.default.removeItem(at: file)
        try overwrite(Data("{}".utf8))
        XCTAssertEqual(chmod(file.path, 0o644), 0)
        XCTAssertThrowsError(try repository.load())
        XCTAssertEqual(try Data(contentsOf: outside), Data("sentinel".utf8))
    }

    func testDirectoryReplacementDuringStagingCannotPublishIntoOutsideDirectory() throws {
        let normal = try CleanupProfileFileRepository(rootURL: root)
        let old = try profile()
        try normal.commit(collection(old), expectedRevision: 0)
        let before = try Data(contentsOf: file)
        let outside = fixture.appendingPathComponent("outside", isDirectory: true)
        let retained = fixture.appendingPathComponent("retained", isDirectory: true)
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        let sentinel = outside.appendingPathComponent("profiles.json")
        try Data("outside sentinel".utf8).write(to: sentinel)
        let originalRoot = root
        let replaced = try CleanupProfileFileRepository(rootURL: root, atomicWriter: { data, descriptor in
            try FileManager.default.moveItem(at: originalRoot, to: retained)
            try FileManager.default.createSymbolicLink(at: originalRoot, withDestinationURL: outside)
            _ = data.withUnsafeBytes { Darwin.write(descriptor, $0.baseAddress, $0.count) }
        })
        XCTAssertThrowsError(try replaced.commit(collection(try profile(id: old.id, revision: 2), revision: 2), expectedRevision: 1))
        XCTAssertEqual(try Data(contentsOf: sentinel), Data("outside sentinel".utf8))
        XCTAssertEqual(try Data(contentsOf: retained.appendingPathComponent("profiles.json")), before)
    }

    func testLockReplacementDuringStagingCannotBypassCASLock() throws {
        let normal = try CleanupProfileFileRepository(rootURL: root)
        let old = try profile()
        try normal.commit(collection(old), expectedRevision: 0)
        let before = try Data(contentsOf: file)
        let lockURL = root.appendingPathComponent(".profiles.lock")
        let replaced = try CleanupProfileFileRepository(rootURL: root, atomicWriter: { data, descriptor in
            try FileManager.default.removeItem(at: lockURL)
            try Data().write(to: lockURL)
            _ = chmod(lockURL.path, 0o600)
            _ = data.withUnsafeBytes { Darwin.write(descriptor, $0.baseAddress, $0.count) }
        })
        XCTAssertThrowsError(try replaced.commit(collection(try profile(id: old.id, revision: 2), revision: 2), expectedRevision: 1))
        XCTAssertEqual(try Data(contentsOf: file), before)
    }

    func testInvalidDecodedCollectionIsRejectedOnLoad() throws {
        let repository = try CleanupProfileFileRepository(rootURL: root)
        try repository.commit(collection(try profile()), expectedRevision: 0)
        let original = try Data(contentsOf: file)
        for violation in 0..<3 {
            var envelope = try XCTUnwrap(JSONSerialization.jsonObject(with: original) as? [String: Any])
            var collection = try XCTUnwrap(envelope["collection"] as? [String: Any])
            var profiles = try XCTUnwrap(collection["profiles"] as? [[String: Any]])
            if violation == 0 { collection["selectedID"] = UUID().uuidString }
            if violation == 1 { profiles.append(profiles[0]); collection["profiles"] = profiles }
            if violation == 2 {
                var credential = try XCTUnwrap(profiles[0]["credential"] as? [String: Any])
                credential["endpoint"] = "https://different.invalid/v1"
                profiles[0]["credential"] = credential
                collection["profiles"] = profiles
            }
            envelope["collection"] = collection
            let invalid = try JSONSerialization.data(withJSONObject: envelope)
            try overwrite(invalid)
            XCTAssertThrowsError(try repository.load())
            XCTAssertEqual(try Data(contentsOf: file), invalid)
        }
    }

    func testReplacedLockAndExternalLockPreventUncoordinatedAccess() throws {
        let repository = try CleanupProfileFileRepository(rootURL: root)
        _ = try repository.load()
        let lockURL = root.appendingPathComponent(".profiles.lock")
        let descriptor = open(lockURL.path, O_RDWR | O_NOFOLLOW | O_CLOEXEC)
        XCTAssertGreaterThanOrEqual(descriptor, 0)
        defer { _ = flock(descriptor, LOCK_UN); close(descriptor) }
        XCTAssertEqual(flock(descriptor, LOCK_EX | LOCK_NB), 0)
        XCTAssertThrowsError(try repository.load()) {
            XCTAssertEqual($0 as? CleanupProfileFileRepository.RepositoryError, .lockUnavailable)
        }
        _ = flock(descriptor, LOCK_UN)
        try FileManager.default.removeItem(at: lockURL)
        try FileManager.default.createSymbolicLink(at: lockURL, withDestinationURL: file)
        XCTAssertThrowsError(try repository.load())
    }
}
