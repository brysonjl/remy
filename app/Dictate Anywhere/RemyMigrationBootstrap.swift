import AppKit
import Foundation

/// Release-only, before Settings/history construction. The Dev app and its
/// storage remain the rollback copy; credentials never cross this boundary.
@MainActor enum RemyMigrationBootstrap {
    static let sourceIdentity = "io.github.brysonjl.remy.dev"
    static let destinationIdentity = "io.github.brysonjl.remy"
    private(set) static var importedThisLaunch = false
    private(set) static var hasImportedSetup = false
    static let credentialReviewKey = "remy.migrationCredentialReviewDismissed"
    static var needsCredentialReview: Bool {
        hasImportedSetup && !UserDefaults.standard.bool(forKey: credentialReviewKey)
    }

    enum Failure: Error { case destinationConflict, preferences, interrupted }

    static func otherIdentity(for identity: String?) -> String? {
        switch identity {
        case sourceIdentity: destinationIdentity
        case destinationIdentity: sourceIdentity
        default: nil
        }
    }

    static func permitsExclusiveLaunch() -> Bool {
        guard !AppDelegate.isRunningTests,
              let other = otherIdentity(for: Bundle.main.bundleIdentifier),
              !NSRunningApplication.runningApplications(withBundleIdentifier: other).isEmpty else { return true }
        NSApplication.shared.setActivationPolicy(.regular)
        NSApp.activate()
        let otherName = other == sourceIdentity ? "Remy Dev" : "Remy"
        let alert = NSAlert()
        alert.messageText = "Quit \(otherName) first"
        alert.informativeText = "Run one Remy app at a time so the dictation shortcut and microphone have a single owner. Your data stays separate."
        alert.addButton(withTitle: "Quit")
        alert.runModal()
        return false
    }

    static func runIfNeeded() -> Bool {
        guard Bundle.main.bundleIdentifier == destinationIdentity,
              !AppDelegate.isRunningTests else { return true }
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let source = support.appendingPathComponent(sourceIdentity, isDirectory: true)
        let destination = support.appendingPathComponent(destinationIdentity, isDirectory: true)
        do {
            if try RemyLocalDataMigration.pendingReceipt(at: destination) != nil {
                guard sourceIsStopped() else { return explainQuitDev() }
                try finishPreferences(at: destination, defaults: .standard, domain: destinationIdentity)
                importedThisLaunch = true
                hasImportedSetup = true
                return true
            }
            // pendingReceipt also validates completed receipts before returning
            // nil, so an incompatible or damaged receipt cannot reach this path.
            hasImportedSetup = FileManager.default.fileExists(atPath: destination.appendingPathComponent("Migration/receipt.json").path)
            // An already initialized Release is never merged with Dev.
            guard !FileManager.default.fileExists(atPath: destination.path),
                  isPristine(UserDefaults.standard.persistentDomain(forName: destinationIdentity)),
                  FileManager.default.fileExists(atPath: source.path) else { return true }
            guard sourceIsStopped() else { return explainQuitDev() }

            let app = NSApplication.shared
            app.setActivationPolicy(.regular)
            app.activate()
            let prompt = NSAlert()
            prompt.messageText = "Bring your setup over from Remy Dev?"
            prompt.informativeText = """
            Copy your settings, history, recovery recordings, downloaded models, and custom sounds into Remy.

            Remy Dev stays intact as your fallback. API keys and macOS permissions do not transfer. You’ll enter your key in Remy and approve its microphone and Accessibility access. Launch at login starts off.
            """
            prompt.addButton(withTitle: "Import from Remy Dev")
            prompt.addButton(withTitle: "Not Now")
            guard prompt.runModal() == .alertFirstButtonReturn else { return false }
            guard sourceIsStopped() else { return explainQuitDev() }
            guard isPristine(UserDefaults.standard.persistentDomain(forName: destinationIdentity)) else {
                throw Failure.destinationConflict
            }
            let original = UserDefaults.standard.persistentDomain(forName: sourceIdentity) ?? [:]
            let preferences = try RemyMigrationPreferences.encoded(original)
            try copyWithProgress(source: source, destination: destination, preferences: preferences)
            try finishPreferences(at: destination, defaults: .standard, domain: destinationIdentity)
            importedThisLaunch = true
            hasImportedSetup = true
            return true
        } catch {
            let alert = NSAlert()
            NSApplication.shared.setActivationPolicy(.regular)
            NSApp.activate()
            alert.messageText = "Your Remy Dev setup is still safe"
            alert.informativeText = """
            Remy could not finish the import. Your original data and keys have not been removed or replaced. Quit Remy Dev, check available disk space, then reopen Remy to retry or resume.

            Existing Remy data is never overwritten. If this continues, keep using Remy Dev while the import is reviewed.
            """
            alert.addButton(withTitle: "Quit")
            alert.runModal()
            return false
        }
    }

