import Foundation
import XCTest
@testable import Dictate_Anywhere

@MainActor final class RemyMigrationBootstrapTests: XCTestCase {
    private var root: URL!
    private var defaults: UserDefaults!
    private var domain: String!
    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("RemyMigrationBootstrapTests-\(UUID())")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        domain = "RemyMigrationBootstrapTests.\(UUID())"
        defaults = UserDefaults(suiteName: domain)!
    }
    override func tearDownWithError() throws {
        defaults.removePersistentDomain(forName: domain)
        try FileManager.default.removeItem(at: root)
    }
    private func prepared() throws -> (URL, [String: Any]) {
        let source = root.appendingPathComponent("dev")
        let target = root.appendingPathComponent("release")
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: false)
        let values = try RemyMigrationPreferences.sanitized([
            "customVocabulary": ["SyntheticTerm"],
            "remyDictationDestination": "originalField",
            "transcriptHistory": Data("[{\"synthetic\":true}]".utf8)
        ])
        let data = try RemyMigrationPreferences.encoded(values)
        _ = try RemyLocalDataMigration.migrate(source: source, destination: target,
            preferences: data, sourceIsStopped: { true })
        return (target, values)
    }
    func testApplyPendingMigrationThenMarkCompleted() throws {
        let (target, values) = try prepared()
        try RemyMigrationBootstrap.finishPreferences(at: target, defaults: defaults, domain: domain)
        XCTAssertTrue(NSDictionary(dictionary: defaults.persistentDomain(forName: domain)!).isEqual(to: values))
        XCTAssertNil(try RemyLocalDataMigration.pendingReceipt(at: target))
    }
    func testResumeAfterPreferencesWrittenBeforeReceiptCommit() throws {
        let (target, values) = try prepared()
        defaults.setPersistentDomain(values, forName: domain)
        XCTAssertNotNil(try RemyLocalDataMigration.pendingReceipt(at: target))
        try RemyMigrationBootstrap.finishPreferences(at: target, defaults: defaults, domain: domain)
        XCTAssertNil(try RemyLocalDataMigration.pendingReceipt(at: target))
        XCTAssertTrue(NSDictionary(dictionary: defaults.persistentDomain(forName: domain)!).isEqual(to: values))
    }
    func testConflictingDestinationIsNotOverwrittenAndRemainsPending() throws {
        let (target, _) = try prepared()
        let original: [String: Any] = ["customVocabulary": ["AlreadyHere"], "unrecognizedAppData": "preserved"]
        defaults.setPersistentDomain(original, forName: domain)
        XCTAssertThrowsError(try RemyMigrationBootstrap.finishPreferences(at: target, defaults: defaults, domain: domain))
        XCTAssertTrue(NSDictionary(dictionary: defaults.persistentDomain(forName: domain)!).isEqual(to: original))
        XCTAssertNotNil(try RemyLocalDataMigration.pendingReceipt(at: target))
    }
    func testOnlyWindowBookkeepingMayExistBeforeImport() {
        XCTAssertTrue(RemyMigrationBootstrap.isPristine(nil))
        XCTAssertTrue(RemyMigrationBootstrap.isPristine(["NSWindow Frame Setup": "frame"]))
        XCTAssertFalse(RemyMigrationBootstrap.isPristine(["unknownAppSetting": true]))
        XCTAssertFalse(RemyMigrationBootstrap.isPristine(["remy.configurationSchemaVersion": 1]))
        XCTAssertFalse(RemyMigrationBootstrap.canApply(["themeMode": "system"], over: ["themeMode": "dark"]))
        XCTAssertFalse(RemyMigrationBootstrap.canApply(
            ["remy.configurationSchemaVersion": 1], over: ["remy.configurationSchemaVersion": true]))
        XCTAssertFalse(RemyMigrationBootstrap.canApply(
            ["remy.configurationSchemaVersion": 1, "soundEffectsEnabled": true],
            over: ["remy.configurationSchemaVersion": 1, "soundEffectsEnabled": 1]))
    }
    func testOnlyDevAndReleaseAreMutuallyExclusive() {
        XCTAssertEqual(RemyMigrationBootstrap.otherIdentity(for: "io.github.brysonjl.remy.dev"), "io.github.brysonjl.remy")
        XCTAssertEqual(RemyMigrationBootstrap.otherIdentity(for: "io.github.brysonjl.remy"), "io.github.brysonjl.remy.dev")
        XCTAssertNil(RemyMigrationBootstrap.otherIdentity(for: "io.github.brysonjl.remy.testhost"))
        XCTAssertNil(RemyMigrationBootstrap.otherIdentity(for: nil))
    }
}
