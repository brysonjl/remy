import Foundation
import Darwin

/// The bundle identity owns every persistent Remy asset. In particular, the
/// FluidAudio defaults are shared by unrelated applications and must not be used.
nonisolated struct RemyStoragePaths: Sendable {
    static let fallbackBundleIdentifier = "io.github.brysonjl.remy"

    static var current: Self {
        Self(applicationSupportDirectory: FileManager.default.urls(
            for: .applicationSupportDirectory, in: .userDomainMask)[0],
             bundleIdentifier: Bundle.main.bundleIdentifier)
    }

    let bundleIdentifier: String
    private let applicationSupportDirectory: URL
    let applicationDirectory: URL
    var modelsDirectory: URL { applicationDirectory.appendingPathComponent("Models", isDirectory: true) }
    var recoveryDirectory: URL { applicationDirectory.appendingPathComponent("Recovery", isDirectory: true) }
    var historyDirectory: URL { applicationDirectory.appendingPathComponent("History", isDirectory: true) }
    var historyDatabase: URL { historyDirectory.appendingPathComponent("history.sqlite") }
    var cleanupProfilesDirectory: URL { applicationDirectory.appendingPathComponent("CleanupProfiles", isDirectory: true) }

    /// Explicit user-requested deletion only. Leaves the open SQLite database,
    /// models, preferences and credentials in place. The controller clears live rows.
    func removeStoredHistoryArtifacts() throws {
        try RemyStoredHistoryDeletion.remove(applicationSupport: applicationSupportDirectory,
                                               bundleIdentifier: bundleIdentifier)
    }

    /// Create only private, owned descendants through directory descriptors.
    /// Never follow an app/history directory symlink while preparing persistence.
    func prepareHistoryDirectory() throws {
        let parent = open(applicationSupportDirectory.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard parent >= 0 else { throw StorageError.unsafeHistoryStore }
        defer { close(parent) }
        func child(_ name: String, of descriptor: Int32) throws -> Int32 {
            guard mkdirat(descriptor, name, 0o700) == 0 || errno == EEXIST else { throw StorageError.unsafeHistoryStore }
            let next = openat(descriptor, name, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard next >= 0 else { throw StorageError.unsafeHistoryStore }
            var info = stat()
            guard fstat(next, &info) == 0, info.st_uid == geteuid(), fchmod(next, 0o700) == 0 else {
                close(next); throw StorageError.unsafeHistoryStore
            }
            return next
        }
        let app = try child(bundleIdentifier, of: parent)
        defer { close(app) }
        let history = try child("History", of: app)
        defer { close(history) }
        // SQLite may touch these names during recovery. Refuse links and other
        // special objects before opening the database, including dangling links.
        for name in ["history.sqlite", "history.sqlite-journal", "history.sqlite-wal", "history.sqlite-shm"] {
            var info = stat()
            if fstatat(history, name, &info, AT_SYMLINK_NOFOLLOW) == 0 {
                guard (info.st_mode & S_IFMT) == S_IFREG, info.st_uid == geteuid(), info.st_nlink == 1 else {
                    throw StorageError.unsafeHistoryStore
                }
            } else if errno != ENOENT { throw StorageError.unsafeHistoryStore }
        }
    }

    init(applicationSupportDirectory: URL, bundleIdentifier: String?) {
        let identity = bundleIdentifier ?? Self.fallbackBundleIdentifier
        // A malformed identity cannot turn a directory component into a path.
        self.bundleIdentifier = !identity.isEmpty && identity != "." && identity != ".."
            && !identity.contains("/") && !identity.contains("\\")
            ? identity : Self.fallbackBundleIdentifier
        self.applicationSupportDirectory = applicationSupportDirectory.standardizedFileURL.resolvingSymlinksInPath()
        applicationDirectory = self.applicationSupportDirectory
            .appendingPathComponent(self.bundleIdentifier, isDirectory: true)
    }

    func modelDirectory(named name: String) -> URL {
        let components = name.split(separator: "/", omittingEmptySubsequences: false)
        precondition(!components.isEmpty && components.allSatisfy {
            !$0.isEmpty && $0 != "." && $0 != ".." && !$0.contains("\\")
        }, "Model directory must be a relative path within Remy's model store")
        return modelsDirectory.appendingPathComponent(name, isDirectory: true)
    }

    /// Check both path components and existing symlinks before a loader can
    /// download, repair or remove assets. Sibling prefixes are not descendants.
    func requireOwnedModelPath(_ url: URL, allowRoot: Bool = true) throws {
        // Never erase a symlink/.. traversal before validating the original
        // path that the filesystem operation will receive.
        guard !url.pathComponents.contains(where: { $0 == "." || $0 == ".." }) else {
            throw StorageError.outsideModelStore
        }
        let root = modelsDirectory.standardizedFileURL
        let candidate = url.standardizedFileURL
        guard !hasSymbolicLinkAncestor(candidate),
              root.resolvingSymlinksInPath().pathComponents == root.pathComponents,
              Self.contains(candidate, in: root, allowRoot: allowRoot),
              Self.contains(candidate.resolvingSymlinksInPath(), in: root, allowRoot: allowRoot) else {
            throw StorageError.outsideModelStore
        }
    }

    func removeModel(at url: URL, fileManager: FileManager = .default) throws {
        try requireOwnedModelPath(url, allowRoot: false)
        if fileManager.fileExists(atPath: url.path) {
            try fileManager.removeItem(at: url)
            removeEmptyModelParents(from: url.deletingLastPathComponent(), fileManager: fileManager)
        }
    }

    func removeEmptyModelParents(from directory: URL, fileManager: FileManager = .default) {
        var current = directory.standardizedFileURL
        while (try? requireOwnedModelPath(current, allowRoot: false)) != nil {
            guard let contents = try? fileManager.contentsOfDirectory(atPath: current.path),
                  contents.isEmpty else { return }
            do { try fileManager.removeItem(at: current) } catch { return }
            current = current.deletingLastPathComponent()
        }
    }

    private static func contains(_ url: URL, in root: URL, allowRoot: Bool) -> Bool {
        let components = url.pathComponents, rootComponents = root.pathComponents
        return components.count >= rootComponents.count + (allowRoot ? 0 : 1)
            && Array(components.prefix(rootComponents.count)) == rootComponents
    }

    private func hasSymbolicLinkAncestor(_ url: URL) -> Bool {
        // Resolving a missing leaf can leave an existing ancestor unresolved.
        // Check each component directly, including dangling symbolic links,
        // before allowing a loader to create any missing directories. The
        // supplied Application Support root is the trust boundary; aliases
        // above it (such as macOS /var) do not belong to the model store.
        var current = url
        while current.pathComponents.count >= applicationSupportDirectory.pathComponents.count {
            if (try? FileManager.default.destinationOfSymbolicLink(atPath: current.path)) != nil {
                return true
            }
            if current.pathComponents.count == applicationSupportDirectory.pathComponents.count { break }
            current = current.deletingLastPathComponent()
        }
        return false
    }

    enum StorageError: LocalizedError {
        case outsideModelStore
        case unsafeHistoryStore
        var errorDescription: String? {
            switch self {
            case .outsideModelStore:
                "The model path is outside Remy's model store or uses a symbolic link. Choose a Remy-owned model directory."
            case .unsafeHistoryStore:
                "Remy could not open its private history folder. Your existing history has not been changed."
            }
        }
    }
}
