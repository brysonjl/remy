import Foundation
import XCTest
@testable import Dictate_Anywhere

@MainActor
final class RemyHistoryBootstrapTests: XCTestCase {
    private func fixture() throws -> (UserDefaults, RemyStoragePaths, URL) {
        let suite = "RemyHistoryBootstrapTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let paths = RemyStoragePaths(applicationSupportDirectory: root, bundleIdentifier: "test.remy.history")
        addTeardownBlock {
            defaults.removePersistentDomain(forName: suite)
            try? FileManager.default.removeItem(at: root)
        }
        return (defaults, paths, root)
    }

    func testPreparationCreatesPrivateOwnedDirectoriesAndIsIdempotent() throws {
        let (_, paths, _) = try fixture()
        try paths.prepareHistoryDirectory()
        try paths.prepareHistoryDirectory()
        for directory in [paths.applicationDirectory, paths.historyDirectory] {
            let attributes = try FileManager.default.attributesOfItem(atPath: directory.path)
            XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o700)
            XCTAssertEqual(attributes[.type] as? FileAttributeType, .typeDirectory)
        }
        XCTAssertEqual(paths.historyDatabase.deletingLastPathComponent(), paths.historyDirectory)
    }

    func testAppAndHistorySymlinksAreRejectedWithoutTouchingDestination() throws {
        for appLink in [true, false] {
            let (defaults, paths, root) = try fixture()
            let target = root.appendingPathComponent("outside")
            try FileManager.default.createDirectory(at: target, withIntermediateDirectories: false)
            if !appLink {
                try FileManager.default.createDirectory(at: paths.applicationDirectory, withIntermediateDirectories: false)
            }
            try FileManager.default.createSymbolicLink(at: appLink ? paths.applicationDirectory : paths.historyDirectory,
                                                      withDestinationURL: target)
            let original = Data("untouched source".utf8)
            defaults.set(original, forKey: "transcriptHistory")
            XCTAssertThrowsError(try paths.prepareHistoryDirectory())
            XCTAssertNotNil(RemyHistoryBootstrap.captureLegacy(in: defaults, paths: paths))
            XCTAssertEqual(defaults.data(forKey: "transcriptHistory"), original)
            XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: target.path), [])
        }
    }

    func testDatabaseAndRecoveryLinksAreRejectedWithoutChangingLinkedBytesOrPreferences() throws {
        for name in ["history.sqlite", "history.sqlite-journal", "history.sqlite-wal", "history.sqlite-shm"] {
            for hardLink in [false, true] {
                let (defaults, paths, root) = try fixture()
                try paths.prepareHistoryDirectory()
                let original = Data("synthetic unchanged bytes".utf8)
                defaults.set(original, forKey: "transcriptHistory")
                let target = root.appendingPathComponent("external")
                try original.write(to: target)
                let link = paths.historyDirectory.appendingPathComponent(name)
                if hardLink {
                    try FileManager.default.linkItem(at: target, to: link)
                } else {
                    try FileManager.default.createSymbolicLink(at: link, withDestinationURL: target)
                }
                XCTAssertThrowsError(try paths.prepareHistoryDirectory(), name)
                XCTAssertNotNil(RemyHistoryBootstrap.captureLegacy(in: defaults, paths: paths), name)
                XCTAssertEqual(try Data(contentsOf: target), original)
                XCTAssertEqual(defaults.data(forKey: "transcriptHistory"), original)
            }
        }
    }

    func testBootstrapPreservesAllLegacyBytesBeforeAnySettingsProjection() throws {
        let (defaults, paths, _) = try fixture()
        let entries = (0..<75).map {
            TranscriptHistoryEntry(id: UUID(), text: "原文 🦾 \($0)",
                                   createdAt: Date(timeIntervalSince1970: Double($0)), rawText: "raw \($0)")
        }
        let original = try JSONEncoder().encode(entries)
        defaults.set(original, forKey: "transcriptHistory")
        XCTAssertNil(RemyHistoryBootstrap.captureLegacy(in: defaults, paths: paths))
        let first = try XCTUnwrap(RemyHistoryBootstrap.legacy)
        XCTAssertEqual(first.data, original)
        XCTAssertEqual(try Data(contentsOf: first.backupURL), original)
        XCTAssertEqual(defaults.data(forKey: "transcriptHistory"), original)
        XCTAssertEqual(try JSONDecoder().decode([TranscriptHistoryEntry].self, from: first.data).count, 75)
        XCTAssertNil(RemyHistoryBootstrap.captureLegacy(in: defaults, paths: paths))
        XCTAssertEqual(RemyHistoryBootstrap.legacy?.backupURL, first.backupURL)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: first.backupURL.deletingLastPathComponent().path).count, 1)
    }

    func testMissingLegacyIsIdempotentAndMalformedPreferenceRemainsUnchanged() throws {
        let (defaults, paths, _) = try fixture()
        for _ in 0..<2 {
            XCTAssertNil(RemyHistoryBootstrap.captureLegacy(in: defaults, paths: paths))
            XCTAssertNil(RemyHistoryBootstrap.legacy)
            XCTAssertNil(defaults.object(forKey: "transcriptHistory"))
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: paths.historyDirectory.appendingPathComponent("Legacy Backups").path))
        defaults.set("malformed synthetic preference", forKey: "transcriptHistory")
        XCTAssertNotNil(RemyHistoryBootstrap.captureLegacy(in: defaults, paths: paths))
        XCTAssertEqual(defaults.string(forKey: "transcriptHistory"), "malformed synthetic preference")
    }
}
