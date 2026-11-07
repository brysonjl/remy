import Foundation
import CryptoKit
import SQLite3
import Darwin

/// Explicit local Dev -> Remy transfer. No preferences/Keychain/model/network lookups.
/// The caller must keep both applications stopped until pending preferences are applied.
nonisolated enum RemyLocalDataMigration {
    enum Failure: Error, Equatable {
        case unsafePath, unsafeFile, sourceRunning, destinationExists, invalidPreferences
        case limitExceeded, insufficientSpace, io, database, unsupportedSchema, verification, publicationUncertain
    }
    struct Receipt: Codable, Equatable, Sendable {
        let schemaVersion: Int
        let migrationID: UUID
        let sourceIdentity: String
        let destinationIdentity: String
        let phase: String
        let preferencesSHA256: String
    }
    private static let allowed: Set<String> = ["History","Recovery","CleanupProfiles","Models","Sounds"]
    private static let maximumFiles = 200_000
    private static let maximumBytes: Int64 = 100 * 1_024 * 1_024 * 1_024

    /// Destination must not exist. Source remains unchanged, including SQLite sidecars.
    /// A published pendingPreferences receipt is deliberately retained for caller recovery.
    static func migrate(source: URL, destination: URL, preferences: Data,
                        sourceIsStopped: () -> Bool,
                        availableCapacity: (() throws -> Int64)? = nil) throws -> Receipt {
        guard sourceIsStopped() else { throw Failure.sourceRunning }
        let source = try physicalURL(source)
        let destination = try physicalURL(destination)
        guard source.isFileURL, destination.isFileURL,
              !source.pathComponents.contains(where: { $0 == "." || $0 == ".." }),
              !destination.pathComponents.contains(where: { $0 == "." || $0 == ".." }),
              source.standardizedFileURL != destination.standardizedFileURL,
              source.deletingLastPathComponent().standardizedFileURL == destination.deletingLastPathComponent().standardizedFileURL else { throw Failure.unsafePath }
        guard preferences.count <= RemyMigrationPreferences.maximumEncodedBytes,
              (try? PropertyListSerialization.propertyList(from:preferences,format:nil)) is [String:Any] else { throw Failure.invalidPreferences }
        let parent = destination.deletingLastPathComponent()
        try checkDirectoryChain(parent)
        try checkItem(source,directory:true)
        var sourceIdentity = stat()
        guard lstat(source.path,&sourceIdentity) == 0 else { throw Failure.unsafePath }
        let parentFD = open(parent.path,O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard parentFD >= 0 else { throw Failure.unsafePath }
        defer { close(parentFD) }
        try requireAbsent(destination)
        let inventory = try inventorySource(source,stopped:sourceIsStopped)
        let extraDatabase = try checkedSum(inventory.historyBytes,inventory.historyBytes)
        let reserve = try checkedSum(try checkedSum(extraDatabase,Int64(preferences.count)),512 * 1_024 * 1_024)
        let capacity: () throws -> Int64 = availableCapacity ?? {
            let values = try parent.resourceValues(forKeys:[.volumeAvailableCapacityForImportantUsageKey])
            guard let available = values.volumeAvailableCapacityForImportantUsage, available >= 0 else { throw Failure.io }
            return available
        }
        guard try capacity() >= checkedSum(inventory.bytes,reserve) else { throw Failure.insufficientSpace }
        let id = UUID(), stageName = ".remy-migration-\(UUID().uuidString).tmp"
        let stage = parent.appendingPathComponent(stageName,isDirectory:true)
        guard mkdirat(parentFD,stageName,0o700) == 0 else { throw Failure.io }
        var stageIdentity = stat()
        guard lstat(stage.path,&stageIdentity) == 0 else { throw Failure.io }
        var published = false
        defer { if !published { try? FileManager.default.removeItem(at:stage) } }
        var count = 0, bytes: Int64 = 0
        for name in allowed.sorted() {
            guard sourceIsStopped() else { throw Failure.sourceRunning }
            let from = source.appendingPathComponent(name)
            if try exists(from) {
                try copyTree(from,to:stage.appendingPathComponent(name),relative:name,
                             count:&count,bytes:&bytes,stopped:sourceIsStopped,capacity:capacity,totalBytes:inventory.bytes,reserve:reserve)
            }
        }
        guard sourceIsStopped() else { throw Failure.sourceRunning }
        guard count == inventory.count, bytes == inventory.bytes else { throw Failure.verification }
        guard try capacity() >= reserve else { throw Failure.insufficientSpace }
        let database = stage.appendingPathComponent("History/history.sqlite")
        if try exists(database) { try normalizeDatabase(database) }
        else {
            for suffix in ["-wal","-shm","-journal"] where try exists(URL(fileURLWithPath:database.path+suffix)) { throw Failure.database }
        }
        let receipt = Receipt(schemaVersion:1,migrationID:id,sourceIdentity:"io.github.brysonjl.remy.dev",
                              destinationIdentity:"io.github.brysonjl.remy",phase:"pendingPreferences",preferencesSHA256:hexDigest(preferences))
        let migration = stage.appendingPathComponent("Migration",isDirectory:true)
        guard mkdir(migration.path,0o700) == 0 else { throw Failure.io }
        try writePrivate(preferences,to:migration.appendingPathComponent("preferences.plist"))
        try writePrivate(JSONEncoder().encode(receipt),to:migration.appendingPathComponent("receipt.json"))
        try syncDirectories(stage)
        guard sourceIsStopped() else { throw Failure.sourceRunning }
        // Recheck source/root identity and destination absence before publication.
        try checkDirectoryChain(parent)
        try checkItem(source,directory:true)
        var finalSource = stat()
        guard lstat(source.path,&finalSource) == 0, finalSource.st_ino == sourceIdentity.st_ino,
              finalSource.st_dev == sourceIdentity.st_dev else { throw Failure.unsafePath }
        var opened = stat(), named = stat()
        guard fstat(parentFD,&opened) == 0, lstat(parent.path,&named) == 0,
              opened.st_ino == named.st_ino, opened.st_dev == named.st_dev else { throw Failure.unsafePath }
        try requireAbsent(destination)
        var finalStage = stat()
        guard lstat(stage.path,&finalStage) == 0, finalStage.st_mode & S_IFMT == S_IFDIR,
              finalStage.st_uid == geteuid(), finalStage.st_ino == stageIdentity.st_ino,
              finalStage.st_dev == stageIdentity.st_dev else { throw Failure.unsafePath }
        guard renameatx_np(parentFD,stageName,parentFD,destination.lastPathComponent,UInt32(RENAME_EXCL)) == 0 else {
            if errno == EEXIST { throw Failure.destinationExists }
            throw Failure.io
        }
        published = true
        // Never erase a published migration on fsync failure: receipt enables explicit recovery.
        guard fsync(parentFD) == 0 else { throw Failure.publicationUncertain }
        return receipt
    }

    static func pendingReceipt(at destination: URL) throws -> Receipt? {
        guard let receipt = try readReceipt(at:destination) else { return nil }
        return receipt.phase == "completed" ? nil : receipt
    }

    static func pendingPreferences(at destination: URL) throws -> Data? {
        let destination = try physicalURL(destination)
        guard let receipt = try pendingReceipt(at:destination) else { return nil }
        let url = destination.appendingPathComponent("Migration/preferences.plist")
        try checkItem(url,directory:false)
        let info = try FileManager.default.attributesOfItem(atPath:url.path)
        guard ((info[.size] as? NSNumber)?.intValue ?? Int.max) <= RemyMigrationPreferences.maximumEncodedBytes,
              (info[.posixPermissions] as? NSNumber)?.intValue == 0o600 else { throw Failure.unsafeFile }
        let data = try Data(contentsOf:url)
        guard data.count <= RemyMigrationPreferences.maximumEncodedBytes,
              (try? PropertyListSerialization.propertyList(from:data,format:nil)) is [String:Any] else { throw Failure.invalidPreferences }
        guard hexDigest(data) == receipt.preferencesSHA256 else { throw Failure.verification }
        return data
    }

    static func markPreferencesCommitted(at destination: URL,migrationID:UUID) throws {
        let destination = try physicalURL(destination)
        guard let previous = try readReceipt(at:destination), previous.migrationID == migrationID else { throw Failure.verification }
        let migration = destination.appendingPathComponent("Migration",isDirectory:true)
        if previous.phase != "completed" {
            _ = try pendingPreferences(at:destination)
            let completed = Receipt(schemaVersion:1,migrationID:migrationID,sourceIdentity:previous.sourceIdentity,
                destinationIdentity:previous.destinationIdentity,phase:"completed",preferencesSHA256:previous.preferencesSHA256)
            let temporary = migration.appendingPathComponent(".receipt-\(UUID().uuidString).tmp")
            defer { try? FileManager.default.removeItem(at:temporary) }
            try writePrivate(JSONEncoder().encode(completed),to:temporary)
            guard rename(temporary.path,migration.appendingPathComponent("receipt.json").path) == 0 else { throw Failure.io }
        }
        let descriptor = open(migration.path,O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard descriptor >= 0 else { throw Failure.unsafePath }
        defer { close(descriptor) }
        guard fsync(descriptor) == 0 else { throw Failure.publicationUncertain }
    }

    private static func readReceipt(at destination: URL) throws -> Receipt? {
        let destination = try physicalURL(destination)
        guard try exists(destination) else { return nil }
        try checkDirectoryChain(destination)
        try checkItem(destination,directory:true)
        guard (try FileManager.default.attributesOfItem(atPath:destination.path)[.posixPermissions] as? NSNumber)?.intValue == 0o700 else { throw Failure.unsafeFile }
        let migration = destination.appendingPathComponent("Migration",isDirectory:true)
        guard try exists(migration) else { return nil }
        try checkDirectoryChain(migration)
        try checkItem(migration,directory:true)
        guard (try FileManager.default.attributesOfItem(atPath:migration.path)[.posixPermissions] as? NSNumber)?.intValue == 0o700 else { throw Failure.unsafeFile }
        let url = migration.appendingPathComponent("receipt.json")
        try checkItem(url,directory:false)
        let attributes = try FileManager.default.attributesOfItem(atPath:url.path)
        guard ((attributes[.size] as? NSNumber)?.intValue ?? Int.max) <= 4_096,
              (attributes[.posixPermissions] as? NSNumber)?.intValue == 0o600 else { throw Failure.unsafeFile }
        let data = try Data(contentsOf:url)
        let receipt: Receipt
        do { receipt = try JSONDecoder().decode(Receipt.self,from:data) } catch { throw Failure.verification }
        guard receipt.schemaVersion == 1 else { throw Failure.unsupportedSchema }
        guard receipt.sourceIdentity == "io.github.brysonjl.remy.dev",
              receipt.destinationIdentity == "io.github.brysonjl.remy",
              ["pendingPreferences","completed"].contains(receipt.phase),
              receipt.preferencesSHA256.utf8.count == 64,
              receipt.preferencesSHA256.utf8.allSatisfy({ "0123456789abcdef".utf8.contains($0) }) else { throw Failure.verification }
        return receipt
    }

    /// Foundation can retain /var even after resolvingSymlinksInPath. Permit only
    /// this fixed macOS system alias; never resolve arbitrary caller-controlled links.
    private static func physicalURL(_ url:URL) throws -> URL {
        guard url.isFileURL, !url.pathComponents.contains(where: { $0 == "." || $0 == ".." }) else { throw Failure.unsafePath }
        if url.path.hasPrefix("/var/") {
            return URL(fileURLWithPath:"/private/var/" + String(url.path.dropFirst(5)),isDirectory:url.hasDirectoryPath)
        }
        return url
    }

    private struct Inventory { var count = 0; var bytes: Int64 = 0; var historyBytes: Int64 = 0 }
    private static func checkedSum(_ first:Int64,_ second:Int64) throws -> Int64 {
        let (sum,overflow) = first.addingReportingOverflow(second)
        guard !overflow, sum >= 0 else { throw Failure.limitExceeded }
        return sum
    }
    private static func hexDigest(_ data:Data) -> String {
        SHA256.hash(data:data).map { String(format:"%02x",$0) }.joined()
    }
    private static func excluded(_ name:String,in relative:String) -> Bool {
        (relative == "CleanupProfiles" && name == ".profiles.lock")
            || (relative.hasPrefix("Models") && (name == ".staging" || name.hasPrefix(".stage-")))
    }
    /// Metadata-only preflight. Source must remain stopped throughout the path-based walk.
    /// Static links/special files are refused; this is not a sandbox against a malicious same-user writer.
    private static func inventorySource(_ root:URL,stopped:()->Bool) throws -> Inventory {
        var inventory = Inventory()
        func walk(_ url:URL,_ relative:String) throws {
            guard stopped() else { throw Failure.sourceRunning }
            var info = stat()
            guard lstat(url.path,&info) == 0, info.st_uid == geteuid(), inventory.count < maximumFiles else { throw Failure.unsafeFile }
            inventory.count += 1
            if info.st_mode & S_IFMT == S_IFDIR {
                for name in try FileManager.default.contentsOfDirectory(atPath:url.path).sorted() where !excluded(name,in:relative) {
                    try walk(url.appendingPathComponent(name),relative+"/"+name)
                }
            } else {
                guard info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1, info.st_size >= 0,
                      info.st_size <= maximumBytes - inventory.bytes else { throw Failure.unsafeFile }
                inventory.bytes += info.st_size
                if relative.hasPrefix("History/") { inventory.historyBytes = try checkedSum(inventory.historyBytes,info.st_size) }
            }
        }
        for name in allowed.sorted() where try exists(root.appendingPathComponent(name)) { try walk(root.appendingPathComponent(name),name) }
        return inventory
    }

    private static func exists(_ url: URL) throws -> Bool {
        var info = stat()
        if lstat(url.path,&info) == 0 { return true }
        if errno == ENOENT { return false }
        throw Failure.io
    }
    private static func requireAbsent(_ url: URL) throws {
        if try exists(url) { throw Failure.destinationExists }
    }
    private static func checkDirectoryChain(_ url: URL) throws {
        var current = url
        while current.path != "/" {
            var info = stat()
            guard lstat(current.path,&info) == 0, info.st_mode & S_IFMT == S_IFDIR else { throw Failure.unsafePath }
            current.deleteLastPathComponent()
        }
    }
    private static func checkItem(_ url: URL,directory:Bool) throws {
        var info = stat()
        guard lstat(url.path,&info) == 0, info.st_uid == geteuid(),
              info.st_mode & S_IFMT == (directory ? S_IFDIR : S_IFREG),
              directory || info.st_nlink == 1 else { throw Failure.unsafeFile }
    }
    private static func copyTree(_ source: URL,to destination: URL,relative:String,
                                 count:inout Int,bytes:inout Int64,stopped:()->Bool,
                                 capacity:() throws -> Int64,totalBytes:Int64,reserve:Int64) throws {
        guard stopped() else { throw Failure.sourceRunning }
        var info = stat()
        guard lstat(source.path,&info) == 0, info.st_uid == geteuid() else { throw Failure.unsafeFile }
        count += 1
        guard count <= maximumFiles else { throw Failure.limitExceeded }
        if info.st_mode & S_IFMT == S_IFDIR {
            guard mkdir(destination.path,0o700) == 0 else { throw Failure.io }
            for name in try FileManager.default.contentsOfDirectory(atPath:source.path).sorted() {
                if excluded(name,in:relative) { continue }
                try copyTree(source.appendingPathComponent(name),to:destination.appendingPathComponent(name),relative:relative+"/"+name,count:&count,bytes:&bytes,stopped:stopped,capacity:capacity,totalBytes:totalBytes,reserve:reserve)
            }
        } else {
            guard info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1, info.st_size >= 0,
                  info.st_size <= maximumBytes - bytes else { throw Failure.unsafeFile }
            guard bytes <= totalBytes, info.st_size <= totalBytes - bytes else { throw Failure.verification }
            guard try capacity() >= checkedSum(totalBytes - bytes,reserve) else { throw Failure.insufficientSpace }
            bytes += info.st_size
            let input = open(source.path,O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
            guard input >= 0 else { throw Failure.unsafeFile }
            defer { close(input) }
            var actual = stat()
            guard fstat(input,&actual) == 0, actual.st_dev == info.st_dev, actual.st_ino == info.st_ino,
                  actual.st_size == info.st_size, actual.st_nlink == 1 else { throw Failure.unsafeFile }
            let output = open(destination.path,O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC,0o600)
            guard output >= 0 else { throw Failure.io }
            defer { close(output) }
            var digest = SHA256(), buffer = [UInt8](repeating:0,count:1_048_576), total: Int64 = 0
            while true {
                guard stopped() else { throw Failure.sourceRunning }
                let size = buffer.withUnsafeMutableBytes { Darwin.read(input,$0.baseAddress,$0.count) }
                if size < 0 && errno == EINTR { continue }
                guard size >= 0 else { throw Failure.io }
                if size == 0 { break }
                guard total <= info.st_size, Int64(size) <= info.st_size - total else { throw Failure.verification }
                let data = Data(buffer.prefix(size)); digest.update(data:data)
                try writeAll(data,to:output); total += Int64(size)
            }
            var after = stat()
            guard fstat(input,&after) == 0, after.st_size == info.st_size, total == info.st_size,
                  after.st_mtimespec.tv_sec == info.st_mtimespec.tv_sec,
                  after.st_mtimespec.tv_nsec == info.st_mtimespec.tv_nsec,
                  fsync(output) == 0 else { throw Failure.verification }
            let expected = Data(digest.finalize())
            guard try fileDigest(destination) == expected else { throw Failure.verification }
        }
    }
    private static func fileDigest(_ url:URL) throws -> Data {
        let file = try FileHandle(forReadingFrom:url); defer { try? file.close() }
        var digest = SHA256()
        while let bytes = try file.read(upToCount:1_048_576), !bytes.isEmpty { digest.update(data:bytes) }
        return Data(digest.finalize())
    }
    private static func writeAll(_ data:Data,to descriptor:Int32) throws {
        try data.withUnsafeBytes { bytes in
            var offset = 0
            while offset < bytes.count {
                let size = Darwin.write(descriptor,bytes.baseAddress!.advanced(by:offset),bytes.count-offset)
                if size < 0 && errno == EINTR { continue }
                guard size > 0 else { throw Failure.io }
                offset += size
            }
        }
    }
    private static func writePrivate(_ data:Data,to url:URL) throws {
        let descriptor = open(url.path,O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC,0o600)
        guard descriptor >= 0 else { throw Failure.io }
        defer { close(descriptor) }
        try writeAll(data,to:descriptor)
        guard fsync(descriptor) == 0 else { throw Failure.io }
    }
    private static func syncDirectories(_ root:URL) throws {
        for name in try FileManager.default.contentsOfDirectory(atPath:root.path) {
            let child = root.appendingPathComponent(name)
            var info = stat()
            guard lstat(child.path,&info) == 0 else { throw Failure.io }
            if info.st_mode & S_IFMT == S_IFDIR { try syncDirectories(child) }
        }
        let descriptor = open(root.path,O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard descriptor >= 0 else { throw Failure.io }
        defer { close(descriptor) }
        guard fsync(descriptor) == 0 else { throw Failure.io }
    }

    private static func normalizeDatabase(_ url:URL) throws {
        var source: OpaquePointer?, target: OpaquePointer?
        guard sqlite3_open_v2(url.path,&source,SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX,nil) == SQLITE_OK else {
            if let source { sqlite3_close(source) }; throw Failure.database
        }
        defer { sqlite3_close(source) }
        sqlite3_busy_timeout(source,1_000)
        try verifyDatabase(source)
        let baseline = try databaseDigest(source)
        let normalized = url.deletingLastPathComponent().appendingPathComponent(".history-normalized-\(UUID().uuidString).sqlite")
        guard sqlite3_open_v2(normalized.path,&target,SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_EXCLUSIVE,nil) == SQLITE_OK else {
            if let target { sqlite3_close(target) }; throw Failure.database
        }
        defer { sqlite3_close(target) }
        guard let backup = sqlite3_backup_init(target,"main",source,"main") else { throw Failure.database }
        let copied = sqlite3_backup_step(backup,-1), finished = sqlite3_backup_finish(backup)
        guard copied == SQLITE_DONE, finished == SQLITE_OK else { throw Failure.database }
        try verifyDatabase(target)
        guard try databaseDigest(target) == baseline else { throw Failure.verification }
        guard sqlite3_exec(target,"PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;",nil,nil,nil) == SQLITE_OK else { throw Failure.database }
        guard sqlite3_close(target) == SQLITE_OK else { throw Failure.database }; target = nil
        guard sqlite3_close(source) == SQLITE_OK else { throw Failure.database }; source = nil
        for suffix in ["","-wal","-shm","-journal"] {
            let old = URL(fileURLWithPath:url.path+suffix)
            if try exists(old) { try FileManager.default.removeItem(at:old) }
        }
        guard rename(normalized.path,url.path) == 0, chmod(url.path,0o600) == 0 else { throw Failure.io }
        let descriptor = open(url.path,O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        guard descriptor >= 0 else { throw Failure.io }
        defer { close(descriptor) }
        guard fsync(descriptor) == 0 else { throw Failure.io }
    }
    private static func verifyDatabase(_ database:OpaquePointer?) throws {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database,"PRAGMA user_version",-1,&statement,nil) == SQLITE_OK else { throw Failure.database }
        guard sqlite3_step(statement) == SQLITE_ROW, (1...2).contains(sqlite3_column_int(statement,0)) else { sqlite3_finalize(statement); throw Failure.unsupportedSchema }
        sqlite3_finalize(statement); statement = nil
        guard sqlite3_prepare_v2(database,"PRAGMA integrity_check",-1,&statement,nil) == SQLITE_OK else { throw Failure.database }
        guard sqlite3_step(statement) == SQLITE_ROW, sqlite3_column_text(statement,0).map({ String(cString:$0) }) == "ok",
              sqlite3_step(statement) == SQLITE_DONE else { sqlite3_finalize(statement); throw Failure.verification }
        sqlite3_finalize(statement); statement = nil
        guard sqlite3_prepare_v2(database,"PRAGMA foreign_key_check",-1,&statement,nil) == SQLITE_OK else { throw Failure.database }
        defer { sqlite3_finalize(statement) }
        guard sqlite3_step(statement) == SQLITE_DONE else { throw Failure.verification }
    }
    private static func databaseDigest(_ database:OpaquePointer?) throws -> Data {
        let tables = ["sessions","attempts","deliveries","tombstones","repository_state","legacy_imports"]
        var digest = SHA256()
        for table in tables {
            var statement: OpaquePointer?
            guard sqlite3_prepare_v2(database,"SELECT * FROM \(table) ORDER BY rowid",-1,&statement,nil) == SQLITE_OK else { throw Failure.database }
            defer { sqlite3_finalize(statement) }
            digest.update(data:Data(table.utf8))
            while true {
                let code = sqlite3_step(statement)
                if code == SQLITE_DONE { break }
                guard code == SQLITE_ROW else { throw Failure.database }
                for column in 0..<sqlite3_column_count(statement) {
                    let type = sqlite3_column_type(statement,column)
                    var header = "\(type):"
                    switch type {
                    case SQLITE_NULL: header += "null;"
                    case SQLITE_INTEGER: header += "\(sqlite3_column_int64(statement,column));"
                    case SQLITE_FLOAT: header += "\(sqlite3_column_double(statement,column).bitPattern);"
                    default:
                        let size = Int(sqlite3_column_bytes(statement,column))
                        header += "\(size):"
                        digest.update(data:Data(header.utf8))
                        if size > 0, let bytes = sqlite3_column_blob(statement,column) { digest.update(data:Data(bytes:bytes,count:size)) }
                        continue
                    }
                    digest.update(data:Data(header.utf8))
                }
                digest.update(data:Data("row;".utf8))
            }
        }
        return Data(digest.finalize())
    }
}
