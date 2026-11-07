import Foundation
import XCTest
import SQLite3
@testable import Dictate_Anywhere

final class RemyLegacyHistorySnapshotTests: XCTestCase {
    private func fixture() throws -> (UserDefaults, URL, URL) {
        let name = "RemyLegacyHistorySnapshotTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock {
            defaults.removePersistentDomain(forName: name)
            try? FileManager.default.removeItem(at: root)
        }
        return (defaults, root, root.appendingPathComponent("backups/history"))
    }
    func testMissingAndMalformedPreferenceAreDistinctAndDoNotCreateBackup() throws {
        let (defaults, root, backup) = try fixture()
        XCTAssertNil(try RemyLegacyHistorySnapshot.capture(from: defaults, backupDirectory: backup, ownedRoot: root))
        XCTAssertFalse(FileManager.default.fileExists(atPath: backup.path))
        defaults.set("not Data", forKey: "transcriptHistory")
        XCTAssertThrowsError(try RemyLegacyHistorySnapshot.capture(from: defaults, backupDirectory: backup, ownedRoot: root)) {
            XCTAssertEqual($0 as? RemyLegacyHistorySnapshot.Failure, .malformedPreference)
        }
        XCTAssertFalse(FileManager.default.fileExists(atPath: backup.path))
    }
    func testOriginalOverFiftyEntriesCapturedExactlyBeforeLaterRewrite() throws {
        let (defaults, root, backup) = try fixture()
        let entries = (0..<75).map { TranscriptHistoryEntry(id:UUID(),text:"原文 🦾 \($0)",createdAt:Date(timeIntervalSince1970:Double($0)),rawText:"raw \($0)") }
        let original = try JSONEncoder().encode(entries)
        defaults.set(original,forKey:"transcriptHistory")
        let snapshot = try XCTUnwrap(RemyLegacyHistorySnapshot.capture(from: defaults, backupDirectory: backup, ownedRoot: root))
        defaults.set(try JSONEncoder().encode(Array(entries.suffix(50))),forKey:"transcriptHistory")
        XCTAssertEqual(snapshot.data,original)
        XCTAssertEqual(try Data(contentsOf:snapshot.backupURL),original)
        XCTAssertEqual(try JSONDecoder().decode([TranscriptHistoryEntry].self,from:snapshot.data),entries)
        XCTAssertTrue(snapshot.backupURL.lastPathComponent.hasPrefix("legacy-history-"))
        XCTAssertEqual(snapshot.backupURL.lastPathComponent.count,"legacy-history-".count+64+".json".count)
        let directoryMode = try FileManager.default.attributesOfItem(atPath:backup.path)[.posixPermissions] as? NSNumber
        let fileMode = try FileManager.default.attributesOfItem(atPath:snapshot.backupURL.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(directoryMode?.intValue,0o700); XCTAssertEqual(fileMode?.intValue,0o600)
    }
    func testIdenticalCaptureReusesVerifiedImmutableBackupAndConflictIsNotOverwritten() throws {
        let (defaults,root,backup) = try fixture()
        let bytes = Data("synthetic legacy bytes".utf8)
        defaults.set(bytes,forKey:"transcriptHistory")
        let first = try XCTUnwrap(RemyLegacyHistorySnapshot.capture(from:defaults,backupDirectory:backup,ownedRoot:root))
        let second = try XCTUnwrap(RemyLegacyHistorySnapshot.capture(from:defaults,backupDirectory:backup,ownedRoot:root))
        XCTAssertEqual(first.backupURL,second.backupURL)
        let conflicting = Data("conflicting fixture".utf8)
        try conflicting.write(to:first.backupURL)
        XCTAssertThrowsError(try RemyLegacyHistorySnapshot.capture(from:defaults,backupDirectory:backup,ownedRoot:root)) {
            XCTAssertEqual($0 as? RemyLegacyHistorySnapshot.Failure,.conflictingBackup)
        }
        XCTAssertEqual(try Data(contentsOf:first.backupURL),conflicting)
        XCTAssertEqual(defaults.data(forKey:"transcriptHistory"),bytes)
    }
    func testEscapingAndSymlinkedBackupDirectoriesAreRejected() throws {
        let (defaults,root,_) = try fixture()
        defaults.set(Data("fixture".utf8),forKey:"transcriptHistory")
        let sibling = root.deletingLastPathComponent().appendingPathComponent(root.lastPathComponent+"-escape")
        XCTAssertThrowsError(try RemyLegacyHistorySnapshot.capture(from:defaults,backupDirectory:sibling,ownedRoot:root)) {
            XCTAssertEqual($0 as? RemyLegacyHistorySnapshot.Failure,.unconfinedBackup)
        }
        let external = root.appendingPathComponent("external")
        try FileManager.default.createDirectory(at:external,withIntermediateDirectories:false)
        let link = root.appendingPathComponent("link")
        try FileManager.default.createSymbolicLink(at:link,withDestinationURL:external)
        XCTAssertThrowsError(try RemyLegacyHistorySnapshot.capture(from:defaults,backupDirectory:link.appendingPathComponent("nested"),ownedRoot:root))
        XCTAssertFalse(FileManager.default.fileExists(atPath:external.appendingPathComponent("nested").path))
    }
    func testSymlinkedBackupFileIsRejectedWithoutChangingTarget() throws {
        let (defaults,root,backup) = try fixture()
        defaults.set(Data("fixture".utf8),forKey:"transcriptHistory")
        let snapshot = try XCTUnwrap(RemyLegacyHistorySnapshot.capture(from:defaults,backupDirectory:backup,ownedRoot:root))
        let target = root.appendingPathComponent("untouched")
        let content = Data("external synthetic content".utf8)
        try content.write(to:target)
        try FileManager.default.removeItem(at:snapshot.backupURL)
        try FileManager.default.createSymbolicLink(at:snapshot.backupURL,withDestinationURL:target)
        XCTAssertThrowsError(try RemyLegacyHistorySnapshot.capture(from:defaults,backupDirectory:backup,ownedRoot:root)) {
            XCTAssertEqual($0 as? RemyLegacyHistorySnapshot.Failure,.unsafeFilesystem)
        }
        XCTAssertEqual(try Data(contentsOf:target),content)
    }
    func testAbruptChildExitRollsBackSQLiteTransactionAndIntegrityRemainsOK() async throws {
        let (_,root,_) = try fixture()
        let url = root.appendingPathComponent("crash.sqlite")
        let repository = try RemyHistoryRepository(url:url)
        let value = RemyHistorySession(id:UUID(),createdAt:Date(timeIntervalSince1970:1),rawText:"raw",selectedText:"committed")
        let saved = await repository.commitRaw(value,generation:0)
        XCTAssertEqual(saved,.saved)
        // This child opens only the fixture DB and exits without sqlite3_close/destructors.
        // No signal, process kill, shell, user history or runtime dependency download is involved.
        let script = #"""
        import ctypes, os, sys
        lib = ctypes.CDLL('/usr/lib/libsqlite3.dylib')
        lib.sqlite3_open.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]
        lib.sqlite3_exec.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
        db = ctypes.c_void_p()
        if lib.sqlite3_open(os.fsencode(sys.argv[1]), ctypes.byref(db)) != 0: os._exit(81)
        if lib.sqlite3_exec(db, b"PRAGMA cache_size=1; BEGIN IMMEDIATE; UPDATE sessions SET selected=hex(zeroblob(262144));", None, None, None) != 0: os._exit(82)
        os._exit(73)
        """#
        let child = Process()
        child.executableURL = URL(fileURLWithPath:"/usr/bin/python3")
        child.arguments = ["-c",script,url.path]
        child.standardOutput = FileHandle.nullDevice; child.standardError = FileHandle.nullDevice
        try child.run()
        child.waitUntilExit()
        XCTAssertEqual(child.terminationStatus,73)
        let reopened = try RemyHistoryRepository(url:url)
        let rows = try await reopened.page()
        XCTAssertEqual(rows.first?.selectedText,"committed")
        XCTAssertEqual(rows.first?.rawText,"raw")
        var db: OpaquePointer?; XCTAssertEqual(sqlite3_open(url.path,&db),SQLITE_OK)
        defer { sqlite3_close(db) }
        var stmt: OpaquePointer?; XCTAssertEqual(sqlite3_prepare_v2(db,"PRAGMA integrity_check",-1,&stmt,nil),SQLITE_OK)
        defer { sqlite3_finalize(stmt) }
        XCTAssertEqual(sqlite3_step(stmt),SQLITE_ROW)
        XCTAssertEqual(sqlite3_column_text(stmt,0).map { String(cString:$0) },"ok")
    }
}
