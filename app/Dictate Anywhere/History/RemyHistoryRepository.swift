import Foundation
import SQLite3
import CryptoKit

/// One actor owns one SQLite connection. No preferences, credential or audio access.
/// Successful writes have committed with synchronous=FULL; failures are observable.
actor RemyHistoryRepository {
    static let schemaVersion = 2
    private var database: OpaquePointer?
    private let enabled: Bool
    private let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

    init(url: URL, historyEnabled: Bool = true, readOnly: Bool = false, maximumPageCount: Int? = nil) throws {
        enabled = historyEnabled
        guard historyEnabled else { return } // Intentional opt-out creates no database.
        var handle: OpaquePointer?
        let result = sqlite3_open_v2(url.path, &handle, (readOnly ? SQLITE_OPEN_READONLY : SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE) | SQLITE_OPEN_FULLMUTEX, nil)
        guard result == SQLITE_OK, let handle else {
            if let handle { sqlite3_close(handle) }
            throw RemyHistoryFailure.storage(code: result)
        }
        database = handle
        do {
            let version = Int(sqlite3_user_data_version(handle))
            guard (0...Self.schemaVersion).contains(version) else { throw RemyHistoryFailure.unsupportedSchema(version) }
            if readOnly {
                guard version == Self.schemaVersion else { throw RemyHistoryFailure.unsupportedSchema(version) }
                try Self.execute(handle, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=2000;")
                return
            }
            // The caller owns/validates the parent directory. Keep transcript-bearing database
            // and SQLite's derived journal files private to the current user.
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            try Self.execute(handle, "PRAGMA foreign_keys=ON; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA busy_timeout=2000;")
            if version == 0 {
                try Self.execute(handle, """
                BEGIN IMMEDIATE;
                CREATE TABLE sessions(id TEXT PRIMARY KEY, created REAL NOT NULL, raw TEXT, selected TEXT NOT NULL,
                  engine TEXT, model TEXT, language TEXT, settings_version INTEGER, instruction_version INTEGER,
                  status TEXT NOT NULL, finished REAL, selected_attempt TEXT, raw_chunk TEXT, continuation_prefix TEXT, selection_kind TEXT NOT NULL, FOREIGN KEY(id,selected_attempt) REFERENCES attempts(session_id,id) DEFERRABLE INITIALLY DEFERRED);
                CREATE INDEX sessions_page ON sessions(created DESC,id DESC);
                CREATE TABLE tombstones(id TEXT PRIMARY KEY);
                CREATE TABLE repository_state(generation INTEGER NOT NULL);
                INSERT INTO repository_state VALUES(0);
                CREATE TABLE attempts(id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                  created REAL NOT NULL,input TEXT NOT NULL,output TEXT,provider TEXT,model TEXT,instruction_version INTEGER,failure TEXT,prompt TEXT,status TEXT NOT NULL,attempt_index INTEGER NOT NULL,profile BLOB, UNIQUE(session_id,attempt_index), UNIQUE(session_id,id));
                CREATE TABLE deliveries(id TEXT PRIMARY KEY,session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                  created REAL NOT NULL,payload TEXT NOT NULL,outcome TEXT NOT NULL);
                CREATE TABLE legacy_imports(source BLOB PRIMARY KEY, entry_count INTEGER NOT NULL);
                PRAGMA user_version=1;
                COMMIT;
                """)
            }
            if version < 2 {
                if version == 1 { try Self.backupBeforeMigration(handle, url: url) }
                try Self.execute(handle, """
                BEGIN IMMEDIATE;
                ALTER TABLE attempts ADD COLUMN started REAL;
                ALTER TABLE attempts ADD COLUMN duration_ms INTEGER;
                PRAGMA user_version=2;
                COMMIT;
                """)
            }
            if let maximumPageCount {
                guard maximumPageCount > 0 else { throw RemyHistoryFailure.invalidRecord }
                try Self.execute(handle, "PRAGMA max_page_count=\(maximumPageCount);")
            }
            // Recover metadata only. No clipboard/paste, provider or microphone operations.
            try Self.execute(handle, "BEGIN IMMEDIATE; UPDATE sessions SET status='interrupted',finished=COALESCE(finished,CAST(strftime('%s','now') AS REAL)) WHERE status='recognized'; UPDATE deliveries SET outcome='uncertain' WHERE outcome IN ('intent','copied','dispatched'); COMMIT;")
        } catch {
            sqlite3_close(handle); database = nil; throw error
        }
    }

    deinit { if let database { sqlite3_close(database) } }

    /// Capture this at session creation. clearAll advances it, preventing even a not-yet-saved
    /// recording from repopulating cleared history through a late recognition callback.
    func generation() throws -> Int64 {
        guard enabled else { return 0 }
        let stmt = try statement("SELECT generation FROM repository_state", [])
        defer { sqlite3_finalize(stmt) }
        guard sqlite3_step(stmt) == SQLITE_ROW else { throw failure() }
        return sqlite3_column_int64(stmt, 0)
    }

    func commitRaw(_ session: RemyHistorySession, generation expectedGeneration: Int64) -> RemyHistoryAcknowledgement {
        write {
            guard session.rawText != nil, session.status == .recognized,
                  session.finishedAt == nil, session.selectedAttemptID == nil else { throw RemyHistoryFailure.invalidRecord }
            guard try generation() == expectedGeneration else { throw RemyHistoryFailure.deleted }
            guard try !exists("SELECT 1 FROM tombstones WHERE id=?", session.id) else { throw RemyHistoryFailure.deleted }
            try insert(session)
        }
    }

    /// Recovery may contain only selected/completed text. Unknown recognition output stays NULL.
    func commitRecovered(_ session: RemyHistorySession, generation expectedGeneration: Int64) -> RemyHistoryAcknowledgement {
        write {
            guard session.status == .completed, session.selectionKind == .recovered,
                  session.finishedAt != nil, session.selectedAttemptID == nil else { throw RemyHistoryFailure.invalidRecord }
            guard try generation() == expectedGeneration else { throw RemyHistoryFailure.deleted }
            guard try !exists("SELECT 1 FROM tombstones WHERE id=?", session.id) else { throw RemyHistoryFailure.deleted }
            try insert(session)
        }
    }

    /// Attempts are append-only. Selection changes only after the attempt was durably inserted.
    func commitFinal(_ attempt: RemyHistoryAttempt, selectOutput: Bool) -> RemyHistoryAcknowledgement {
        write {
            try requireSession(attempt.sessionID)
            guard attempt.index > 0, (attempt.prompt?.utf8.count ?? 0) <= 65_536,
                  attempt.profile?.isBounded != false,
                  (attempt.provider?.utf8.count ?? 0) <= 256, (attempt.model?.utf8.count ?? 0) <= 256,
                  (attempt.failureCategory?.utf8.count ?? 0) <= 128,
                  (attempt.durationMilliseconds ?? 0) >= 0,
                  attempt.startedAt?.timeIntervalSince1970.isFinite != false,
                  (attempt.status == .succeeded) == (attempt.output != nil) else { throw RemyHistoryFailure.invalidRecord }
            let profile = try attempt.profile.map { try JSONEncoder().encode($0) }
            try run("INSERT INTO attempts(id,session_id,created,input,output,provider,model,instruction_version,failure,prompt,status,attempt_index,profile,started,duration_ms) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", [.text(attempt.id.uuidString),.text(attempt.sessionID.uuidString),.real(attempt.createdAt.timeIntervalSince1970),.text(attempt.input),.optional(attempt.output),.optional(attempt.provider),.optional(attempt.model),.integer(attempt.instructionVersion),.optional(attempt.failureCategory),.optional(attempt.prompt),.text(attempt.status.rawValue),.integer(attempt.index),profile.map(Value.blob) ?? .integer(nil),attempt.startedAt.map { .real($0.timeIntervalSince1970) } ?? .integer(nil),.integer(attempt.durationMilliseconds)])
            if selectOutput {
                guard attempt.status == .succeeded, let output = attempt.output else { throw RemyHistoryFailure.invalidRecord }
                try run("UPDATE sessions SET selected=?,selected_attempt=?,selection_kind='attempt',status='completed',finished=? WHERE id=?", [.text(output),.text(attempt.id.uuidString),.real(attempt.createdAt.timeIntervalSince1970),.text(attempt.sessionID.uuidString)])
            }
        }
    }

    /// Stable ID is the delivery group. Only an intent can create a row.
    func recordDelivery(_ delivery: RemyHistoryDelivery) -> RemyHistoryAcknowledgement {
        write {
            try requireSession(delivery.sessionID)
            guard delivery.outcome == .intent else { throw RemyHistoryFailure.invalidTransition }
            try run("INSERT INTO deliveries VALUES(?,?,?,?,?)", [.text(delivery.id.uuidString),.text(delivery.sessionID.uuidString),.real(delivery.createdAt.timeIntervalSince1970),.text(delivery.payload),.text(delivery.outcome.rawValue)])
        }
    }

    func finishDelivery(id: UUID, outcome: RemyHistoryDelivery.Outcome) -> RemyHistoryAcknowledgement {
        write {
            let stmt = try statement("SELECT outcome FROM deliveries WHERE id=?", [.text(id.uuidString)])
            defer { sqlite3_finalize(stmt) }
            guard sqlite3_step(stmt) == SQLITE_ROW, let current = columnText(stmt,0).flatMap(RemyHistoryDelivery.Outcome.init(rawValue:)) else { throw RemyHistoryFailure.invalidTransition }
            let allowed: Bool
            switch current {
            case .intent: allowed = outcome != .intent
            case .copied: allowed = [.dispatched,.verified,.uncertain,.failed].contains(outcome)
            case .dispatched: allowed = [.verified,.uncertain,.failed].contains(outcome)
            case .verified,.uncertain,.failed: allowed = false
            }
            guard allowed else { throw RemyHistoryFailure.invalidTransition }
            try run("UPDATE deliveries SET outcome=? WHERE id=?", [.text(outcome.rawValue),.text(id.uuidString)])
        }
    }

    /// Failed cleanup remains failed; fallback selection is a separate explicit event.
    func selectFallback(sessionID: UUID, text: String, at date: Date) -> RemyHistoryAcknowledgement {
        selectOutput(sessionID:sessionID,text:text,kind:.fallback,at:date)
    }

    func completeRawSelection(sessionID: UUID, text: String, at date: Date) -> RemyHistoryAcknowledgement {
        selectOutput(sessionID:sessionID,text:text,kind:.raw,at:date)
    }

    private func selectOutput(sessionID: UUID, text: String, kind: RemyHistorySession.SelectionKind,
                              at date: Date) -> RemyHistoryAcknowledgement {
        write {
            try requireSession(sessionID)
            try run("UPDATE sessions SET selected=?,selected_attempt=NULL,selection_kind=?,status='completed',finished=? WHERE id=?", [.text(text),.text(kind.rawValue),.real(date.timeIntervalSince1970),.text(sessionID.uuidString)])
        }
    }

    func finishSession(id: UUID, at date: Date, interrupted: Bool = false) -> RemyHistoryAcknowledgement {
        write {
            try requireSession(id)
            guard try rows("SELECT * FROM sessions WHERE id=?", [.text(id.uuidString)]).first?.status == .recognized else { throw RemyHistoryFailure.invalidTransition }
            try run("UPDATE sessions SET status=?,finished=? WHERE id=?", [.text(interrupted ? "interrupted" : "completed"),.real(date.timeIntervalSince1970),.text(id.uuidString)])
        }
    }

    func page(before: RemyHistoryCursor? = nil, limit: Int = 50, search: String? = nil) throws -> [RemyHistorySession] {
        guard enabled else { return [] }
        var clauses: [String] = [], bindings: [Value] = []
        if let before {
            clauses.append("(created < ? OR (created = ? AND id < ?))")
            bindings += [.real(before.createdAt.timeIntervalSince1970),.real(before.createdAt.timeIntervalSince1970),.text(before.id.uuidString)]
        }
        if let search, !search.isEmpty {
            // Literal Unicode substring search; no SQL/LIKE metacharacters or token-loss.
            clauses.append("(instr(selected,?)>0 OR instr(COALESCE(raw,''),?)>0)")
            bindings += [.text(search),.text(search)]
        }
        bindings.append(.integer(max(1, min(limit, 1000))))
        return try rows("SELECT * FROM sessions" + (clauses.isEmpty ? "" : " WHERE " + clauses.joined(separator: " AND ")) + " ORDER BY created DESC,id DESC LIMIT ?", bindings)
    }

    func attempts(sessionID: UUID) throws -> [RemyHistoryAttempt] {
        guard enabled else { return [] }
        let stmt = try statement("SELECT id,created,input,output,provider,model,instruction_version,failure,prompt,status,attempt_index,profile,started,duration_ms FROM attempts WHERE session_id=? ORDER BY attempt_index,created,id", [.text(sessionID.uuidString)])
        defer { sqlite3_finalize(stmt) }
        var results: [RemyHistoryAttempt] = []
        while true {
            let code = sqlite3_step(stmt)
            if code == SQLITE_DONE { return results }
            guard code == SQLITE_ROW, let id = columnText(stmt, 0).flatMap(UUID.init(uuidString:)), let input = columnText(stmt, 2) else { throw failure() }
            results.append(.init(id: id, sessionID: sessionID, createdAt: Date(timeIntervalSince1970: sqlite3_column_double(stmt,1)), input: input, output: columnText(stmt,3), provider: columnText(stmt,4), model: columnText(stmt,5), instructionVersion: sqlite3_column_type(stmt,6) == SQLITE_NULL ? nil : Int(sqlite3_column_int64(stmt,6)), failureCategory: columnText(stmt,7), prompt: columnText(stmt,8), status: columnText(stmt,9).flatMap(RemyHistoryAttempt.Status.init(rawValue:)), index: Int(sqlite3_column_int64(stmt,10)), profile: try columnData(stmt,11).map { try JSONDecoder().decode(RemyHistoryProfileSnapshot.self,from:$0) }, startedAt: sqlite3_column_type(stmt,12) == SQLITE_NULL ? nil : Date(timeIntervalSince1970: sqlite3_column_double(stmt,12)), durationMilliseconds: sqlite3_column_type(stmt,13) == SQLITE_NULL ? nil : Int(sqlite3_column_int64(stmt,13))))
        }
    }

    func deliveries(sessionID: UUID) throws -> [RemyHistoryDelivery] {
        guard enabled else { return [] }
        let stmt = try statement("SELECT id,created,payload,outcome FROM deliveries WHERE session_id=? ORDER BY created,id", [.text(sessionID.uuidString)])
        defer { sqlite3_finalize(stmt) }
        var results: [RemyHistoryDelivery] = []
        while true {
            let code = sqlite3_step(stmt)
            if code == SQLITE_DONE { return results }
            guard code == SQLITE_ROW, let id = columnText(stmt,0).flatMap(UUID.init(uuidString:)), let payload = columnText(stmt,2), let outcome = columnText(stmt,3).flatMap(RemyHistoryDelivery.Outcome.init(rawValue:)) else { throw failure() }
            results.append(.init(id:id,sessionID:sessionID,createdAt:Date(timeIntervalSince1970:sqlite3_column_double(stmt,1)),payload:payload,outcome:outcome))
        }
    }

    private func columnData(_ stmt: OpaquePointer, _ index: Int32) -> Data? {
        sqlite3_column_blob(stmt,index).map { Data(bytes:$0,count:Int(sqlite3_column_bytes(stmt,index))) }
    }

    private func columnText(_ stmt: OpaquePointer, _ index: Int32) -> String? {
        sqlite3_column_text(stmt,index).map { String(decoding: UnsafeBufferPointer(start:$0,count:Int(sqlite3_column_bytes(stmt,index))),as:UTF8.self) }
    }

    func delete(sessionID: UUID) -> RemyHistoryAcknowledgement {
        write {
            try run("INSERT OR IGNORE INTO tombstones VALUES(?)", [.text(sessionID.uuidString)])
            try run("DELETE FROM sessions WHERE id=?", [.text(sessionID.uuidString)])
        }
    }

    func clearAll() -> RemyHistoryAcknowledgement {
        write {
            try run("UPDATE repository_state SET generation=generation+1", [])
            try run("INSERT OR IGNORE INTO tombstones SELECT id FROM sessions", [])
            try run("DELETE FROM sessions", [])
        }
    }

    /// Caller preserves the supplied original bytes as its restricted backup. No preferences read.
    /// Original JSONEncoder Date semantics match TranscriptHistoryEntry's inherited encoding.
    func importLegacy(_ data: Data) -> RemyHistoryAcknowledgement {
        guard enabled else { return .historyOff }
        nonisolated struct Legacy: Decodable { let id: UUID; let text: String; let createdAt: Date; let rawText: String? }
        guard let entries = try? JSONDecoder().decode([Legacy].self, from: data),
              Set(entries.map(\.id)).count == entries.count else { return .notSaved(.malformedLegacy) }
        return write {
            let digest = Data(SHA256.hash(data: data))
            if try hasImport(digest) { return }
            for entry in entries {
                let session = RemyHistorySession(id: entry.id, createdAt: entry.createdAt, rawText: entry.rawText, selectedText: entry.text, status: .completed, finishedAt: entry.createdAt, selectionKind: .legacy)
                if try exists("SELECT 1 FROM tombstones WHERE id=?", entry.id) { continue }
                if let existing = try rows("SELECT * FROM sessions WHERE id=?", [.text(entry.id.uuidString)]).first {
                    guard existing == session else { throw RemyHistoryFailure.conflictingLegacyID }
                } else { try insert(session) }
            }
            // Verify all nondeleted imported values before recording the receipt/commit.
            for entry in entries where try !exists("SELECT 1 FROM tombstones WHERE id=?", entry.id) {
                guard try rows("SELECT * FROM sessions WHERE id=?", [.text(entry.id.uuidString)]).first == RemyHistorySession(id: entry.id, createdAt: entry.createdAt, rawText: entry.rawText, selectedText: entry.text, status: .completed, finishedAt: entry.createdAt, selectionKind: .legacy) else { throw RemyHistoryFailure.conflictingLegacyID }
            }
            try run("INSERT INTO legacy_imports VALUES(?,?)", [.blob(digest),.integer(entries.count)])
        }
    }

    private func requireSession(_ id: UUID) throws {
        if try exists("SELECT 1 FROM tombstones WHERE id=?", id) { throw RemyHistoryFailure.deleted }
        guard try exists("SELECT 1 FROM sessions WHERE id=?", id) else { throw RemyHistoryFailure.missingSession }
    }

    private func insert(_ value: RemyHistorySession) throws {
        try run("INSERT INTO sessions VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", [.text(value.id.uuidString),.real(value.createdAt.timeIntervalSince1970),.optional(value.rawText),.text(value.selectedText),.optional(value.engine),.optional(value.model),.optional(value.language),.integer(value.settingsVersion),.integer(value.instructionVersion),.text(value.status.rawValue),value.finishedAt.map { .real($0.timeIntervalSince1970) } ?? .integer(nil),value.selectedAttemptID.map { .text($0.uuidString) } ?? .integer(nil),.optional(value.rawChunk),.optional(value.continuationPrefix),.text(value.selectionKind.rawValue)])
    }

    private func write(_ operation: () throws -> Void) -> RemyHistoryAcknowledgement {
        guard enabled else { return .historyOff }
        do {
            try run("BEGIN IMMEDIATE", [])
            do { try operation(); try run("COMMIT", []) }
            catch { try? run("ROLLBACK", []); throw error }
            return .saved
        } catch let error as RemyHistoryFailure { return .notSaved(error) }
        catch { return .notSaved(.storage(code: SQLITE_ERROR)) }
    }

    private nonisolated enum Value { case text(String), real(Double), integer(Int?), blob(Data)
        static func optional(_ value: String?) -> Value { value.map(Value.text) ?? .integer(nil) }
    }
    private func statement(_ sql: String, _ values: [Value]) throws -> OpaquePointer {
        var result: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &result, nil) == SQLITE_OK, let result else { throw failure() }
        do {
            for (index, value) in values.enumerated() {
                let position = Int32(index + 1)
                let code: Int32
                switch value {
                case .text(let text):
                    guard text.utf8.count <= Int(Int32.max) else { throw RemyHistoryFailure.invalidRecord }
                    code = text.withCString { sqlite3_bind_text(result, position, $0, Int32(text.utf8.count), transient) }
                case .real(let number): code = sqlite3_bind_double(result, position, number)
                case .integer(let number): code = number.map { sqlite3_bind_int64(result, position, Int64($0)) } ?? sqlite3_bind_null(result, position)
                case .blob(let data):
                    guard data.count <= Int(Int32.max) else { throw RemyHistoryFailure.invalidRecord }
                    code = data.withUnsafeBytes { sqlite3_bind_blob(result, position, $0.baseAddress, Int32($0.count), transient) }
                }
                guard code == SQLITE_OK else { throw RemyHistoryFailure.storage(code: code) }
            }
            return result
        } catch { sqlite3_finalize(result); throw error }
    }
    private func run(_ sql: String, _ values: [Value]) throws {
        let stmt = try statement(sql, values); defer { sqlite3_finalize(stmt) }
        guard sqlite3_step(stmt) == SQLITE_DONE else { throw failure() }
    }
    private func exists(_ sql: String, _ id: UUID) throws -> Bool {
        let stmt = try statement(sql, [.text(id.uuidString)]); defer { sqlite3_finalize(stmt) }
        let code = sqlite3_step(stmt)
        guard code == SQLITE_ROW || code == SQLITE_DONE else { throw failure() }
        return code == SQLITE_ROW
    }
    private func hasImport(_ data: Data) throws -> Bool {
        let stmt = try statement("SELECT 1 FROM legacy_imports WHERE source=?", [.blob(data)]); defer { sqlite3_finalize(stmt) }
        let code = sqlite3_step(stmt)
        guard code == SQLITE_ROW || code == SQLITE_DONE else { throw failure() }
        return code == SQLITE_ROW
    }
    private func rows(_ sql: String, _ values: [Value]) throws -> [RemyHistorySession] {
        let stmt = try statement(sql, values); defer { sqlite3_finalize(stmt) }
        var output: [RemyHistorySession] = []
        func text(_ index: Int32) -> String? { sqlite3_column_text(stmt,index).map { String(decoding: UnsafeBufferPointer(start: $0, count: Int(sqlite3_column_bytes(stmt,index))), as: UTF8.self) } }
        func integer(_ index: Int32) -> Int? { sqlite3_column_type(stmt,index) == SQLITE_NULL ? nil : Int(sqlite3_column_int64(stmt,index)) }
        while true {
            let code = sqlite3_step(stmt)
            if code == SQLITE_DONE { return output }
            guard code == SQLITE_ROW, let idString = text(0), let id = UUID(uuidString: idString), let selected = text(3) else { throw failure() }
            output.append(.init(id:id,createdAt:Date(timeIntervalSince1970:sqlite3_column_double(stmt,1)),rawText:text(2),selectedText:selected,engine:text(4),model:text(5),language:text(6),settingsVersion:integer(7),instructionVersion:integer(8),status:text(9).flatMap(RemyHistorySession.Status.init(rawValue:)) ?? .interrupted,finishedAt:sqlite3_column_type(stmt,10) == SQLITE_NULL ? nil : Date(timeIntervalSince1970:sqlite3_column_double(stmt,10)),selectedAttemptID:text(11).flatMap(UUID.init(uuidString:)),rawChunk:text(12),continuationPrefix:text(13),selectionKind:text(14).flatMap(RemyHistorySession.SelectionKind.init(rawValue:)) ?? .raw))
        }
    }
    private func failure() -> RemyHistoryFailure { .storage(code: sqlite3_errcode(database)) }
    /// Preserve an untouched schema-1 snapshot for an explicit rollback. The
    /// database is never downgraded automatically by an older application.
    private static func backupBeforeMigration(_ source: OpaquePointer, url: URL) throws {
        let backupURL = url.deletingLastPathComponent()
            .appendingPathComponent("history-before-v2-\(UUID().uuidString).sqlite")
        guard FileManager.default.createFile(atPath: backupURL.path, contents: nil,
                                            attributes: [.posixPermissions: 0o600]) else {
            throw RemyHistoryFailure.storage(code: SQLITE_CANTOPEN)
        }
        var destination: OpaquePointer?
        var completed = false
        defer {
            if let destination { sqlite3_close(destination) }
            if !completed { try? FileManager.default.removeItem(at: backupURL) }
        }
        let opened = sqlite3_open_v2(backupURL.path, &destination, SQLITE_OPEN_READWRITE, nil)
        guard opened == SQLITE_OK, let destination else { throw RemyHistoryFailure.storage(code: opened) }
        guard let backup = sqlite3_backup_init(destination, "main", source, "main") else {
            throw RemyHistoryFailure.storage(code: sqlite3_errcode(destination))
        }
        let copied = sqlite3_backup_step(backup, -1)
        let finished = sqlite3_backup_finish(backup)
        guard copied == SQLITE_DONE, finished == SQLITE_OK else {
            throw RemyHistoryFailure.storage(code: copied == SQLITE_DONE ? finished : copied)
        }
        completed = true
    }
    private static func execute(_ db: OpaquePointer?, _ sql: String) throws {
        let code = sqlite3_exec(db, sql, nil, nil, nil)
        guard code == SQLITE_OK else { throw RemyHistoryFailure.storage(code: code) }
    }
}

private nonisolated func sqlite3_user_data_version(_ database: OpaquePointer?) -> Int32 {
    var stmt: OpaquePointer?
    guard sqlite3_prepare_v2(database,"PRAGMA user_version",-1,&stmt,nil) == SQLITE_OK else { return Int32.max }
    defer { sqlite3_finalize(stmt) }
    guard sqlite3_step(stmt) == SQLITE_ROW else { return Int32.max }
    return sqlite3_column_int(stmt,0)
}
