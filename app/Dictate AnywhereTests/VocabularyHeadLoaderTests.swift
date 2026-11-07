import CoreML
import Foundation
import XCTest
@testable import Dictate_Anywhere

@MainActor
final class VocabularyHeadLoaderTests: XCTestCase {
    func testNormalPreparationRejectsMissingHeadWithoutCreatingOrDownloadingFiles() async throws {
        let root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("RemyVocabularyTests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let paths = RemyStoragePaths(applicationSupportDirectory: root, bundleIdentifier: "test.remy.vocabulary")
        let directory = paths.modelDirectory(named: "vocabulary")
        do {
            _ = try await VocabularyHeadLoader.load(configuration: MLModelConfiguration(), directory: directory, paths: paths)
            XCTFail("Missing optional assets must not prepare or download")
        } catch { XCTAssertTrue(error.localizedDescription.contains("not installed")) }
        XCTAssertFalse(FileManager.default.fileExists(atPath: directory.path))
    }

    func testNormalPreparationPreservesIncompleteHeadWithoutRepairOrDeletion() async throws {
        let root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("RemyVocabularyTests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let paths = RemyStoragePaths(applicationSupportDirectory: root, bundleIdentifier: "test.remy.vocabulary")
        let directory = paths.modelDirectory(named: "vocabulary")
        let head = directory.appendingPathComponent("CtcHead.mlmodelc")
        try FileManager.default.createDirectory(at: head, withIntermediateDirectories: true)
        let partial = head.appendingPathComponent("weight.partial")
        try Data("unchanged partial".utf8).write(to: partial)
        do {
            _ = try await VocabularyHeadLoader.load(configuration: MLModelConfiguration(), directory: directory, paths: paths)
            XCTFail("Incomplete optional assets must not prepare or repair")
        } catch { XCTAssertTrue(error.localizedDescription.contains("not installed")) }
        XCTAssertEqual(try Data(contentsOf: partial), Data("unchanged partial".utf8))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: head.path), ["weight.partial"])
    }
}
