import Foundation
import SQLite3
import XCTest
@testable import Dictate_Anywhere

final class RemyHistoryRepositoryTests: XCTestCase {
    private func fixture() throws -> (URL, URL) {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }
        return (directory, directory.appendingPathComponent("history.sqlite"))
    }
    private func session(_ text: String = "你好 👩🏽‍💻 café", id: UUID = UUID(), date: Date = Date(timeIntervalSince1970: 1234.5)) -> RemyHistorySession {
        .init(id: id, createdAt: date, rawText: "raw \(text)", selectedText: text, engine: "synthetic", model: "fixture", language: "zh", settingsVersion: 1, instructionVersion: 1)
    }
    private func execute(_ url: URL, _ sql: String) throws {
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open(url.path, &db), SQLITE_OK)
        defer { sqlite3_close(db) }
        XCTAssertEqual(sqlite3_exec(db, sql, nil, nil, nil), SQLITE_OK)
    }
    func testUnicodeRoundTripFinalAttemptAndDeliverySurviveReopen() async throws {
        let (_, url) = try fixture()
        let value = session()
        let repository = try RemyHistoryRepository(url: url)
        let saved = await repository.commitRaw(value, generation: 0)
        XCTAssertEqual(saved, .saved)
        let attempt = RemyHistoryAttempt(id: UUID(), sessionID: value.id, createdAt: value.createdAt, input: value.rawText!, output: "最终 🦾", provider: "synthetic", model: "fixture", instructionVersion: 1, failureCategory: nil, prompt: "synthetic immutable instruction", profile: .init(profileID: UUID(), cleanupMode: "synthetic", reasoningEnabled: false, contextEnabled: false, style: "plain", structure: nil))
        let final = await repository.commitFinal(attempt, selectOutput: true)
        XCTAssertEqual(final, .saved)
        let delivery = await repository.recordDelivery(.init(id: UUID(), sessionID: value.id, createdAt: value.createdAt, payload: "最终 🦾", outcome: .intent))
        XCTAssertEqual(delivery, .saved)
        let reopened = try RemyHistoryRepository(url: url)
        let rows = try await reopened.page()
        XCTAssertEqual(rows.count, 1)
        XCTAssertEqual(rows.first?.rawText, value.rawText)
        XCTAssertEqual(rows.first?.selectedText, "最终 🦾")
        XCTAssertEqual(rows.first?.engine, "synthetic")
        let attempts = try await reopened.attempts(sessionID: value.id)
        let deliveries = try await reopened.deliveries(sessionID: value.id)
        XCTAssertEqual(attempts, [attempt])
        XCTAssertEqual(deliveries.map(\.outcome), [.uncertain])
        XCTAssertEqual(deliveries.first?.payload, "最终 🦾")
    }
    func testHistoryOffCreatesNoDatabaseAndIsNotFailure() async throws {
        let (_, url) = try fixture()
        let repository = try RemyHistoryRepository(url: url, historyEnabled: false)
        let result = await repository.commitRaw(session(), generation: 0)
        XCTAssertEqual(result, .historyOff)
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
    }
    func testSchemaOneUpgradePreservesAttemptsAndBacksUpBeforeAddingTiming() async throws {
        let (directory, url) = try fixture()
        let repository = try RemyHistoryRepository(url: url)
        let value = session()
        _ = await repository.commitRaw(value, generation: 0)
        let original = RemyHistoryAttempt(id: UUID(), sessionID: value.id, createdAt: value.createdAt,
            input: "synthetic input", output: nil, provider: "fixture", model: "fixture",
            instructionVersion: 1, failureCategory: "providerFailure")
        _ = await repository.commitFinal(original, selectOutput: false)
        // Strip just the additive v2 fields to recreate a real schema-1 database.
        try execute(url, "ALTER TABLE attempts DROP COLUMN started; ALTER TABLE attempts DROP COLUMN duration_ms; PRAGMA user_version=1;")
        let upgraded = try RemyHistoryRepository(url: url)
        let oldAttempts = try await upgraded.attempts(sessionID: value.id)
        XCTAssertEqual(oldAttempts, [original])
        let timed = RemyHistoryAttempt(id: UUID(), sessionID: value.id, createdAt: value.createdAt.addingTimeInterval(2),
            input: "retry", output: "clean", provider: "fixture", model: "fixture", instructionVersion: 1,
            failureCategory: nil, index: 2, startedAt: value.createdAt, durationMilliseconds: 2000)
        let saved = await upgraded.commitFinal(timed, selectOutput: true)
        XCTAssertEqual(saved, .saved)
        let reopened = try RemyHistoryRepository(url: url, readOnly: true)
        let all = try await reopened.attempts(sessionID: value.id)
        XCTAssertEqual(all, [original, timed])
        let backups = try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
            .filter { $0.lastPathComponent.hasPrefix("history-before-v2-") }
        XCTAssertEqual(backups.count, 1)
        let backup = try XCTUnwrap(backups.first)
        let attributes = try FileManager.default.attributesOfItem(atPath: backup.path)
        XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o600)
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open_v2(backup.path, &db, SQLITE_OPEN_READONLY, nil), SQLITE_OK)
        defer { sqlite3_close(db) }
        var stmt: OpaquePointer?
        XCTAssertEqual(sqlite3_prepare_v2(db, "SELECT failure FROM attempts WHERE attempt_index=1", -1, &stmt, nil), SQLITE_OK)
        defer { sqlite3_finalize(stmt) }
        XCTAssertEqual(sqlite3_step(stmt), SQLITE_ROW)
        XCTAssertEqual(sqlite3_column_text(stmt, 0).map { String(cString: $0) }, "providerFailure")
        // The backup itself stays schema 1 and has no new timing column.
        var version: OpaquePointer?
        XCTAssertEqual(sqlite3_prepare_v2(db, "PRAGMA user_version", -1, &version, nil), SQLITE_OK)
        defer { sqlite3_finalize(version) }
        XCTAssertEqual(sqlite3_step(version), SQLITE_ROW)
        XCTAssertEqual(sqlite3_column_int(version, 0), 1)
    }
    func testFutureSchemaRejectedWithoutDowngrade() throws {
        let (_, url) = try fixture()
        try execute(url, "PRAGMA user_version=99")
        XCTAssertThrowsError(try RemyHistoryRepository(url: url)) { XCTAssertEqual($0 as? RemyHistoryFailure, .unsupportedSchema(99)) }
        var db: OpaquePointer?; sqlite3_open(url.path, &db); defer { sqlite3_close(db) }
        var stmt: OpaquePointer?; sqlite3_prepare_v2(db,"PRAGMA user_version",-1,&stmt,nil); defer { sqlite3_finalize(stmt) }
        XCTAssertEqual(sqlite3_step(stmt), SQLITE_ROW)
        XCTAssertEqual(sqlite3_column_int(stmt,0),99)
    }
    func testVerifiedLegacyImportPreservesIDsDatesUnknownProvenanceAndIsIdempotent() async throws {
        let (_, url) = try fixture()
        let original = TranscriptHistoryEntry(id: UUID(), text: "旧数据 🎙️", createdAt: Date(timeIntervalSince1970: 5678.125), rawText: "原始")
        let bytes = try JSONEncoder().encode([original])
        let repository = try RemyHistoryRepository(url: url)
        let first = await repository.importLegacy(bytes), repeated = await repository.importLegacy(bytes)
        XCTAssertEqual(first,.saved); XCTAssertEqual(repeated,.saved)
        let rows = try await repository.page()
        XCTAssertEqual(rows.count,1); XCTAssertEqual(rows.first?.id,original.id)
        XCTAssertEqual(rows.first?.createdAt,original.createdAt); XCTAssertEqual(rows.first?.rawText,original.rawText)
        XCTAssertNil(rows.first?.engine); XCTAssertNil(rows.first?.settingsVersion)
    }
    func testLegacyConflictRollsBackEarlierEntriesAndMalformedInputDoesNotWrite() async throws {
        let (_, url) = try fixture()
        let repository = try RemyHistoryRepository(url: url)
        let existing = session()
        _ = await repository.commitRaw(existing, generation: 0)
        let fresh = TranscriptHistoryEntry(id: UUID(), text: "must rollback", createdAt: Date(), rawText: nil)
        let conflict = TranscriptHistoryEntry(id: existing.id, text: "conflict", createdAt: existing.createdAt, rawText: nil)
        let result = await repository.importLegacy(try JSONEncoder().encode([fresh,conflict]))
        XCTAssertEqual(result,.notSaved(.conflictingLegacyID))
        let malformed = await repository.importLegacy(Data("invalid".utf8))
        XCTAssertEqual(malformed,.notSaved(.malformedLegacy))
        let rows = try await repository.page()
        XCTAssertEqual(rows,[existing])
    }
    func testDeletionCascadesAndLateWritesOrReimportCannotResurrect() async throws {
        let (_, url) = try fixture()
        let repository = try RemyHistoryRepository(url: url)
        let value = session()
        _ = await repository.commitRaw(value, generation: 0)
        _ = await repository.commitFinal(.init(id: UUID(), sessionID: value.id, createdAt: value.createdAt, input: "before", output: "before", provider: nil, model: nil, instructionVersion: nil, failureCategory: nil), selectOutput: false)
        _ = await repository.recordDelivery(.init(id: UUID(), sessionID: value.id, createdAt: value.createdAt, payload: "before", outcome: .intent))
        let deleted = await repository.delete(sessionID: value.id)
        XCTAssertEqual(deleted,.saved)
        let late = await repository.commitFinal(.init(id: UUID(), sessionID: value.id, createdAt: Date(), input: "late", output: "late", provider: nil, model: nil, instructionVersion: nil, failureCategory: nil), selectOutput: true)
        XCTAssertEqual(late,.notSaved(.deleted))
        let recreated = await repository.commitRaw(value, generation: 0)
        XCTAssertEqual(recreated,.notSaved(.deleted))
        let legacy = TranscriptHistoryEntry(id: value.id, text: value.selectedText, createdAt: value.createdAt, rawText: value.rawText)
        let imported = await repository.importLegacy(try JSONEncoder().encode([legacy]))
        XCTAssertEqual(imported,.saved)
        let rows = try await repository.page(search: "late")
        XCTAssertTrue(rows.isEmpty)
        let all = try await repository.page()
        XCTAssertTrue(all.isEmpty)
        let attempts = try await repository.attempts(sessionID: value.id)
        let deliveries = try await repository.deliveries(sessionID: value.id)
        XCTAssertTrue(attempts.isEmpty); XCTAssertTrue(deliveries.isEmpty)
    }
    func testClearAllRevokesPendingUnsavedSessionGeneration() async throws {
        let (_, url) = try fixture()
        let repository = try RemyHistoryRepository(url: url)
        let oldGeneration = try await repository.generation()
        let clear = await repository.clearAll()
        XCTAssertEqual(clear, .saved)
        let late = await repository.commitRaw(session(), generation: oldGeneration)
        XCTAssertEqual(late, .notSaved(.deleted))
        let freshGeneration = try await repository.generation()
        let fresh = await repository.commitRaw(session(), generation: freshGeneration)
        XCTAssertEqual(fresh, .saved)
    }
    func testTenThousandEntryLiteralUnicodeSearchAndStablePagination() async throws {
        let (_, url) = try fixture()
        let entries = (0..<10_000).map { TranscriptHistoryEntry(id: UUID(), text: $0 % 2 == 0 ? "你好_% \($0)" : "other \($0)", createdAt: Date(timeIntervalSince1970: Double($0 / 3)), rawText: nil) }
        let repository = try RemyHistoryRepository(url: url)
        let imported = await repository.importLegacy(try JSONEncoder().encode(entries))
        XCTAssertEqual(imported,.saved)
        var ids = Set<UUID>(), cursor: RemyHistoryCursor?
        while true {
            let page = try await repository.page(before: cursor, limit: 333, search: "你好_%")
            if page.isEmpty { break }
            for row in page { XCTAssertTrue(ids.insert(row.id).inserted) }
            let last = try XCTUnwrap(page.last)
            cursor = .init(createdAt: last.createdAt, id: last.id)
        }
        XCTAssertEqual(ids.count,5_000)
    }
    func testInterruptedUncommittedTransactionRollsBackAndDatabaseRemainsHealthy() async throws {
        let (_, url) = try fixture()
        let repository = try RemyHistoryRepository(url: url)
        let value = session()
        _ = await repository.commitRaw(value, generation: 0)
        // Closing a separate connection with an open write transaction models an interrupted
        // transaction. No process is killed and no user's database is opened.
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open(url.path, &db), SQLITE_OK)
        XCTAssertEqual(sqlite3_exec(db, "BEGIN IMMEDIATE; UPDATE sessions SET selected='uncommitted';", nil,nil,nil), SQLITE_OK)
        XCTAssertEqual(sqlite3_close(db), SQLITE_OK)
        let reopened = try RemyHistoryRepository(url: url)
        let rows = try await reopened.page()
        XCTAssertEqual(rows.first?.selectedText, value.selectedText)
        XCTAssertEqual(rows.first?.status, .interrupted)
        var checkDB: OpaquePointer?; sqlite3_open(url.path,&checkDB)
        defer { sqlite3_close(checkDB) }
        var stmt: OpaquePointer?; sqlite3_prepare_v2(checkDB,"PRAGMA integrity_check",-1,&stmt,nil)
        defer { sqlite3_finalize(stmt) }
        XCTAssertEqual(sqlite3_step(stmt), SQLITE_ROW)
        XCTAssertEqual(sqlite3_column_text(stmt,0).map { String(cString:$0) }, "ok")
    }
    func testDeliveryLifecycleKeepsStableIDAndRejectsTerminalOverwrite() async throws {
        let (_, url) = try fixture()
        let repository = try RemyHistoryRepository(url:url)
        let value = session()
        _ = await repository.commitRaw(value,generation:0)
        let id = UUID()
        let intent = await repository.recordDelivery(.init(id:id,sessionID:value.id,createdAt:value.createdAt,payload:"exact delivered text",outcome:.intent))
        XCTAssertEqual(intent,.saved)
        let copied = await repository.finishDelivery(id:id,outcome:.copied)
        let dispatched = await repository.finishDelivery(id:id,outcome:.dispatched)
        let verified = await repository.finishDelivery(id:id,outcome:.verified)
        XCTAssertEqual(copied,.saved); XCTAssertEqual(dispatched,.saved); XCTAssertEqual(verified,.saved)
        let overwrite = await repository.finishDelivery(id:id,outcome:.failed)
        XCTAssertEqual(overwrite,.notSaved(.invalidTransition))
        let records = try await repository.deliveries(sessionID:value.id)
        XCTAssertEqual(records.count,1); XCTAssertEqual(records.first?.id,id)
        XCTAssertEqual(records.first?.outcome,.verified)
        XCTAssertEqual(records.first?.payload,"exact delivered text")
    }
    func testRealReadOnlyAndFullFailuresAreObservable() async throws {
        let (_, url) = try fixture()
        let writable = try RemyHistoryRepository(url:url)
        let original = session()
        _ = await writable.commitRaw(original,generation:0)
        let readOnly = try RemyHistoryRepository(url:url,readOnly:true)
        let denied = await readOnly.commitRaw(session(),generation:0)
        guard case .notSaved(.storage(let code)) = denied else { return XCTFail("Expected read-only failure") }
        XCTAssertEqual(code,SQLITE_READONLY)
        let limited = try RemyHistoryRepository(url:url,maximumPageCount:1)
        let full = await limited.commitRaw(session(String(repeating:"large synthetic data",count:100_000)),generation:0)
        guard case .notSaved(.storage(let fullCode)) = full else { return XCTFail("Expected disk-full failure") }
        XCTAssertEqual(fullCode,SQLITE_FULL)
        let rows = try await limited.page()
        XCTAssertEqual(rows.count,1); XCTAssertEqual(rows.first?.id,original.id)
    }
    func testAttemptInputDistinctFromRawAndInvalidSelectionRollsBack() async throws {
        let (_, url) = try fixture()
        let repository = try RemyHistoryRepository(url:url)
        let original = session()
        _ = await repository.commitRaw(original,generation:0)
        let failed = RemyHistoryAttempt(id:UUID(),sessionID:original.id,createdAt:Date(),input:"filler-stripped input",output:nil,provider:nil,model:nil,instructionVersion:1,failureCategory:"transport")
        let invalid = await repository.commitFinal(failed,selectOutput:true)
        XCTAssertEqual(invalid,.notSaved(.invalidRecord))
        let empty = try await repository.attempts(sessionID:original.id)
        XCTAssertTrue(empty.isEmpty)
        let valid = await repository.commitFinal(failed,selectOutput:false)
        XCTAssertEqual(valid,.saved)
        let rows = try await repository.page()
        XCTAssertEqual(rows.first?.rawText,original.rawText)
        let attempts = try await repository.attempts(sessionID:original.id)
        XCTAssertEqual(attempts.first?.input,"filler-stripped input")
    }
    func testRealSQLiteWriteFailureIsObservableAndRollbackLeavesNoPartialSession() async throws {
        let (_, url) = try fixture()
        let repository = try RemyHistoryRepository(url: url)
        // Enforce a real SQLite constraint failure inside the transaction, rather than a fake acknowledgement.
        try execute(url, "CREATE TRIGGER reject_fixture BEFORE INSERT ON sessions BEGIN SELECT RAISE(ABORT,'synthetic'); END")
        let result = await repository.commitRaw(session(), generation: 0)
        guard case .notSaved(.storage) = result else { return XCTFail("Expected observable storage failure") }
        let rows = try await repository.page()
        XCTAssertTrue(rows.isEmpty)
    }
}
