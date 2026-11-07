import Foundation
import XCTest
@testable import Dictate_Anywhere

final class RemyParakeetAssetsTests: XCTestCase {
    private let v2 = ["Preprocessor.mlmodelc", "Encoder.mlmodelc", "Decoder.mlmodelc", "JointDecision.mlmodelc"]
    private let v3 = ["Preprocessor.mlmodelc", "Encoder.mlmodelc", "Decoder.mlmodelc", "JointDecisionv3.mlmodelc"]
    private let fused = ["Preprocessor.mlmodelc", "Decoder.mlmodelc", "JointDecision.mlmodelc"]

    private func withFixture(_ body: (URL, RemyStoragePaths) throws -> Void) throws {
        let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("RemyParakeetAssetsTests-\(UUID())")
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temporary) }
        let paths = RemyStoragePaths(applicationSupportDirectory: temporary, bundleIdentifier: "io.github.brysonjl.remy.dev")
        try body(temporary, paths)
    }

    private func installShape(at directory: URL, components: [String]) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        for name in components {
            try FileManager.default.createDirectory(at: directory.appendingPathComponent(name), withIntermediateDirectories: true)
        }
        try Data("{}".utf8).write(to: directory.appendingPathComponent("parakeet_vocab.json"))
    }

    func testExactCoreMLDirectoryIsRecognizedWithoutSDKShortenedSibling() throws {
        try withFixture { _, paths in
            for model in [ParakeetModelChoice.englishOnly, .multilingual, .multilingualUltra] {
                let exact = paths.modelDirectory(named: model.modelDirectoryName)
                let shortened = paths.modelDirectory(named: model.modelDirectoryName.replacingOccurrences(of: "-coreml", with: ""))
                try installShape(at: exact, components: model == .englishOnly ? v2 : v3)
                XCTAssertFalse(FileManager.default.fileExists(atPath: shortened.path))
                XCTAssertTrue(RemyParakeetAssets.isPresent(at: exact, model: model, paths: paths), model.rawValue)
                try FileManager.default.moveItem(at: exact, to: shortened)
                XCTAssertFalse(RemyParakeetAssets.isPresent(at: exact, model: model, paths: paths), "A sibling must not satisfy the exact loader path")
            }
        }
    }

    func testEveryCompiledComponentAndRegularVocabularyAreRequired() throws {
        try withFixture { _, paths in
            let exact = paths.modelDirectory(named: ParakeetModelChoice.englishOnly.modelDirectoryName)
            try installShape(at: exact, components: v2)
            for name in v2 + ["parakeet_vocab.json"] {
                let component = exact.appendingPathComponent(name)
                let held = exact.appendingPathComponent("held-" + name)
                try FileManager.default.moveItem(at: component, to: held)
                XCTAssertFalse(RemyParakeetAssets.isPresent(at: exact, model: .englishOnly, paths: paths), name)
                if name.hasSuffix("mlmodelc") {
                    try Data().write(to: component)
                } else {
                    try FileManager.default.createDirectory(at: component, withIntermediateDirectories: false)
                }
                XCTAssertFalse(RemyParakeetAssets.isPresent(at: exact, model: .englishOnly, paths: paths), "Wrong filesystem type: \(name)")
                try FileManager.default.removeItem(at: component)
                try FileManager.default.moveItem(at: held, to: component)
            }
            XCTAssertTrue(RemyParakeetAssets.isPresent(at: exact, model: .englishOnly, paths: paths))
        }
    }

    func testV3AndUltraRequireActualV3JointWhileCompactUsesFusedGraphs() throws {
        try withFixture { _, paths in
            for model in [ParakeetModelChoice.multilingual, .multilingualUltra] {
                let exact = paths.modelDirectory(named: model.modelDirectoryName)
                try installShape(at: exact, components: v2)
                XCTAssertFalse(RemyParakeetAssets.isPresent(at: exact, model: model, paths: paths))
                try FileManager.default.moveItem(at: exact.appendingPathComponent("JointDecision.mlmodelc"), to: exact.appendingPathComponent("JointDecisionv3.mlmodelc"))
                XCTAssertTrue(RemyParakeetAssets.isPresent(at: exact, model: model, paths: paths))
            }
            let compact = paths.modelDirectory(named: ParakeetModelChoice.compactEnglish.modelDirectoryName)
            try installShape(at: compact, components: fused)
            XCTAssertFalse(FileManager.default.fileExists(atPath: compact.appendingPathComponent("Encoder.mlmodelc").path))
            XCTAssertTrue(RemyParakeetAssets.isPresent(at: compact, model: .compactEnglish, paths: paths))
            XCTAssertFalse(RemyParakeetAssets.isPresent(at: compact, model: .englishOnly, paths: paths))
            XCTAssertFalse(RemyParakeetAssets.isPresent(at: compact, model: .senseVoice, paths: paths))
        }
    }

    func testUnownedAndSymlinkedDirectoriesAndComponentsAreRejected() throws {
        try withFixture { temporary, paths in
            let outside = temporary.appendingPathComponent("outside")
            try installShape(at: outside, components: v2)
            XCTAssertFalse(RemyParakeetAssets.isPresent(at: outside, model: .englishOnly, paths: paths))
            let exact = paths.modelDirectory(named: ParakeetModelChoice.englishOnly.modelDirectoryName)
            try FileManager.default.createDirectory(at: paths.modelsDirectory, withIntermediateDirectories: true)
            try FileManager.default.createSymbolicLink(at: exact, withDestinationURL: outside)
            XCTAssertFalse(RemyParakeetAssets.isPresent(at: exact, model: .englishOnly, paths: paths))
            try FileManager.default.removeItem(at: exact)
            try installShape(at: exact, components: v2)
            for name in ["Encoder.mlmodelc", "parakeet_vocab.json"] {
                let component = exact.appendingPathComponent(name)
                try FileManager.default.removeItem(at: component)
                try FileManager.default.createSymbolicLink(at: component, withDestinationURL: outside.appendingPathComponent(name))
                XCTAssertFalse(RemyParakeetAssets.isPresent(at: exact, model: .englishOnly, paths: paths), name)
                try FileManager.default.removeItem(at: component)
                try FileManager.default.copyItem(at: outside.appendingPathComponent(name), to: component)
            }
        }
    }
}
