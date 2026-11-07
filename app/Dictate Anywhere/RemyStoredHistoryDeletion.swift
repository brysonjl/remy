import Foundation
import Darwin

/// Descriptor-relative, nonrecursive removal of recognized Remy history files.
/// Unknown files and all links/special files are preserved. A failed operation
/// reports incomplete deletion; rerunning is safe after the problem is repaired.
nonisolated enum RemyStoredHistoryDeletion {
    enum Failure: Error { case unsafeOrUnavailable }

    static func remove(applicationSupport: URL, bundleIdentifier: String) throws {
        guard !bundleIdentifier.isEmpty, bundleIdentifier != ".", bundleIdentifier != "..",
              !bundleIdentifier.contains("/"), !bundleIdentifier.contains("\\") else {
            throw Failure.unsafeOrUnavailable
        }
        let parent = open(applicationSupport.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard parent >= 0 else { throw Failure.unsafeOrUnavailable }
        defer { close(parent) }
        guard let app = try directory(bundleIdentifier, parent: parent) else { return }
        defer { close(app) }
        var failed = false
        do {
            if let history = try directory("History", parent: app) {
                defer { close(history) }
                do { try removeFiles(in: history, matching: schemaBackup) } catch { failed = true }
                do {
                    if let legacy = try directory("Legacy Backups", parent: history) {
                        defer { close(legacy) }
                        try removeFiles(in: legacy, matching: legacyBackup)
                    }
                } catch { failed = true }
            }
        } catch { failed = true }
        do {
            if let recovery = try directory("Recovery", parent: app) {
                defer { close(recovery) }
                try removeFiles(in: recovery, matching: recoveryFile)
            }
        } catch { failed = true }
        guard !failed else { throw Failure.unsafeOrUnavailable }
    }

    private static func directory(_ name: String, parent: Int32) throws -> Int32? {
        let descriptor = openat(parent, name, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        if descriptor < 0, errno == ENOENT { return nil }
        guard descriptor >= 0 else { throw Failure.unsafeOrUnavailable }
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_uid == geteuid() else {
            close(descriptor); throw Failure.unsafeOrUnavailable
        }
        return descriptor
    }

    private static func removeFiles(in descriptor: Int32, matching: (String) -> Bool) throws {
        let copy = dup(descriptor)
        guard copy >= 0 else { throw Failure.unsafeOrUnavailable }
        guard let stream = fdopendir(copy) else {
            close(copy); throw Failure.unsafeOrUnavailable
        }
        defer { closedir(stream) }
        var failed = false
        while true {
            errno = 0
            guard let entry = readdir(stream) else {
                if errno != 0 { failed = true }
                break
            }
            let name = withUnsafePointer(to: &entry.pointee.d_name) {
                $0.withMemoryRebound(to: CChar.self, capacity: Int(MAXNAMLEN) + 1) { String(cString: $0) }
            }
            guard matching(name) else { continue }
            var info = stat()
            guard fstatat(descriptor, name, &info, AT_SYMLINK_NOFOLLOW) == 0 else {
                if errno != ENOENT { failed = true }
                continue
            }
            guard (info.st_mode & S_IFMT) == S_IFREG, info.st_uid == geteuid(), info.st_nlink == 1 else {
                failed = true; continue
            }
            // unlinkat never follows a substituted leaf link or recurses into a
            // directory. Directory descriptors keep ancestor substitutions confined.
            if unlinkat(descriptor, name, 0) != 0, errno != ENOENT { failed = true }
        }
        guard !failed else { throw Failure.unsafeOrUnavailable }
    }

    private static func schemaBackup(_ name: String) -> Bool {
        guard name.hasPrefix("history-before-v2-"), name.hasSuffix(".sqlite") else { return false }
        return UUID(uuidString: String(name.dropFirst("history-before-v2-".count).dropLast(7))) != nil
    }

    private static func legacyBackup(_ name: String) -> Bool {
        if name.hasPrefix("legacy-history-"), name.hasSuffix(".json") {
            let digest = name.dropFirst("legacy-history-".count).dropLast(5)
            return digest.count == 64 && digest.allSatisfy { "0123456789abcdef".contains($0) }
        }
        guard name.hasPrefix(".legacy-history-"), name.hasSuffix(".tmp") else { return false }
        return UUID(uuidString: String(name.dropFirst(".legacy-history-".count).dropLast(4))) != nil
    }

    private static func recoveryFile(_ name: String) -> Bool {
        let url = URL(fileURLWithPath: name)
        return ["caf", "json"].contains(url.pathExtension)
            && UUID(uuidString: url.deletingPathExtension().lastPathComponent) != nil
    }
}
