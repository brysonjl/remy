import Foundation
import XCTest
@testable import Dictate_Anywhere

final class RemyTestIsolationTests: XCTestCase {
    func testHostCannotUseDevelopmentPersistenceIdentity() {
        XCTAssertEqual(Bundle.main.bundleIdentifier, "io.github.brysonjl.remy.testhost")
        XCTAssertEqual(RemyStoragePaths.current.bundleIdentifier, "io.github.brysonjl.remy.testhost")
        XCTAssertNotEqual(RemyStoragePaths.current.applicationDirectory.lastPathComponent, "io.github.brysonjl.remy.dev")
    }
}
