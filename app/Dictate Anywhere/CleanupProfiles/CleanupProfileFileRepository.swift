import Foundation
import Darwin

/// Explicitly constructed storage adapter; no standard-path/defaults/Keychain
/// lookup and no automatic migration. All IO is relative to nofollow directory
/// descriptors. Cooperating instances serialize load/CAS/publication with flock.
@MainActor
final class CleanupProfileFileRepository: CleanupProfileRepository {
    enum RepositoryError: Error, Equatable {
        case unsafePath, unsafePermissions, unsafeFile, malformed, unsupportedSchema
        case invalidCollection, conflict, exhaustedRevision, oversized, lockUnavailable
        case io, publicationUncertain
    }
    /// Test injection may write the exclusive staging descriptor, never close it.
    /// Publication/validation remains repository-owned even when a writer throws.
    typealias AtomicWriter = (Data, Int32) throws -> Void
    private struct Header: Decodable { let schemaVersion: Int }
    private struct Envelope: Codable {
        let schemaVersion: Int
        let collection: CleanupProfileCollection
    }
    private struct Stored {
        let bytes: Data
        let collection: CleanupProfileCollection
        let inode: ino_t
        let device: dev_t
    }
    static let maximumBytes = 1_048_576
    private static let fileName = "profiles.json"
    private static let lockName = ".profiles.lock"
    private let rootPath: String
    private let writer: AtomicWriter

    init(rootURL: URL, atomicWriter: AtomicWriter? = nil) throws {
        guard rootURL.isFileURL, rootURL.path.hasPrefix("/"),
              !rootURL.pathComponents.contains(where: { $0 == "." || $0 == ".." }),
              rootURL.pathComponents.count > 2 else { throw RepositoryError.unsafePath }
        // macOS' system /var alias is the sole permitted path alias, above the
        // trusted fixture/storage root. Never resolve arbitrary source symlinks.
        let path = rootURL.path
        rootPath = path.hasPrefix("/var/") ? "/private/var/" + String(path.dropFirst(5)) : path
        writer = atomicWriter ?? Self.writeAll
    }

    func load() throws -> CleanupProfileCollection {
        try withLockedDirectory { descriptor, _ in
            try readStored(descriptor)?.collection ?? .empty
        }
    }

