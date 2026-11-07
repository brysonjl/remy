import Foundation
import CryptoKit
import Darwin

/// Captures the original bytes before Settings can cap/rewrite them. This does not migrate data.
nonisolated struct RemyLegacyHistorySnapshot: Sendable {
    let data: Data
    let backupURL: URL

    enum Failure: Error, Equatable, Sendable {
        case malformedPreference
        case unconfinedBackup
        case unsafeFilesystem
        case conflictingBackup
        case filesystem(code: Int32)
    }

    /// The caller explicitly owns `ownedRoot`; it must already exist. Only descendant directories
    /// are created. Traversal uses directory descriptors and O_NOFOLLOW, never symlink targets.
    /// Missing legacy data returns nil; all backup failures throw, never imply history-off.
    static func capture(from defaults: UserDefaults, key: String = "transcriptHistory",
                        backupDirectory: URL, ownedRoot: URL) throws -> Self? {
        guard let object = defaults.object(forKey: key) else { return nil }
        guard let data = object as? Data else { throw Failure.malformedPreference }
        let root = ownedRoot.standardizedFileURL
        let destination = backupDirectory.standardizedFileURL
        guard root.isFileURL, destination.isFileURL,
              destination.path.hasPrefix(root.path + "/") else { throw Failure.unconfinedBackup }
        let suffix = destination.path.dropFirst(root.path.count + 1)
        let components = suffix.split(separator: "/").map(String.init)
        guard !components.isEmpty, !components.contains("..") else { throw Failure.unconfinedBackup }
        var descriptor = open(root.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard descriptor >= 0 else { throw Failure.filesystem(code: errno) }
        defer { close(descriptor) }
        var rootInfo = stat()
        guard fstat(descriptor, &rootInfo) == 0, rootInfo.st_uid == geteuid() else { throw Failure.unsafeFilesystem }
        for component in components {
            if mkdirat(descriptor, component, 0o700) != 0 && errno != EEXIST {
                throw Failure.filesystem(code: errno)
            }
            let next = openat(descriptor, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard next >= 0 else { throw Failure.unsafeFilesystem }
            var directoryInfo = stat()
            guard fstat(next, &directoryInfo) == 0, directoryInfo.st_uid == geteuid() else {
                close(next); throw Failure.unsafeFilesystem
            }
            if fchmod(next, 0o700) != 0 { let code = errno; close(next); throw Failure.filesystem(code: code) }
            close(descriptor); descriptor = next
        }
        let digest = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        let name = "legacy-history-\(digest).json"
        let url = destination.appendingPathComponent(name)
        if try verifyExisting(at: descriptor, name: name, expected: data) {
            guard fsync(descriptor) == 0 else { throw Failure.filesystem(code: errno) }
            return .init(data: data, backupURL: url)
        }
        let temporary = ".legacy-history-\(UUID().uuidString).tmp"
        let file = openat(descriptor, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard file >= 0 else { throw Failure.filesystem(code: errno) }
        defer { close(file); unlinkat(descriptor, temporary, 0) }
        if fchmod(file, 0o600) != 0 { throw Failure.filesystem(code: errno) }
        try data.withUnsafeBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let count = Darwin.write(file, bytes.baseAddress!.advanced(by: offset), bytes.count - offset)
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw Failure.filesystem(code: errno) }
                offset += count
            }
        }
        guard fsync(file) == 0 else { throw Failure.filesystem(code: errno) }
        // Atomic publication without replacement: a concurrent identical capture is safe;
        // a pre-existing conflicting object is never overwritten.
        if linkat(descriptor, temporary, descriptor, name, 0) != 0 && errno != EEXIST {
            throw Failure.filesystem(code: errno)
        }
        guard try verifyExisting(at: descriptor, name: name, expected: data) else { throw Failure.conflictingBackup }
        guard fsync(descriptor) == 0 else { throw Failure.filesystem(code: errno) }
        return .init(data: data, backupURL: url)
    }

    private static func verifyExisting(at directory: Int32, name: String, expected: Data) throws -> Bool {
        let file = openat(directory, name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard file >= 0 else {
            if errno == ENOENT { return false }
            throw Failure.unsafeFilesystem
        }
        defer { close(file) }
        var info = stat()
        guard fstat(file, &info) == 0 else { throw Failure.filesystem(code: errno) }
        guard info.st_uid == geteuid(), (info.st_mode & S_IFMT) == S_IFREG, info.st_nlink == 1 || info.st_nlink == 2,
              (info.st_mode & 0o777) == 0o600 else { throw Failure.unsafeFilesystem }
        guard info.st_size == expected.count else { throw Failure.conflictingBackup }
        var actual = Data(count: expected.count)
        try actual.withUnsafeMutableBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let count = Darwin.read(file, bytes.baseAddress!.advanced(by: offset), bytes.count - offset)
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw Failure.conflictingBackup }
                offset += count
            }
        }
        guard actual == expected else { throw Failure.conflictingBackup }
        return true
    }
}
