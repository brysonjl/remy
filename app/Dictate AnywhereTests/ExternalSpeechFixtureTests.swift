import Foundation
import XCTest

final class ExternalSpeechFixtureTests: XCTestCase {
    private func fixture() throws -> (URL, [String: String]) {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ExternalSpeechFixtureTests-\(UUID())")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }
        return (directory, [ExternalSpeechFixtures.environmentKey: directory.path])
    }

    func testAbsentOptInSkipsInsteadOfSearchingForBundledRecordings() {
        XCTAssertThrowsError(try ExternalSpeechFixtures.url(named: "en-recovery", environment: [:])) {
            XCTAssertTrue($0 is XCTSkip)
        }
    }

    func testSuppliedFileResolvedAndMissingOptionalFileSkips() throws {
        let (directory, environment) = try fixture()
        let file = directory.appendingPathComponent("zh-short.wav")
        // A path-resolution sentinel, not generated speech or a valid WAV.
        try Data("synthetic path sentinel".utf8).write(to: file)
        XCTAssertEqual(try ExternalSpeechFixtures.url(named: "zh-short", environment: environment), file)
        XCTAssertThrowsError(try ExternalSpeechFixtures.url(named: "zh-long", environment: environment)) {
            XCTAssertTrue($0 is XCTSkip)
        }
    }

    func testInvalidConfiguredDirectoryFailsRatherThanSkipping() {
        XCTAssertThrowsError(try ExternalSpeechFixtures.directory(environment: [ExternalSpeechFixtures.environmentKey: "relative/fixtures"])) {
            XCTAssertFalse($0 is XCTSkip)
        }
        XCTAssertThrowsError(try ExternalSpeechFixtures.directory(environment: [ExternalSpeechFixtures.environmentKey: "/nonexistent-fixtures-\(UUID())"])) {
            XCTAssertFalse($0 is XCTSkip)
        }
    }

    func testTraversalAndNonRegularInputsFailRatherThanSkipping() throws {
        let (directory, environment) = try fixture()
        XCTAssertThrowsError(try ExternalSpeechFixtures.url(named: "../outside", environment: environment)) {
            XCTAssertFalse($0 is XCTSkip)
        }
        let file = directory.appendingPathComponent("synthetic-input")
        try Data("synthetic path sentinel".utf8).write(to: file)
        try FileManager.default.createSymbolicLink(at: directory.appendingPathComponent("zh-short.wav"), withDestinationURL: file)
        XCTAssertThrowsError(try ExternalSpeechFixtures.url(named: "zh-short", environment: environment)) {
            XCTAssertFalse($0 is XCTSkip)
        }
    }
}
