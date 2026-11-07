import Foundation
import XCTest
@testable import Dictate_Anywhere

final class RemyConfigurationVersionTests: XCTestCase {
    private func withDefaults(_ body: (UserDefaults, String) throws -> Void) throws {
        let name = "io.github.brysonjl.remy.configuration-tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defaults.removePersistentDomain(forName: name)
        defer { defaults.removePersistentDomain(forName: name) }
        try body(defaults, name)
    }

    func testLegacyPreflightIsReadOnlyAndCommitPreservesAllOtherPreferences() throws {
        try withDefaults { defaults, name in
            defaults.set([["id": "synthetic-id", "text": "Synthetic history"]], forKey: "transcriptHistory")
            defaults.set(Data([0, 1, 2, 3]), forKey: "hotkeyBindings")
            defaults.set("https://example.invalid/v1", forKey: "openAICompatibleBaseURL")
            defaults.set("existing-credential-reference", forKey: "credentialReference")
            let before = defaults.persistentDomain(forName: name) ?? [:]
            XCTAssertEqual(RemyConfigurationVersion.preflight(in: defaults), .legacy)
            XCTAssertNil(defaults.object(forKey: RemyConfigurationVersion.key))
            XCTAssertEqual(NSDictionary(dictionary: defaults.persistentDomain(forName: name) ?? [:]), NSDictionary(dictionary: before))
            XCTAssertEqual(RemyConfigurationVersion.migrateIfNeeded(in: defaults), .migrated(from: 0, to: 1))
            XCTAssertEqual(RemyConfigurationVersion.preflight(in: defaults), .current)
            var after = defaults.persistentDomain(forName: name) ?? [:]
            after.removeValue(forKey: RemyConfigurationVersion.key)
            XCTAssertEqual(NSDictionary(dictionary: after), NSDictionary(dictionary: before))
        }
    }

    func testExplicitZeroMigratesOnceAndCurrentCommitIsIdempotent() throws {
        try withDefaults { defaults, name in
            defaults.set(0, forKey: RemyConfigurationVersion.key)
            XCTAssertEqual(RemyConfigurationVersion.preflight(in: defaults), .legacy)
            XCTAssertEqual(RemyConfigurationVersion.migrateIfNeeded(in: defaults), .migrated(from: 0, to: 1))
            let committed = defaults.persistentDomain(forName: name) ?? [:]
            XCTAssertEqual(RemyConfigurationVersion.migrateIfNeeded(in: defaults), .current)
            XCTAssertEqual(NSDictionary(dictionary: defaults.persistentDomain(forName: name) ?? [:]), NSDictionary(dictionary: committed))
        }
    }

    func testFutureVersionAndStalePreflightNeverDowngrade() throws {
        try withDefaults { defaults, name in
            XCTAssertEqual(RemyConfigurationVersion.preflight(in: defaults), .legacy)
            defaults.set(99, forKey: RemyConfigurationVersion.key)
            defaults.set("retain", forKey: "unrelated")
            let before = defaults.persistentDomain(forName: name) ?? [:]
            XCTAssertEqual(RemyConfigurationVersion.preflight(in: defaults), .unsupported(version: 99))
            XCTAssertEqual(RemyConfigurationVersion.migrateIfNeeded(in: defaults), .unsupported(version: 99))
            XCTAssertEqual(NSDictionary(dictionary: defaults.persistentDomain(forName: name) ?? [:]), NSDictionary(dictionary: before))
        }
    }

    func testMalformedMarkersAreRejectedWithoutRewriting() throws {
        let values: [Any] = [true, false, "1", -1, 1.5, Data([1]), [1], ["version": 1], NSNumber(value: UInt64.max)]
        try withDefaults { defaults, name in
            for value in values {
                defaults.set(value, forKey: RemyConfigurationVersion.key)
                let before = defaults.persistentDomain(forName: name) ?? [:]
                XCTAssertEqual(RemyConfigurationVersion.preflight(in: defaults), .malformed)
                XCTAssertEqual(RemyConfigurationVersion.migrateIfNeeded(in: defaults), .malformed)
                XCTAssertEqual(NSDictionary(dictionary: defaults.persistentDomain(forName: name) ?? [:]), NSDictionary(dictionary: before))
            }
        }
    }

    func testIndependentDefaultsDomainsDoNotShareVersionMarkers() throws {
        try withDefaults { first, _ in
            try withDefaults { second, _ in
                XCTAssertEqual(RemyConfigurationVersion.migrateIfNeeded(in: first), .migrated(from: 0, to: 1))
                XCTAssertEqual(RemyConfigurationVersion.preflight(in: second), .legacy)
                XCTAssertNil(second.object(forKey: RemyConfigurationVersion.key))
            }
        }
    }
}
