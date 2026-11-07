import CryptoKit
import Foundation
import XCTest
@testable import Dictate_Anywhere

final class RemyModelManifestTests: XCTestCase {
    func testReviewedV2ManifestPinsCompleteRuntimeBundle() throws {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Dictate Anywhere/ModelManifests/parakeet-v2.json")
        let manifest = try RemyModelManifest.load(from: url)
        XCTAssertEqual(manifest.revision, "ee09c569f73759e6d44c9bd16766f477b2b36d39")
        XCTAssertEqual(manifest.fluidAudioVersion, "0.17.5")
        XCTAssertEqual(manifest.files.count, 22)
        XCTAssertEqual(manifest.totalBytes, 464_414_912)
        XCTAssertEqual(manifest.license, "CC-BY-4.0")
        XCTAssertEqual(Set(manifest.files.map { $0.path.components(separatedBy: "/")[0] }),
                       ["Encoder.mlmodelc", "Decoder.mlmodelc", "JointDecision.mlmodelc", "Preprocessor.mlmodelc", "parakeet_vocab.json", "README.md"])
        let roundTrip = try JSONDecoder().decode(RemyModelManifest.self, from: manifest.encoded())
        XCTAssertEqual(try manifest.digest, try roundTrip.digest)
    }

    func testTraversalMutableURLsAndInventoryConflictsAreRejected() {
        let data = Data("test".utf8)
        for path in ["../escape", "/absolute", "weights//x", "weights/./x", "weights\\x"] {
            XCTAssertThrowsError(try ModelStoreFixture.manifest(data: data, path: path).validate())
        }
        let manifest = ModelStoreFixture.manifest(data: data)
        let artifact = manifest.files[0]
        let mutable = RemyModelManifest.Artifact(path: artifact.path, size: artifact.size, sha256: artifact.sha256,
            url: URL(string: "https://huggingface.co/Fixture/model/resolve/main/\(artifact.path)")!)
        XCTAssertThrowsError(try ModelStoreFixture.replacing(manifest, files: [mutable]).validate())
        XCTAssertThrowsError(try ModelStoreFixture.replacing(manifest, files: [artifact, artifact]).validate())
        let parent = ModelStoreFixture.manifest(data: data, path: "Encoder.mlmodelc").files[0]
        XCTAssertThrowsError(try ModelStoreFixture.replacing(manifest, files: [parent, artifact]).validate())
    }
}

/// Small reviewed-shape fixtures, never real model weights or Settings.
nonisolated enum ModelStoreFixture {
    static func manifest(data: Data = Data("weights".utf8), revision: String = String(repeating: "a", count: 40),
                         path: String = "Encoder.mlmodelc/weights/weight.bin") -> RemyModelManifest {
        let digest = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        let url = URL(string: "https://huggingface.co/Fixture/model/resolve/\(revision)/\(path)")!
        return RemyModelManifest(schemaVersion: 1, repo: "Fixture/model", revision: revision,
            license: "test-only", fluidAudioVersion: "0.17.5", fluidAudioRevision: String(repeating: "b", count: 40),
            totalBytes: Int64(data.count), files: [.init(path: path, size: Int64(data.count), sha256: digest, url: url)])
    }

    static func replacing(_ manifest: RemyModelManifest, files: [RemyModelManifest.Artifact]) -> RemyModelManifest {
        RemyModelManifest(schemaVersion: manifest.schemaVersion, repo: manifest.repo, revision: manifest.revision,
            license: manifest.license, fluidAudioVersion: manifest.fluidAudioVersion,
            fluidAudioRevision: manifest.fluidAudioRevision, totalBytes: files.reduce(0) { $0 + $1.size }, files: files)
    }
}
