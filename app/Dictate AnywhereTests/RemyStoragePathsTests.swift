import Foundation
import XCTest
@testable import Dictate_Anywhere

final class RemyStoragePathsTests: XCTestCase {
    private func fixture() throws -> (URL, RemyStoragePaths) {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("RemyStoragePathsTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return (directory, RemyStoragePaths(applicationSupportDirectory: directory, bundleIdentifier: "io.github.brysonjl.remy.dev"))
    }

    func testDevelopmentReleaseAndUpstreamDataAreIndependent() throws {
        let (temporary, development) = try fixture()
        defer { try? FileManager.default.removeItem(at: temporary) }
        let release = RemyStoragePaths(applicationSupportDirectory: temporary, bundleIdentifier: nil)
        XCTAssertEqual(development.applicationDirectory.lastPathComponent, "io.github.brysonjl.remy.dev")
        XCTAssertEqual(release.applicationDirectory.lastPathComponent, "io.github.brysonjl.remy")
        XCTAssertNotEqual(development.modelsDirectory, release.modelsDirectory)
        XCTAssertEqual(development.recoveryDirectory.lastPathComponent, "Recovery")
        XCTAssertEqual(development.modelsDirectory.deletingLastPathComponent(), development.applicationDirectory)
        XCTAssertEqual(development.recoveryDirectory.deletingLastPathComponent(), development.applicationDirectory)
        try development.requireOwnedModelPath(development.modelDirectory(named: "nemotron-streaming/1120ms"))
        XCTAssertThrowsError(try development.requireOwnedModelPath(release.modelsDirectory))
        XCTAssertThrowsError(try development.requireOwnedModelPath(temporary.appendingPathComponent("FluidAudio/Models")))
    }

    func testMalformedIdentityCannotEscapeApplicationSupport() throws {
        let (temporary, _) = try fixture()
        defer { try? FileManager.default.removeItem(at: temporary) }
        for identity in ["", ".", "..", "../FluidAudio", "/FluidAudio", "app\\escape"] {
            let paths = RemyStoragePaths(applicationSupportDirectory: temporary, bundleIdentifier: identity)
            XCTAssertEqual(paths.bundleIdentifier, RemyStoragePaths.fallbackBundleIdentifier)
            XCTAssertEqual(paths.applicationDirectory.deletingLastPathComponent(), temporary.standardizedFileURL.resolvingSymlinksInPath())
        }
    }

    func testModelDeletionRejectsRootSiblingAndTraversal() throws {
        let (temporary, paths) = try fixture()
        defer { try? FileManager.default.removeItem(at: temporary) }
        let sibling = paths.applicationDirectory.appendingPathComponent("Models-other/weights")
        let upstream = temporary.appendingPathComponent("FluidAudio/Models/weights")
        for url in [paths.modelsDirectory, sibling, upstream,
                    paths.modelsDirectory.appendingPathComponent("../Recovery") ] {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            XCTAssertThrowsError(try paths.removeModel(at: url))
            XCTAssertTrue(FileManager.default.fileExists(atPath: url.path))
        }
        XCTAssertThrowsError(try paths.requireOwnedModelPath(
            paths.modelsDirectory.appendingPathComponent("nested/../model")))
    }

    func testNestedRemovalPreservesRootOtherModelsAndUpstreamSentinel() throws {
        let (temporary, paths) = try fixture()
        defer { try? FileManager.default.removeItem(at: temporary) }
        let model = paths.modelDirectory(named: "nemotron/multilingual/1120ms")
        let other = paths.modelDirectory(named: "parakeet-v2")
        let upstream = temporary.appendingPathComponent("FluidAudio/Models/upstream-sentinel")
        for url in [model, other, upstream] {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            try Data("keep".utf8).write(to: url.appendingPathComponent("weights"))
        }
        try paths.removeModel(at: model)
        XCTAssertFalse(FileManager.default.fileExists(atPath: paths.modelDirectory(named: "nemotron").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: paths.modelsDirectory.path))
        XCTAssertEqual(try String(contentsOf: other.appendingPathComponent("weights"), encoding: .utf8), "keep")
        XCTAssertEqual(try String(contentsOf: upstream.appendingPathComponent("weights"), encoding: .utf8), "keep")
        // Pruning cannot climb through a directory belonging to another store.
        paths.removeEmptyModelParents(from: upstream)
        XCTAssertTrue(FileManager.default.fileExists(atPath: upstream.path))
    }

    func testSymlinkCannotRedirectModelOperationsToSharedCache() throws {
        let (temporary, paths) = try fixture()
        defer { try? FileManager.default.removeItem(at: temporary) }
        let upstream = temporary.appendingPathComponent("FluidAudio/Models", isDirectory: true)
        try FileManager.default.createDirectory(at: upstream, withIntermediateDirectories: true)
        try Data("keep".utf8).write(to: upstream.appendingPathComponent("weights"))
        try FileManager.default.createDirectory(at: paths.modelsDirectory, withIntermediateDirectories: true)
        let redirected = paths.modelDirectory(named: "redirected")
        try FileManager.default.createSymbolicLink(at: redirected, withDestinationURL: upstream)
        XCTAssertThrowsError(try paths.requireOwnedModelPath(redirected.appendingPathComponent("weights")))
        XCTAssertThrowsError(try paths.removeModel(at: redirected))
        XCTAssertEqual(try String(contentsOf: upstream.appendingPathComponent("weights"), encoding: .utf8), "keep")

        // Lexical normalization must not hide a link that the original
        // filesystem path traverses before climbing back to its parent.
        XCTAssertThrowsError(try paths.removeModel(at:
            redirected.appendingPathComponent("../upstream-victim")))
        let dangling = paths.modelDirectory(named: "dangling")
        try FileManager.default.createSymbolicLink(at: dangling,
            withDestinationURL: temporary.appendingPathComponent("missing/outside"))
        XCTAssertThrowsError(try paths.requireOwnedModelPath(dangling.appendingPathComponent("new-model")))

        try FileManager.default.removeItem(at: paths.modelsDirectory)
        try FileManager.default.createSymbolicLink(at: paths.modelsDirectory, withDestinationURL: upstream)
        XCTAssertThrowsError(try paths.requireOwnedModelPath(paths.modelsDirectory))
        XCTAssertThrowsError(try paths.removeModel(at: paths.modelDirectory(named: "weights")))
        XCTAssertEqual(try String(contentsOf: upstream.appendingPathComponent("weights"), encoding: .utf8), "keep")

        // A redirected application directory is also an escape even when its
        // Models child does not exist yet.
        try FileManager.default.removeItem(at: paths.applicationDirectory)
        try FileManager.default.createSymbolicLink(at: paths.applicationDirectory, withDestinationURL: upstream)
        XCTAssertThrowsError(try paths.requireOwnedModelPath(paths.modelDirectory(named: "new-model")))
        XCTAssertEqual(try String(contentsOf: upstream.appendingPathComponent("weights"), encoding: .utf8), "keep")
    }
}
