import CoreFoundation
import Foundation

/// Version 1 introduces only a Remy settings-domain marker. It does not
/// migrate history to SQLite, create provider profiles, or snapshot preferences.
/// Callers supply Remy's defaults domain (or an isolated suite in tests),
/// never another application's domain. No secrets or preferences are exported.
nonisolated enum RemyConfigurationVersion {
    static let key = "remy.configurationSchemaVersion"
    static let currentVersion = 1

    enum Preflight: Equatable, Sendable {
        case legacy
        case current
        case unsupported(version: Int)
        case malformed
    }

    enum MigrationResult: Equatable, Sendable {
        case current
        case migrated(from: Int, to: Int)
        case unsupported(version: Int)
        case malformed
    }

    /// Read-only. Run before initializing settings that write during startup.
    static func preflight(in defaults: UserDefaults) -> Preflight {
        guard let stored = defaults.object(forKey: key) else { return .legacy }
        guard let number = stored as? NSNumber,
              CFGetTypeID(number) != CFBooleanGetTypeID(),
              ["c", "s", "i", "l", "q", "C", "S", "I", "L", "Q"].contains(String(cString: number.objCType)),
              let version = Int(number.stringValue), version >= 0 else { return .malformed }
        switch version {
        case 0: return .legacy
        case currentVersion: return .current
        default: return .unsupported(version: version)
        }
    }

    /// Explicit commit rechecks the stored marker, so a stale preflight is not
    /// permission to downgrade a future version. Version 1 changes one key only;
    /// there is no multi-key transaction or durable history migration to claim.
    /// UserDefaults persistence remains asynchronous; no crash-durability claim
    /// is made beyond submitting this single value to the defaults system.
    static func migrateIfNeeded(in defaults: UserDefaults) -> MigrationResult {
        switch preflight(in: defaults) {
        case .legacy:
            defaults.set(currentVersion, forKey: key)
            return .migrated(from: 0, to: currentVersion)
        case .current:
            return .current
        case .unsupported(let version):
            return .unsupported(version: version)
        case .malformed:
            return .malformed
        }
    }
}