    /// Called only before any ordinary app code has permission to mutate these
    /// settings. A crash before the receipt commits can replay an identical
    /// snapshot; a different destination domain blocks instead of overwriting.
    static func finishPreferences(at destination: URL, defaults: UserDefaults, domain: String) throws {
        guard let receipt = try RemyLocalDataMigration.pendingReceipt(at: destination),
              let data = try RemyLocalDataMigration.pendingPreferences(at: destination),
              let preferences = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] else {
            throw Failure.preferences
        }
        let sanitized = try RemyMigrationPreferences.sanitized(preferences)
        guard NSDictionary(dictionary: preferences).isEqual(to: sanitized),
              canApply(preferences, over: defaults.persistentDomain(forName: domain)) else {
            throw Failure.destinationConflict
        }
        defaults.setPersistentDomain(preferences, forName: domain)
        guard defaults.synchronize(),
              canApply(preferences, over: defaults.persistentDomain(forName: domain)),
              !isPristine(defaults.persistentDomain(forName: domain)) else { throw Failure.preferences }
        try RemyLocalDataMigration.markPreferencesCommitted(at: destination, migrationID: receipt.migrationID)
    }

    static func isPristine(_ domain: [String: Any]?) -> Bool {
        managedDomain(domain).isEmpty
    }

    static func canApply(_ preferences: [String: Any], over existing: [String: Any]?) -> Bool {
        let managed = managedDomain(existing)
        if managed.isEmpty { return true }
        guard Set(managed.keys) == Set(preferences.keys),
              let checked = try? RemyMigrationPreferences.sanitized(managed) else { return false }
        return NSDictionary(dictionary: checked).isEqual(to: preferences)
    }

    private static func managedDomain(_ domain: [String: Any]?) -> [String: Any] {
        // AppKit may save window bookkeeping before Settings exists. Everything
        // else, including unknown application keys, is a destination conflict.
        (domain ?? [:]).filter { key, _ in
            !key.hasPrefix("NSWindow Frame ") && !["NSNavLastRootDirectory", "NSNavPanelExpandedStateForSaveMode"].contains(key)
        }
    }

    private nonisolated static func sourceIsStopped() -> Bool {
        NSRunningApplication.runningApplications(withBundleIdentifier: "io.github.brysonjl.remy.dev").isEmpty
    }

    private static func explainQuitDev() -> Bool {
        NSApplication.shared.setActivationPolicy(.regular)
        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = "Quit Remy Dev before importing"
        alert.informativeText = "The import needs a stable copy of your data. Quit Remy Dev, then reopen Remy. Your setup stays in place."
        alert.addButton(withTitle: "Quit Remy")
        alert.runModal()
        return false
    }

    private static func copyWithProgress(source: URL, destination: URL, preferences: Data) throws {
        let alert = NSAlert()
        alert.messageText = "Bringing your setup to Remy"
        alert.informativeText = "Copying and verifying your local data. Keep Remy Dev closed. Large downloaded models may take a few minutes."
        alert.addButton(withTitle: "Importing…").isEnabled = false
        let progress = NSProgressIndicator(frame: NSRect(x: 0, y: 0, width: 280, height: 18))
        progress.style = .bar
        progress.isIndeterminate = true
        progress.startAnimation(nil)
        alert.accessoryView = progress
        alert.window.standardWindowButton(.closeButton)?.isEnabled = false
        let box = MigrationResultBox()
        let finished = DispatchGroup()
        finished.enter()
        DispatchQueue.global(qos: .userInitiated).async {
            defer { finished.leave() }
            let result: Result<Void, Error> = Result {
                _ = try RemyLocalDataMigration.migrate(source: source, destination: destination,
                    preferences: preferences, sourceIsStopped: { !box.isCancelled && sourceIsStopped() })
            }
            box.set(result)
            DispatchQueue.main.async {
                if NSApp.modalWindow === alert.window { NSApp.stopModal() }
            }
        }
        alert.runModal()
        guard let result = box.get() else {
            box.cancel()
            finished.wait()
            throw Failure.interrupted
        }
        try result.get()
    }
}

private nonisolated final class MigrationResultBox: @unchecked Sendable {
    private let lock = NSLock()
    private var result: Result<Void, Error>?
    private var cancelled = false
    var isCancelled: Bool { lock.withLock { cancelled } }
    func cancel() { lock.withLock { cancelled = true } }
    func set(_ value: Result<Void, Error>) { lock.withLock { result = value } }
    func get() -> Result<Void, Error>? { lock.withLock { result } }
}
