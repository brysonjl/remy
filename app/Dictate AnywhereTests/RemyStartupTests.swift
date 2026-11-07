import Foundation
import XCTest
@testable import Dictate_Anywhere

final class RemyStartupTests: XCTestCase {
    func testConfigurationGateAllowsLegacyAndCurrentWithoutChangingOtherData() throws {
        let name = "io.github.brysonjl.remy.startup-tests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        defaults.set("keep synthetic history", forKey: "transcriptHistory")
        XCTAssertNil(RemyStartup.configurationIssue(in: defaults))
        XCTAssertEqual(defaults.integer(forKey: RemyConfigurationVersion.key), 1)
        XCTAssertNil(RemyStartup.configurationIssue(in: defaults))
        XCTAssertEqual(defaults.string(forKey: "transcriptHistory"), "keep synthetic history")
    }

    func testConfigurationGateBlocksFutureAndMalformedWithoutMutation() throws {
        let name = "io.github.brysonjl.remy.startup-tests.\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let markers: [Any] = [99, "invalid"]
        for marker in markers {
            defaults.set(marker, forKey: RemyConfigurationVersion.key)
            let before = defaults.persistentDomain(forName: name) ?? [:]
            XCTAssertNotNil(RemyStartup.configurationIssue(in: defaults))
            XCTAssertEqual(NSDictionary(dictionary: defaults.persistentDomain(forName: name) ?? [:]), NSDictionary(dictionary: before))
        }
    }
}
