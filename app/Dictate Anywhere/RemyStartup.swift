import Foundation

nonisolated enum RemyStartup {
    /// A nil result allows application construction. This single-key migration
    /// must precede Settings' legacy migrations, Keychain reads and login work.
    static func configurationIssue(in defaults: UserDefaults) -> String? {
        switch RemyConfigurationVersion.migrateIfNeeded(in: defaults) {
        case .current, .migrated:
            return nil
        case .unsupported(let version):
            return "These settings use configuration version \(version). This build supports version \(RemyConfigurationVersion.currentVersion). Open a compatible Remy build or restore a Remy settings backup. No Remy settings were changed."
        case .malformed:
            return "The Remy configuration version could not be read. Restore a Remy settings backup before reopening the app. No Remy settings were changed."
        }
    }
}
