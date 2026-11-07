import Foundation

/// Runs before Settings constructs its legacy history projection. Only Remy's
/// own preference domain is supplied; no upstream app data is discovered.
@MainActor enum RemyHistoryBootstrap {
    private(set) static var legacy: RemyLegacyHistorySnapshot?

    static func forgetLegacyAfterExplicitDeletion() { legacy = nil }

    static func captureLegacy(in defaults: UserDefaults, paths: RemyStoragePaths = .current) -> String? {
        do {
            try paths.prepareHistoryDirectory()
            legacy = try RemyLegacyHistorySnapshot.capture(from: defaults,
                backupDirectory: paths.historyDirectory.appendingPathComponent("Legacy Backups", isDirectory: true),
                ownedRoot: paths.applicationDirectory)
            return nil
        } catch {
            // No Settings initializer has run. Leave the source unchanged.
            return "Remy could not safely back up its existing history. Check that its Application Support folder is writable, then reopen Remy. The original history has not been changed."
        }
    }

    static func makeController(paths: RemyStoragePaths = .current) -> RemyHistoryController {
        do {
            try paths.prepareHistoryDirectory()
            return RemyHistoryController(repository: try RemyHistoryRepository(url: paths.historyDatabase))
        } catch {
            return RemyHistoryController(repository: nil, openingFailure: error as? RemyHistoryFailure ?? .storage(code: 14))
        }
    }
}