    func commit(_ collection: CleanupProfileCollection, expectedRevision: UInt64) throws {
        try Self.validate(collection)
        guard expectedRevision < UInt64.max else { throw RepositoryError.exhaustedRevision }
        guard collection.revision == expectedRevision + 1 else { throw RepositoryError.invalidCollection }
        let bytes: Data
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.sortedKeys]
            bytes = try encoder.encode(Envelope(schemaVersion: 1, collection: collection))
        } catch { throw RepositoryError.invalidCollection }
        guard bytes.count <= Self.maximumBytes else { throw RepositoryError.oversized }
        try withLockedDirectory { directory, lockMetadata in
            let previous = try readStored(directory)
            guard (previous?.collection.revision ?? 0) == expectedRevision else { throw RepositoryError.conflict }
            if let previous {
                guard collection.completedLegacyMigrations.isSuperset(of: previous.collection.completedLegacyMigrations) else {
                    throw RepositoryError.invalidCollection
                }
                let oldProfiles = Dictionary(uniqueKeysWithValues: previous.collection.profiles.map { ($0.id, $0) })
                for profile in collection.profiles {
                    if let old = oldProfiles[profile.id] {
                        if profile != old {
                            guard old.revision < UInt64.max, profile.revision == old.revision + 1 else { throw RepositoryError.invalidCollection }
                        }
                    } else if profile.revision != 1 { throw RepositoryError.invalidCollection }
                }
            } else if collection.profiles.contains(where: { $0.revision != 1 }) { throw RepositoryError.invalidCollection }
            let stage = ".profiles-\(UUID().uuidString).tmp"
            let descriptor = openat(directory, stage, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, mode_t(0o600))
            guard descriptor >= 0 else { throw RepositoryError.io }
            var removeStage = true
            defer {
                close(descriptor)
                if removeStage { _ = unlinkat(directory, stage, 0) }
            }
            do { try writer(bytes, descriptor) }
            catch { throw RepositoryError.io }
            _ = try checkedFile(descriptor)
            guard try readBytes(descriptor) == bytes, fsync(descriptor) == 0 else { throw RepositoryError.io }
            // An injected writer or uncooperative process cannot silently replace
            // a same-revision current file while staging is in progress.
            try verifyRootIdentity(directory)
            try verifyNamedLock(directory, metadata: lockMetadata)
            let current = try readStored(directory)
            guard current?.bytes == previous?.bytes,
                  current?.inode == previous?.inode, current?.device == previous?.device else { throw RepositoryError.conflict }
            try verifyNamedStage(directory, name: stage, descriptor: descriptor)
            if previous != nil {
                guard renameatx_np(directory, stage, directory, Self.fileName, UInt32(RENAME_SWAP)) == 0 else { throw RepositoryError.io }
                // The previous single-link file now occupies the staging name.
                if fsync(directory) != 0 {
                    if renameatx_np(directory, stage, directory, Self.fileName, UInt32(RENAME_SWAP)) != 0 {
                        removeStage = false // Retain old inode for explicit recovery.
                        throw RepositoryError.publicationUncertain
                    }
                    guard fsync(directory) == 0 else { throw RepositoryError.publicationUncertain }
                    throw RepositoryError.io
                }
            } else {
                guard renameatx_np(directory, stage, directory, Self.fileName, UInt32(RENAME_EXCL)) == 0 else { throw RepositoryError.io }
                if fsync(directory) != 0 {
                    guard unlinkat(directory, Self.fileName, 0) == 0, fsync(directory) == 0 else {
                        throw RepositoryError.publicationUncertain
                    }
                    throw RepositoryError.io
                }
            }
            // Publication's directory fsync is the commit point. Cleanup of the
            // old staging inode may be retried later; never retry the commit.
        }
    }

    private static func validate(_ collection: CleanupProfileCollection) throws {
        guard collection.profiles.count <= 64, collection.completedLegacyMigrations.count <= 128,
              Set(collection.profiles.map(\.id)).count == collection.profiles.count,
              collection.selectedID.map({ id in collection.profiles.contains { $0.id == id } }) ?? true,
              collection.completedLegacyMigrations.allSatisfy({ !$0.isEmpty && $0.utf8.count <= 128 }),
              collection.revision > 0 || collection == .empty else { throw RepositoryError.invalidCollection }
        for profile in collection.profiles {
            guard profile.name.utf8.count <= 256, profile.baseURL.utf8.count <= 2_048,
                  profile.model.utf8.count <= 512, profile.prompt.utf8.count <= 65_536 else { throw RepositoryError.invalidCollection }
            do { _ = try profile.validatedEndpoint() }
            catch { throw RepositoryError.invalidCollection }
        }
    }

    private func readStored(_ directory: Int32) throws -> Stored? {
        let descriptor = openat(directory, Self.fileName, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
        guard descriptor >= 0 else {
            if errno == ENOENT { return nil }
            throw RepositoryError.unsafeFile
        }
        defer { close(descriptor) }
        let metadata = try checkedFile(descriptor)
        let bytes = try readBytes(descriptor)
        let header: Header
        do { header = try JSONDecoder().decode(Header.self, from: bytes) }
        catch { throw RepositoryError.malformed }
        guard header.schemaVersion == 1 else { throw RepositoryError.unsupportedSchema }
        let envelope: Envelope
        do { envelope = try JSONDecoder().decode(Envelope.self, from: bytes) }
        catch { throw RepositoryError.malformed }
        try Self.validate(envelope.collection)
        return Stored(bytes: bytes, collection: envelope.collection, inode: metadata.st_ino, device: metadata.st_dev)
    }

    private func checkedFile(_ descriptor: Int32) throws -> stat {
        var metadata = stat()
        guard fstat(descriptor, &metadata) == 0,
              metadata.st_mode & S_IFMT == S_IFREG, metadata.st_uid == geteuid(), metadata.st_nlink == 1 else {
            throw RepositoryError.unsafeFile
        }
        guard metadata.st_mode & 0o777 == 0o600 else { throw RepositoryError.unsafePermissions }
        guard metadata.st_size >= 0, metadata.st_size <= Self.maximumBytes else { throw RepositoryError.oversized }
        return metadata
    }

    private func readBytes(_ descriptor: Int32) throws -> Data {
        var result = Data()
        var buffer = [UInt8](repeating: 0, count: 16_384)
        while true {
            let count = buffer.withUnsafeMutableBytes { pread(descriptor, $0.baseAddress, $0.count, off_t(result.count)) }
            if count < 0 {
                if errno == EINTR { continue }
                throw RepositoryError.io
            }
            if count == 0 { return result }
            guard count <= Self.maximumBytes - result.count else { throw RepositoryError.oversized }
            result.append(contentsOf: buffer.prefix(count))
        }
    }

    private func withLockedDirectory<T>(_ operation: (Int32, stat) throws -> T) throws -> T {
        let directory = try openDirectory()
        defer { close(directory) }
        let lockFile = openat(directory, Self.lockName, O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK, mode_t(0o600))
        guard lockFile >= 0 else { throw RepositoryError.unsafeFile }
        defer { close(lockFile) }
        let lockMetadata = try checkedFile(lockFile)
        let deadline = ContinuousClock.now.advanced(by: .seconds(1))
        while flock(lockFile, LOCK_EX | LOCK_NB) != 0 {
            guard errno == EWOULDBLOCK || errno == EAGAIN || errno == EINTR else { throw RepositoryError.io }
            guard ContinuousClock.now < deadline else { throw RepositoryError.lockUnavailable }
            Thread.sleep(forTimeInterval: 0.005)
        }
        defer { _ = flock(lockFile, LOCK_UN) }
        try verifyNamedLock(directory, metadata: lockMetadata)
        return try operation(directory, lockMetadata)
    }

    private func verifyNamedStage(_ directory: Int32, name: String, descriptor: Int32) throws {
        let opened = try checkedFile(descriptor)
        var named = stat()
        guard fstatat(directory, name, &named, AT_SYMLINK_NOFOLLOW) == 0,
              named.st_dev == opened.st_dev, named.st_ino == opened.st_ino,
              named.st_mode & S_IFMT == S_IFREG, named.st_uid == opened.st_uid,
              named.st_mode & 0o777 == 0o600, named.st_nlink == 1 else {
            throw RepositoryError.unsafeFile
        }
    }

    private func verifyNamedLock(_ directory: Int32, metadata: stat) throws {
        var namedLock = stat()
        guard fstatat(directory, Self.lockName, &namedLock, AT_SYMLINK_NOFOLLOW) == 0,
              namedLock.st_dev == metadata.st_dev, namedLock.st_ino == metadata.st_ino,
              namedLock.st_mode & S_IFMT == S_IFREG, namedLock.st_uid == geteuid(),
              namedLock.st_mode & 0o777 == 0o600, namedLock.st_nlink == 1 else { throw RepositoryError.unsafeFile }
    }

    private func verifyRootIdentity(_ directory: Int32) throws {
        let named = try openDirectory(createIfMissing: false)
        defer { close(named) }
        var openedMetadata = stat(), namedMetadata = stat()
        guard fstat(directory, &openedMetadata) == 0, fstat(named, &namedMetadata) == 0,
              openedMetadata.st_dev == namedMetadata.st_dev, openedMetadata.st_ino == namedMetadata.st_ino else {
            throw RepositoryError.unsafePath
        }
    }

    private func openDirectory(createIfMissing: Bool = true) throws -> Int32 {
        let components = rootPath.split(separator: "/").map(String.init)
        var descriptor = open("/", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard descriptor >= 0 else { throw RepositoryError.io }
        do {
            for (index, component) in components.enumerated() {
                var next = openat(descriptor, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
                if next < 0, errno == ENOENT, createIfMissing, index == components.count - 1 {
                    let created = mkdirat(descriptor, component, mode_t(0o700))
                    guard created == 0 || errno == EEXIST else { throw RepositoryError.io }
                    if created == 0, fsync(descriptor) != 0 { throw RepositoryError.io }
                    next = openat(descriptor, component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
                }
                guard next >= 0 else { throw RepositoryError.unsafePath }
                close(descriptor); descriptor = next
            }
            var metadata = stat()
            guard fstat(descriptor, &metadata) == 0, metadata.st_mode & S_IFMT == S_IFDIR,
                  metadata.st_uid == geteuid(), metadata.st_mode & 0o777 == 0o700 else { throw RepositoryError.unsafePermissions }
            return descriptor
        } catch { close(descriptor); throw error }
    }

    private static func writeAll(_ data: Data, _ descriptor: Int32) throws {
        try data.withUnsafeBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let count = Darwin.write(descriptor, bytes.baseAddress!.advanced(by: offset), bytes.count - offset)
                if count < 0, errno == EINTR { continue }
                guard count > 0 else { throw RepositoryError.io }
                offset += count
            }
        }
    }
}
