import Foundation
import SQLite3
import Darwin
import XCTest
@testable import Dictate_Anywhere

@MainActor final class RemyLocalDataMigrationTests: XCTestCase {
    private func fixture() throws -> (URL,URL,Data) {
        let root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath().appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
        let source = root.appendingPathComponent("Dev",isDirectory:true), destination = root.appendingPathComponent("Remy",isDirectory:true)
        try FileManager.default.createDirectory(at:source,withIntermediateDirectories:false,attributes:[.posixPermissions:0o700])
        addTeardownBlock { try? FileManager.default.removeItem(at:root) }
        let preferences = try PropertyListSerialization.data(fromPropertyList:["syntheticSetting":"synthetic value"],format:.binary,options:0)
        return (source,destination,preferences)
    }
    private func file(_ root:URL,_ name:String,_ text:String) throws {
        let url = root.appendingPathComponent(name)
        try FileManager.default.createDirectory(at:url.deletingLastPathComponent(),withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
        try Data(text.utf8).write(to:url)
    }
    private func sql(_ database:OpaquePointer?,_ statement:String) throws {
        guard sqlite3_exec(database,statement,nil,nil,nil) == SQLITE_OK else { throw RemyLocalDataMigration.Failure.database }
    }
    func testCopiesOnlyAllowedLocalDataAndPreservesSourceAndPendingPreferences() throws {
        let (source,destination,preferences) = try fixture()
        for path in ["History/legacy.json","Recovery/fixture.caf","CleanupProfiles/profiles.json","Models/model.bin","Sounds/custom.wav"] { try file(source,path,"synthetic fixture") }
        try file(source,"Unrelated/private.txt","excluded")
        try file(source,"CleanupProfiles/.profiles.lock","")
        try file(source,"Models/.staging/unfinished.bin","excluded")
        let receipt = try RemyLocalDataMigration.migrate(source:source,destination:destination,preferences:preferences,sourceIsStopped:{true})
        XCTAssertEqual(receipt.phase,"pendingPreferences")
        XCTAssertEqual(try RemyLocalDataMigration.pendingReceipt(at:destination),receipt)
        XCTAssertEqual(try RemyLocalDataMigration.pendingPreferences(at:destination),preferences)
        for path in ["History/legacy.json","Recovery/fixture.caf","CleanupProfiles/profiles.json","Models/model.bin","Sounds/custom.wav"] {
            XCTAssertEqual(try Data(contentsOf:source.appendingPathComponent(path)),try Data(contentsOf:destination.appendingPathComponent(path)))
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath:destination.appendingPathComponent("Unrelated").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath:destination.appendingPathComponent("CleanupProfiles/.profiles.lock").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath:destination.appendingPathComponent("Models/.staging").path))
        XCTAssertEqual((try FileManager.default.attributesOfItem(atPath:destination.path)[.posixPermissions] as? NSNumber)?.intValue,0o700)
        XCTAssertEqual((try FileManager.default.attributesOfItem(atPath:destination.appendingPathComponent("Migration/preferences.plist").path)[.posixPermissions] as? NSNumber)?.intValue,0o600)
        XCTAssertFalse(String(data:try JSONEncoder().encode(receipt),encoding:.utf8)!.contains("synthetic value"))
        try RemyLocalDataMigration.markPreferencesCommitted(at:destination,migrationID:receipt.migrationID)
        XCTAssertNil(try RemyLocalDataMigration.pendingReceipt(at:destination))
        XCTAssertNil(try RemyLocalDataMigration.pendingPreferences(at:destination))
        XCTAssertEqual(try Data(contentsOf:destination.appendingPathComponent("Migration/preferences.plist")),preferences)
        try RemyLocalDataMigration.markPreferencesCommitted(at:destination,migrationID:receipt.migrationID)
    }
    func testKnownVarAliasWorksButArbitraryAncestorSymlinkStillFails() throws {
        let (source,destination,preferences) = try fixture()
        let aliasSource = URL(fileURLWithPath:source.path.replacingOccurrences(of:"/private/var/",with:"/var/"),isDirectory:true)
        let aliasDestination = URL(fileURLWithPath:destination.path.replacingOccurrences(of:"/private/var/",with:"/var/"),isDirectory:true)
        let receipt = try RemyLocalDataMigration.migrate(source:aliasSource,destination:aliasDestination,preferences:preferences,sourceIsStopped:{true})
        XCTAssertEqual(try RemyLocalDataMigration.pendingReceipt(at:aliasDestination),receipt)
        XCTAssertEqual(try RemyLocalDataMigration.pendingPreferences(at:aliasDestination),preferences)
        try RemyLocalDataMigration.markPreferencesCommitted(at:aliasDestination,migrationID:receipt.migrationID)
        XCTAssertNil(try RemyLocalDataMigration.pendingReceipt(at:aliasDestination))
        let parent = source.deletingLastPathComponent()
        let link = parent.appendingPathComponent("alias")
        try FileManager.default.createSymbolicLink(at:link,withDestinationURL:parent)
        XCTAssertThrowsError(try RemyLocalDataMigration.migrate(source:link.appendingPathComponent("Dev"),destination:link.appendingPathComponent("Other"),preferences:preferences,sourceIsStopped:{true})) {
            XCTAssertEqual($0 as? RemyLocalDataMigration.Failure,.unsafePath)
        }
    }
    func testExistingDestinationAndRunningSourceAreNeverOverwritten() throws {
        let (source,destination,preferences) = try fixture()
        try FileManager.default.createDirectory(at:destination,withIntermediateDirectories:false)
        try file(destination,"sentinel","kept")
        XCTAssertThrowsError(try RemyLocalDataMigration.migrate(source:source,destination:destination,preferences:preferences,sourceIsStopped:{true})) {
            XCTAssertEqual($0 as? RemyLocalDataMigration.Failure,.destinationExists)
        }
        XCTAssertEqual(try Data(contentsOf:destination.appendingPathComponent("sentinel")),Data("kept".utf8))
        XCTAssertThrowsError(try RemyLocalDataMigration.migrate(source:source,destination:destination,preferences:preferences,sourceIsStopped:{false})) {
            XCTAssertEqual($0 as? RemyLocalDataMigration.Failure,.sourceRunning)
        }
    }
    func testStoppedPredicateChangingMidCopyLeavesDestinationAbsentAndSourceIntact() throws {
        let (source,destination,preferences) = try fixture()
        try file(source,"Models/fixture.bin","source kept")
        var checks = 0
        XCTAssertThrowsError(try RemyLocalDataMigration.migrate(source:source,destination:destination,preferences:preferences,sourceIsStopped:{ checks += 1; return checks < 5 })) {
            XCTAssertEqual($0 as? RemyLocalDataMigration.Failure,.sourceRunning)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath:destination.path))
        XCTAssertEqual(try Data(contentsOf:source.appendingPathComponent("Models/fixture.bin")),Data("source kept".utf8))
        XCTAssertFalse(try FileManager.default.contentsOfDirectory(atPath:source.deletingLastPathComponent().path).contains { $0.hasPrefix(".remy-migration-") })
    }
    func testSymlinkHardlinkAndSpecialSourceAreRejected() throws {
        for kind in 0..<3 {
            let (source,destination,preferences) = try fixture()
            try FileManager.default.createDirectory(at:source.appendingPathComponent("Models"),withIntermediateDirectories:true)
            let target = source.appendingPathComponent("fixture"), invalid = source.appendingPathComponent("Models/invalid")
            try Data("synthetic".utf8).write(to:target)
            if kind == 0 { try FileManager.default.createSymbolicLink(at:invalid,withDestinationURL:target) }
            if kind == 1 { XCTAssertEqual(link(target.path,invalid.path),0) }
            if kind == 2 { XCTAssertEqual(mkfifo(invalid.path,0o600),0) }
            XCTAssertThrowsError(try RemyLocalDataMigration.migrate(source:source,destination:destination,preferences:preferences,sourceIsStopped:{true}))
            XCTAssertFalse(FileManager.default.fileExists(atPath:destination.path))
            XCTAssertEqual(try Data(contentsOf:target),Data("synthetic".utf8))
        }
    }
    func testCommittedWALDataIsIncludedWithoutMutatingSourceCohort() throws {
        let (source,destination,preferences) = try fixture()
        let history = source.appendingPathComponent("History")
        try FileManager.default.createDirectory(at:history,withIntermediateDirectories:true)
        let url = history.appendingPathComponent("history.sqlite")
        var database:OpaquePointer?
        XCTAssertEqual(sqlite3_open(url.path,&database),SQLITE_OK)
        defer { sqlite3_close(database) }
        try sql(database,"PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; PRAGMA user_version=1; CREATE TABLE sessions(id TEXT,raw TEXT); CREATE TABLE attempts(id TEXT); CREATE TABLE deliveries(id TEXT); CREATE TABLE tombstones(id TEXT); CREATE TABLE repository_state(generation INTEGER); CREATE TABLE legacy_imports(source BLOB); INSERT INTO sessions VALUES('synthetic-id','中文 🦾 WAL');")
        let before = try Data(contentsOf:url), beforeWAL = try Data(contentsOf:URL(fileURLWithPath:url.path+"-wal"))
        _ = try RemyLocalDataMigration.migrate(source:source,destination:destination,preferences:preferences,sourceIsStopped:{true})
        XCTAssertEqual(try Data(contentsOf:url),before)
        XCTAssertEqual(try Data(contentsOf:URL(fileURLWithPath:url.path+"-wal")),beforeWAL)
        var copied:OpaquePointer?
        XCTAssertEqual(sqlite3_open(destination.appendingPathComponent("History/history.sqlite").path,&copied),SQLITE_OK)
        defer { sqlite3_close(copied) }
        var statement:OpaquePointer?
        XCTAssertEqual(sqlite3_prepare_v2(copied,"SELECT raw FROM sessions",-1,&statement,nil),SQLITE_OK)
        defer { sqlite3_finalize(statement) }
        XCTAssertEqual(sqlite3_step(statement),SQLITE_ROW)
        XCTAssertEqual(sqlite3_column_text(statement,0).map { String(cString:$0) },"中文 🦾 WAL")
        XCTAssertFalse(FileManager.default.fileExists(atPath:destination.appendingPathComponent("History/history.sqlite-wal").path))
    }
    func testSourceGrowingAfterOpenIsRejectedWithoutPublishing() throws {
        let (source,destination,preferences) = try fixture()
        try file(source,"Models/fixture.bin","initial")
        var grew = false
        let parent = source.deletingLastPathComponent()
        XCTAssertThrowsError(try RemyLocalDataMigration.migrate(source:source,destination:destination,preferences:preferences,sourceIsStopped:{
            if !grew,
               let stage = try? FileManager.default.contentsOfDirectory(at:parent,includingPropertiesForKeys:nil).first(where: { $0.lastPathComponent.hasPrefix(".remy-migration-") }),
               FileManager.default.fileExists(atPath:stage.appendingPathComponent("Models/fixture.bin").path) {
                grew = true
                if let handle = try? FileHandle(forWritingTo:source.appendingPathComponent("Models/fixture.bin")) {
                    _ = try? handle.seekToEnd()
                    try? handle.write(contentsOf:Data(repeating:0x41,count:32_768))
                    try? handle.close()
                }
            }
            return true
        }))
        XCTAssertTrue(grew)
        XCTAssertFalse(FileManager.default.fileExists(atPath:destination.path))
    }
    func testHotDeleteJournalIsRecoveredOnlyInStage() throws {
        let (source,destination,preferences) = try fixture()
        let history = source.appendingPathComponent("History")
        try FileManager.default.createDirectory(at:history,withIntermediateDirectories:true)
        let url = history.appendingPathComponent("history.sqlite")
        var database:OpaquePointer?
        XCTAssertEqual(sqlite3_open(url.path,&database),SQLITE_OK)
        try sql(database,"PRAGMA journal_mode=DELETE; PRAGMA user_version=1; CREATE TABLE sessions(id TEXT,raw TEXT); CREATE TABLE attempts(id TEXT); CREATE TABLE deliveries(id TEXT); CREATE TABLE tombstones(id TEXT); CREATE TABLE repository_state(generation INTEGER); CREATE TABLE legacy_imports(source BLOB); INSERT INTO sessions VALUES('fixture','committed');")
        XCTAssertEqual(sqlite3_close(database),SQLITE_OK)
        let script = #"""
        import ctypes, os, sys
        lib = ctypes.CDLL('/usr/lib/libsqlite3.dylib')
        lib.sqlite3_open.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]
        lib.sqlite3_exec.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
        db = ctypes.c_void_p()
        if lib.sqlite3_open(os.fsencode(sys.argv[1]), ctypes.byref(db)) != 0: os._exit(81)
        if lib.sqlite3_exec(db, b"PRAGMA cache_size=1; BEGIN IMMEDIATE; UPDATE sessions SET raw=hex(zeroblob(262144));", None, None, None) != 0: os._exit(82)
        os._exit(73)
        """#
        let child = Process()
        child.executableURL = URL(fileURLWithPath:"/usr/bin/python3"); child.arguments = ["-c",script,url.path]
        child.standardOutput = FileHandle.nullDevice; child.standardError = FileHandle.nullDevice
        try child.run(); child.waitUntilExit()
        XCTAssertEqual(child.terminationStatus,73)
        let journalURL = URL(fileURLWithPath:url.path+"-journal")
        let before = try Data(contentsOf:url), journal = try Data(contentsOf:journalURL)
        _ = try RemyLocalDataMigration.migrate(source:source,destination:destination,preferences:preferences,sourceIsStopped:{true})
        XCTAssertEqual(try Data(contentsOf:url),before)
        XCTAssertEqual(try Data(contentsOf:journalURL),journal)
        var copied:OpaquePointer?
        XCTAssertEqual(sqlite3_open(destination.appendingPathComponent("History/history.sqlite").path,&copied),SQLITE_OK)
        defer { sqlite3_close(copied) }
        var statement:OpaquePointer?
        XCTAssertEqual(sqlite3_prepare_v2(copied,"SELECT raw FROM sessions",-1,&statement,nil),SQLITE_OK)
        defer { sqlite3_finalize(statement) }
        XCTAssertEqual(sqlite3_step(statement),SQLITE_ROW)
        XCTAssertEqual(sqlite3_column_text(statement,0).map { String(cString:$0) },"committed")
    }
    func testValidPlistTamperingCannotBeAppliedOrMarkedComplete() throws {
        let (source,destination,preferences) = try fixture()
        let receipt = try RemyLocalDataMigration.migrate(source:source,destination:destination,preferences:preferences,sourceIsStopped:{true})
        let different = try PropertyListSerialization.data(fromPropertyList:["syntheticSetting":"changed"],format:.binary,options:0)
        try different.write(to:destination.appendingPathComponent("Migration/preferences.plist"))
        XCTAssertThrowsError(try RemyLocalDataMigration.pendingPreferences(at:destination)) {
            XCTAssertEqual($0 as? RemyLocalDataMigration.Failure,.verification)
        }
        XCTAssertThrowsError(try RemyLocalDataMigration.markPreferencesCommitted(at:destination,migrationID:receipt.migrationID))
        XCTAssertNotNil(try RemyLocalDataMigration.pendingReceipt(at:destination))
    }
    func testLowSpacePreflightReadsOnlyMetadataAndLeavesDestinationAbsent() throws {
        let (source,destination,preferences) = try fixture()
        try file(source,"Models/fixture.bin","kept")
        XCTAssertThrowsError(try RemyLocalDataMigration.migrate(source:source,destination:destination,preferences:preferences,sourceIsStopped:{true},availableCapacity:{0})) {
            XCTAssertEqual($0 as? RemyLocalDataMigration.Failure,.insufficientSpace)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath:destination.path))
        XCTAssertEqual(try Data(contentsOf:source.appendingPathComponent("Models/fixture.bin")),Data("kept".utf8))
        XCTAssertFalse(try FileManager.default.contentsOfDirectory(atPath:source.deletingLastPathComponent().path).contains { $0.hasPrefix(".remy-migration-") })
    }
    func testMalformedOrNewerPublishedReceiptBlocksAndCannotBeMarkedComplete() throws {
        let (source,destination,preferences) = try fixture()
        let receipt = try RemyLocalDataMigration.migrate(source:source,destination:destination,preferences:preferences,sourceIsStopped:{true})
        XCTAssertThrowsError(try RemyLocalDataMigration.markPreferencesCommitted(at:destination,migrationID:UUID()))
        let url = destination.appendingPathComponent("Migration/receipt.json")
        let future = RemyLocalDataMigration.Receipt(schemaVersion:99,migrationID:receipt.migrationID,sourceIdentity:receipt.sourceIdentity,destinationIdentity:receipt.destinationIdentity,phase:"completed",preferencesSHA256:receipt.preferencesSHA256)
        try JSONEncoder().encode(future).write(to:url)
        XCTAssertThrowsError(try RemyLocalDataMigration.pendingReceipt(at:destination)) {
            XCTAssertEqual($0 as? RemyLocalDataMigration.Failure,.unsupportedSchema)
        }
        XCTAssertThrowsError(try RemyLocalDataMigration.markPreferencesCommitted(at:destination,migrationID:receipt.migrationID))
    }
}
