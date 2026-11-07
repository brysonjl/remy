import Foundation
import XCTest
@testable import Dictate_Anywhere

final class RemyStoredHistoryDeletionTests: XCTestCase {
    private func fixture() throws -> (URL, RemyStoragePaths) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("RemyDeletion-\(UUID())")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        let paths = RemyStoragePaths(applicationSupportDirectory: root, bundleIdentifier: "test.remy.deletion")
        try paths.prepareHistoryDirectory()
        return (root, paths)
    }

    @discardableResult private func write(_ url: URL) throws -> URL {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("synthetic transcript".utf8).write(to: url)
        return url
    }

    func testRecognizedSnapshotsAndOrphanRecoveryRemovedWithoutUnrelatedDeletion() throws {
        let (root, paths) = try fixture()
        let removed = try [
            write(paths.historyDirectory.appendingPathComponent("history-before-v2-\(UUID()).sqlite")),
            write(paths.historyDirectory.appendingPathComponent("Legacy Backups/legacy-history-\(String(repeating: "a", count: 64)).json")),
            write(paths.historyDirectory.appendingPathComponent("Legacy Backups/.legacy-history-\(UUID()).tmp")),
            write(paths.recoveryDirectory.appendingPathComponent("\(UUID()).caf")),
            write(paths.recoveryDirectory.appendingPathComponent("\(UUID()).json"))
        ]
        let preserved = try [write(paths.historyDatabase),
            write(paths.historyDirectory.appendingPathComponent("history-before-v2-unrecognized.sqlite")),
            write(paths.recoveryDirectory.appendingPathComponent("notes.json")),
            write(paths.modelsDirectory.appendingPathComponent("weights")),
            write(root.appendingPathComponent("other.app/History/history-before-v2-\(UUID()).sqlite"))]
        try paths.removeStoredHistoryArtifacts()
        for url in removed { XCTAssertFalse(FileManager.default.fileExists(atPath: url.path)) }
        for url in preserved { XCTAssertTrue(FileManager.default.fileExists(atPath: url.path)) }
        // A new value represents reopening the same store; deletion remains idempotent.
        let reopened = RemyStoragePaths(applicationSupportDirectory: root, bundleIdentifier: paths.bundleIdentifier)
        try reopened.removeStoredHistoryArtifacts()
    }

    func testLeafLinkFailurePreservesExternalDataAndCanBeRetried() throws {
        let (root, paths) = try fixture()
        let outside = try write(root.appendingPathComponent("external/sentinel"))
        let link = paths.historyDirectory.appendingPathComponent("history-before-v2-\(UUID()).sqlite")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: outside)
        let recovery = try write(paths.recoveryDirectory.appendingPathComponent("\(UUID()).caf"))
        XCTAssertThrowsError(try paths.removeStoredHistoryArtifacts())
        XCTAssertTrue(FileManager.default.fileExists(atPath: outside.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: recovery.path))
        XCTAssertNotNil(try? FileManager.default.destinationOfSymbolicLink(atPath: link.path))
        try FileManager.default.removeItem(at: link)
        try paths.removeStoredHistoryArtifacts()
    }

    func testDirectoryLinksAndRecognizedDirectoriesNeverRecurse() throws {
        let (root, paths) = try fixture()
        let outside = try write(root.appendingPathComponent("external/\(UUID()).caf"))
        try FileManager.default.createSymbolicLink(at: paths.recoveryDirectory,
                                                  withDestinationURL: outside.deletingLastPathComponent())
        let fakeBackup = paths.historyDirectory.appendingPathComponent("history-before-v2-\(UUID()).sqlite")
        let nested = try write(fakeBackup.appendingPathComponent("keep"))
        XCTAssertThrowsError(try paths.removeStoredHistoryArtifacts())
        XCTAssertTrue(FileManager.default.fileExists(atPath: outside.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: nested.path))
    }

    func testApplicationDirectoryLinkAndHardLinksAreRejected() throws {
        let (root, paths) = try fixture()
        let outside = try write(root.appendingPathComponent("outside"))
        let backup = paths.historyDirectory.appendingPathComponent("history-before-v2-\(UUID()).sqlite")
        try FileManager.default.linkItem(at: outside, to: backup)
        XCTAssertThrowsError(try paths.removeStoredHistoryArtifacts())
        XCTAssertTrue(FileManager.default.fileExists(atPath: outside.path))
        try FileManager.default.removeItem(at: paths.applicationDirectory)
        try FileManager.default.createSymbolicLink(at: paths.applicationDirectory,
                                                  withDestinationURL: root)
        XCTAssertThrowsError(try paths.removeStoredHistoryArtifacts())
        XCTAssertTrue(FileManager.default.fileExists(atPath: outside.path))
    }
}
