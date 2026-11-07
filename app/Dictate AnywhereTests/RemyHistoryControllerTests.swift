import Foundation
import SQLite3
import XCTest
@testable import Dictate_Anywhere

@MainActor final class RemyHistoryControllerTests: XCTestCase {
    private func fixture() throws -> RemyHistoryRepository {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
        addTeardownBlock { try? FileManager.default.removeItem(at:directory) }
        return try RemyHistoryRepository(url:directory.appendingPathComponent("history.sqlite"))
    }
    private var config: RemyHistoryConfiguration {
        .init(engine:"synthetic",model:"fixture",language:"zh",settingsVersion:1,instructionVersion:1,
              provider:"synthetic",cleanupModel:"fixture",prompt:"Synthetic instruction",
              profile:.init(profileID:nil,cleanupMode:"synthetic",reasoningEnabled:false,contextEnabled:false,style:nil,structure:nil))
    }
    private func begin(_ controller: RemyHistoryController, enabled: Bool = true) async -> RemyHistorySessionContext {
        await controller.beginSession(id:UUID(),createdAt:Date(timeIntervalSince1970:1234),enabled:enabled,configuration:config)
    }
    func testRawAttemptAndDeliveryRoundTripThroughBridge() async throws {
        let repository = try fixture()
        // Use the same injected repository for assertions; no Settings or output adapter.
        let bridge = RemyHistoryController(repository:repository)
        let prepared = await bridge.prepare()
        XCTAssertEqual(prepared,.saved)
        let context = await begin(bridge)
        let raw = await bridge.recordRaw(context,rawChunk:"新识别 🦾",continuationPrefix:"此前内容 ")
        XCTAssertEqual(raw,.saved)
        let recognized = try await repository.page()
        XCTAssertEqual(recognized.first?.rawText,"新识别 🦾")
        XCTAssertEqual(recognized.first?.selectedText,"此前内容 新识别 🦾")
        let final = await bridge.recordAttempt(context,input:"filler stripped input",output:"最终文本",status:.succeeded,selectOutput:true)
        XCTAssertEqual(final,.saved)
        let id = UUID()
        let intent = await bridge.recordDeliveryIntent(context,id:id,payload:"最终文本")
        XCTAssertEqual(intent,.saved)
        let outcome = await bridge.recordDeliveryResult(context,id:id,outcome:.copied)
        XCTAssertEqual(outcome,.saved)
        let rows = try await repository.page()
        XCTAssertEqual(rows.first?.rawText,"新识别 🦾")
        XCTAssertEqual(rows.first?.rawChunk,"新识别 🦾")
        XCTAssertEqual(rows.first?.continuationPrefix,"此前内容 ")
        XCTAssertEqual(rows.first?.selectedText,"最终文本")
        XCTAssertEqual(rows.first?.selectionKind,.attempt)
        let attempts = try await repository.attempts(sessionID:context.id)
        XCTAssertEqual(attempts.first?.input,"filler stripped input")
        XCTAssertEqual(attempts.first?.profile,config.profile)
        let deliveries = try await repository.deliveries(sessionID:context.id)
        XCTAssertEqual(deliveries.first?.outcome,.copied)
        XCTAssertNil(bridge.notSaved)
    }
    func testFailedCleanupPreservesNilOutputAndExplicitFallbackSelection() async throws {
        let repository = try fixture()
        let controller = RemyHistoryController(repository:repository)
        let context = await begin(controller)
        _ = await controller.recordRaw(context,rawChunk:"um raw")
        let failed = await controller.recordAttempt(context,input:"raw",output:nil,status:.failed,failureCategory:"transport",selectOutput:false)
        XCTAssertEqual(failed,.saved)
        let fallback = await controller.selectFallback(context,text:"raw")
        XCTAssertEqual(fallback,.saved)
        let rows = try await repository.page()
        let attempts = try await repository.attempts(sessionID:context.id)
        XCTAssertEqual(rows.first?.rawText,"um raw")
        XCTAssertEqual(rows.first?.selectedText,"raw")
        XCTAssertEqual(rows.first?.selectionKind,.fallback)
        XCTAssertNil(rows.first?.selectedAttemptID)
        XCTAssertEqual(attempts.first?.status,.failed)
        XCTAssertNil(attempts.first?.output)
    }
    func testDetailsExposeTimingAndCancellationAndRespectDeletion() async throws {
        let repository = try fixture()
        let controller = RemyHistoryController(repository: repository)
        let context = await begin(controller)
        _ = await controller.recordRaw(context, rawChunk: "raw")
        _ = await controller.recordAttempt(context, input: "raw", output: nil,
            status: .cancelled, failureCategory: "cancelled", selectOutput: false,
            startedAt: context.createdAt, durationMilliseconds: 25)
        _ = await controller.interruptSession(context)
        let attempts = try await controller.attempts(sessionID: context.id)
        XCTAssertEqual(attempts.first?.durationMilliseconds, 25)
        XCTAssertEqual(attempts.first?.startedAt, context.createdAt)
        XCTAssertEqual(attempts.first?.status, .cancelled)
        XCTAssertEqual(controller.entries.first?.status, .interrupted)
        let stored = try await repository.page()
        XCTAssertEqual(stored.first?.status, .interrupted)
        let deliveries = try await controller.deliveries(sessionID: context.id)
        XCTAssertTrue(deliveries.isEmpty)
        _ = await controller.delete(id: context.id)
        do { _ = try await controller.attempts(sessionID: context.id); XCTFail("Deleted details must stay hidden") }
        catch { XCTAssertEqual(error as? RemyHistoryFailure, .deleted) }
        do { _ = try await controller.deliveries(sessionID: context.id); XCTFail("Deleted details must stay hidden") }
        catch { XCTAssertEqual(error as? RemyHistoryFailure, .deleted) }
    }
    func testOpenFailureRetainsMemoryAndIsNeverHistoryOff() async {
        let controller = RemyHistoryController(repository:nil,openingFailure:.storage(code:13))
        let prepared = await controller.prepare()
        XCTAssertEqual(prepared,.notSaved(.storage(code:13)))
        let context = await begin(controller)
        let raw = await controller.recordRaw(context,rawChunk:"recoverable synthetic text")
        XCTAssertEqual(raw,.notSaved(.storage(code:13)))
        let final = await controller.recordAttempt(context,input:"recoverable",output:"clean recoverable",status:.succeeded,selectOutput:true)
        XCTAssertEqual(final,.notSaved(.storage(code:13)))
        XCTAssertEqual(controller.entries.first?.rawText,"recoverable synthetic text")
        XCTAssertEqual(controller.entries.first?.selectedText,"clean recoverable")
        XCTAssertEqual(controller.unsavedAttempts.count,1)
        let delivery = await controller.recordDeliveryIntent(context,id:UUID(),payload:"clean recoverable")
        XCTAssertEqual(delivery,.notSaved(.storage(code:13)))
        XCTAssertEqual(controller.notSaved,.storage(code:13))
    }
    func testIntentionalOffDoesNotCreateEntriesOrHideSeparateOpeningFailure() async {
        let controller = RemyHistoryController(repository:nil,openingFailure:.storage(code:13))
        let context = await begin(controller,enabled:false)
        let raw = await controller.recordRaw(context,rawChunk:"off text")
        let attempt = await controller.recordAttempt(context,input:"off",output:"off",status:.succeeded,selectOutput:true)
        let intent = await controller.recordDeliveryIntent(context,id:UUID(),payload:"off")
        XCTAssertEqual(raw,.historyOff); XCTAssertEqual(attempt,.historyOff); XCTAssertEqual(intent,.historyOff)
        XCTAssertTrue(controller.entries.isEmpty); XCTAssertTrue(controller.unsavedAttempts.isEmpty)
        XCTAssertEqual(controller.notSaved,.storage(code:13))
    }
    func testDeleteAndClearRejectOldSessionCallbacksAndAllowFreshSession() async throws {
        let repository = try fixture()
        let controller = RemyHistoryController(repository:repository)
        let first = await begin(controller)
        _ = await controller.recordRaw(first,rawChunk:"first")
        let removed = await controller.delete(id:first.id)
        XCTAssertEqual(removed,.saved)
        let late = await controller.selectFallback(first,text:"late")
        XCTAssertEqual(late,.notSaved(.deleted))
        let pending = await begin(controller)
        let cleared = await controller.clear()
        XCTAssertEqual(cleared,.saved)
        let resurrect = await controller.recordRaw(pending,rawChunk:"must not return")
        XCTAssertEqual(resurrect,.notSaved(.deleted))
        XCTAssertTrue(controller.entries.isEmpty)
        let fresh = await begin(controller)
        let saved = await controller.recordRaw(fresh,rawChunk:"fresh")
        XCTAssertEqual(saved,.saved)
        XCTAssertEqual(controller.entries.map(\.id),[fresh.id])
    }
    func testDeliveryRequiresAcknowledgedIntentAndSameSession() async throws {
        let controller = RemyHistoryController(repository:try fixture())
        let first = await begin(controller), second = await begin(controller)
        _ = await controller.recordRaw(first,rawChunk:"first")
        _ = await controller.recordRaw(second,rawChunk:"second")
        let id = UUID()
        let beforeIntent = await controller.recordDeliveryResult(first,id:id,outcome:.copied)
        XCTAssertEqual(beforeIntent,.notSaved(.invalidTransition))
        _ = await controller.recordDeliveryIntent(first,id:id,payload:"first")
        let wrongOwner = await controller.recordDeliveryResult(second,id:id,outcome:.copied)
        XCTAssertEqual(wrongOwner,.notSaved(.invalidTransition))
        let rightOwner = await controller.recordDeliveryResult(first,id:id,outcome:.copied)
        XCTAssertEqual(rightOwner,.saved)
    }
    func testPrepareImportsSuppliedBytesAndRefreshMergesUnsavedMemory() async throws {
        let controller = RemyHistoryController(repository:try fixture())
        let original = TranscriptHistoryEntry(id:UUID(),text:"legacy",createdAt:Date(timeIntervalSince1970:1),rawText:nil)
        let prepared = await controller.prepare(importLegacyData:try JSONEncoder().encode([original]))
        XCTAssertEqual(prepared,.saved)
        XCTAssertEqual(controller.entries.first?.id,original.id)
        let context = await begin(controller)
        _ = await controller.recordRaw(context,rawChunk:"new")
        let refreshed = await controller.refresh()
        XCTAssertEqual(refreshed,.saved)
        XCTAssertEqual(Set(controller.entries.map(\.id)),Set([original.id,context.id]))
    }
}

@MainActor private final class HistoryProjectionGate {
    private var continuation: CheckedContinuation<Void, Never>?
    private var released = false
    var fetched = false
    func wait() async {
        fetched = true
        if released { return }
        await withCheckedContinuation { continuation = $0 }
    }
    func release() { released = true; continuation?.resume(); continuation = nil }
}

extension RemyHistoryControllerTests {
    func testSuspendedRefreshCannotResurrectDeletedSQLiteRow() async throws {
        let repository = try fixture()
        let value = RemyHistorySession(id:UUID(),createdAt:Date(),rawText:"raw",selectedText:"selected")
        _ = await repository.commitRaw(value,generation:0)
        let gate = HistoryProjectionGate()
        defer { gate.release() }
        let controller = RemyHistoryController(repository:repository,projectionLoader: {
            let rows = try await repository.page()
            await gate.wait()
            return rows
        })
        let refresh = Task { await controller.refresh() }
        let deadline = Date().addingTimeInterval(2)
        while !gate.fetched && Date() < deadline { await Task.yield() }
        XCTAssertTrue(gate.fetched)
        let deleted = await controller.delete(id:value.id)
        XCTAssertEqual(deleted,.saved)
        gate.release()
        _ = await refresh.value
        XCTAssertTrue(controller.entries.isEmpty)
        let rows = try await repository.page()
        XCTAssertTrue(rows.isEmpty)
    }
    func testEnabledContextRejectsUnexpectedRepositoryHistoryOff() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at:directory) }
        let repository = try RemyHistoryRepository(url:directory.appendingPathComponent("never-created.sqlite"),historyEnabled:false)
        let controller = RemyHistoryController(repository:repository)
        let context = await begin(controller)
        let result = await controller.recordRaw(context,rawChunk:"retain me")
        XCTAssertEqual(result,.notSaved(.invalidTransition))
        XCTAssertEqual(controller.entries.first?.rawText,"retain me")
        XCTAssertEqual(controller.notSaved,.invalidTransition)
        XCTAssertFalse(FileManager.default.fileExists(atPath:directory.path))
        let off = await begin(controller,enabled:false)
        let intentional = await controller.recordRaw(off,rawChunk:"do not retain")
        XCTAssertEqual(intentional,.historyOff)
        XCTAssertEqual(controller.entries.count,1)
    }
    func testUnavailableRepositoryRetainsSuppliedLegacyBytesInMemory() async throws {
        let controller = RemyHistoryController(repository:nil,openingFailure:.storage(code:13))
        let original = TranscriptHistoryEntry(id:UUID(),text:"旧文本 🦾",createdAt:Date(timeIntervalSince1970:111),rawText:"原始")
        let result = await controller.prepare(importLegacyData:try JSONEncoder().encode([original]))
        XCTAssertEqual(result,.notSaved(.storage(code:13)))
        XCTAssertEqual(controller.entries.first?.id,original.id)
        XCTAssertEqual(controller.entries.first?.selectedText,original.text)
        XCTAssertEqual(controller.entries.first?.rawText,original.rawText)
        XCTAssertEqual(controller.entries.first?.createdAt,original.createdAt)
        XCTAssertEqual(controller.entries.first?.selectionKind,.legacy)
    }
    func testCompleteRawSelectionDoesNotFabricateAttemptOrFallback() async throws {
        let repository = try fixture()
        let controller = RemyHistoryController(repository:repository)
        let context = await begin(controller)
        XCTAssertTrue(controller.isValid(context))
        _ = await controller.recordRaw(context,rawChunk:"um raw words")
        let result = await controller.completeRawSelection(context,text:"raw words")
        XCTAssertEqual(result,.saved)
        let rows = try await repository.page()
        XCTAssertEqual(rows.first?.rawText,"um raw words")
        XCTAssertEqual(rows.first?.selectedText,"raw words")
        XCTAssertEqual(rows.first?.selectionKind,.raw)
        XCTAssertEqual(rows.first?.status,.completed)
        XCTAssertNil(rows.first?.selectedAttemptID)
        let attempts = try await repository.attempts(sessionID:context.id)
        XCTAssertTrue(attempts.isEmpty)
        _ = await controller.delete(id:context.id)
        XCTAssertFalse(controller.isValid(context))
        let late = await controller.completeRawSelection(context,text:"late")
        XCTAssertEqual(late,.notSaved(.deleted))
        let pending = await begin(controller)
        _ = await controller.clear()
        XCTAssertFalse(controller.isValid(pending))
    }
    func testRecoveredSelectionPreservesUnknownRawAndDoesNotFabricateAttempt() async throws {
        let repository = try fixture()
        let controller = RemyHistoryController(repository:repository)
        let context = await begin(controller)
        let result = await controller.recordRecovered(context,text:"combined completed recovery")
        XCTAssertEqual(result,.saved)
        let rows = try await repository.page()
        XCTAssertEqual(rows.first?.selectedText,"combined completed recovery")
        XCTAssertEqual(rows.first?.selectionKind,.recovered)
        XCTAssertEqual(rows.first?.status,.completed)
        XCTAssertNil(rows.first?.rawText)
        XCTAssertNil(rows.first?.rawChunk)
        XCTAssertNil(rows.first?.continuationPrefix)
        XCTAssertNil(rows.first?.selectedAttemptID)
        let attempts = try await repository.attempts(sessionID:context.id)
        XCTAssertTrue(attempts.isEmpty)
        _ = await controller.delete(id:context.id)
        let late = await controller.recordRecovered(context,text:"late")
        XCTAssertEqual(late,.notSaved(.deleted))
    }
    func testRecoveredSelectionRetainsExplicitKnownRawAndFailedSaveMemory() async throws {
        let repository = try fixture()
        let controller = RemyHistoryController(repository:repository)
        let context = await begin(controller)
        let saved = await controller.recordRecovered(context,text:"completed",rawText:"known raw")
        XCTAssertEqual(saved,.saved)
        let rows = try await repository.page()
        XCTAssertEqual(rows.first?.rawText,"known raw")
        let failed = RemyHistoryController(repository:nil,openingFailure:.storage(code:13))
        let missing = await begin(failed)
        let notSaved = await failed.recordRecovered(missing,text:"memory only")
        XCTAssertEqual(notSaved,.notSaved(.storage(code:13)))
        XCTAssertEqual(failed.entries.first?.selectedText,"memory only")
        XCTAssertNil(failed.entries.first?.rawText)
    }
    func testUnavailableRepositoryDeleteRevokesAndRemovesUnsavedPrivateText() async throws {
        let controller = RemyHistoryController(repository:nil,openingFailure:.storage(code:13))
        let context = await begin(controller)
        _ = await controller.recordRaw(context,rawChunk:"private unsaved raw")
        _ = await controller.recordAttempt(context,input:"private input",output:"private result",status:.succeeded,selectOutput:true)
        XCTAssertFalse(controller.unsavedAttempts.isEmpty)
        let removed = await controller.delete(id:context.id)
        XCTAssertEqual(removed,.notSaved(.storage(code:13)))
        XCTAssertTrue(controller.entries.isEmpty); XCTAssertTrue(controller.unsavedAttempts.isEmpty)
        XCTAssertFalse(controller.isValid(context))
        XCTAssertEqual(controller.pendingDeletionIDs,[context.id])
        let late = await controller.recordRecovered(context,text:"must not return")
        XCTAssertEqual(late,.notSaved(.deleted))
        XCTAssertTrue(controller.entries.isEmpty)
        let original = TranscriptHistoryEntry(id:context.id,text:"legacy must not return",createdAt:context.createdAt,rawText:nil)
        _ = await controller.prepare(importLegacyData:try JSONEncoder().encode([original]))
        XCTAssertTrue(controller.entries.isEmpty)
    }
    func testUnavailableRepositoryClearRevokesPendingContextsAndPreventsLegacyReseeding() async throws {
        let controller = RemyHistoryController(repository:nil,openingFailure:.storage(code:13))
        let context = await begin(controller), pending = await begin(controller)
        _ = await controller.recordRaw(context,rawChunk:"private raw")
        _ = await controller.recordAttempt(context,input:"private",output:nil,status:.failed,selectOutput:false)
        let cleared = await controller.clear()
        XCTAssertEqual(cleared,.notSaved(.storage(code:13)))
        XCTAssertTrue(controller.pendingClear)
        XCTAssertTrue(controller.entries.isEmpty); XCTAssertTrue(controller.unsavedAttempts.isEmpty)
        XCTAssertFalse(controller.isValid(context)); XCTAssertFalse(controller.isValid(pending))
        let late = await controller.recordRaw(pending,rawChunk:"late result")
        XCTAssertEqual(late,.notSaved(.deleted))
        let otherLegacy = TranscriptHistoryEntry(id:UUID(),text:"old legacy",createdAt:Date(),rawText:nil)
        _ = await controller.prepare(importLegacyData:try JSONEncoder().encode([otherLegacy]))
        XCTAssertTrue(controller.entries.isEmpty)
        let fresh = await begin(controller)
        _ = await controller.recordRaw(fresh,rawChunk:"new explicit session retained")
        XCTAssertEqual(controller.entries.map(\.selectedText),["new explicit session retained"])
    }
    func testReadOnlyDeleteAndClearHideLocallyButReportUnconfirmedDiskDeletion() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
        defer { try? FileManager.default.removeItem(at:directory) }
        let url = directory.appendingPathComponent("history.sqlite")
        let writable = try RemyHistoryRepository(url:url)
        let value = RemyHistorySession(id:UUID(),createdAt:Date(),rawText:"persisted private raw",selectedText:"persisted private selected")
        _ = await writable.commitRaw(value,generation:0)
        let readOnly = try RemyHistoryRepository(url:url,readOnly:true)
        let controller = RemyHistoryController(repository:readOnly)
        _ = await controller.prepare()
        XCTAssertEqual(controller.entries.count,1)
        let context = await controller.beginSession(id:value.id,createdAt:value.createdAt,enabled:true,configuration:config)
        let removed = await controller.delete(id:value.id)
        guard case .notSaved(.storage) = removed else { return XCTFail("Must report failed durable deletion") }
        XCTAssertFalse(controller.isValid(context))
        XCTAssertTrue(controller.entries.isEmpty)
        XCTAssertEqual(controller.pendingDeletionIDs,[value.id])
        _ = await controller.refresh()
        XCTAssertTrue(controller.entries.isEmpty)
        let diskRows = try await readOnly.page()
        XCTAssertEqual(diskRows.count,1) // Honest restart caveat: disk bytes remain.
        let cleared = await controller.clear()
        guard case .notSaved(.storage) = cleared else { return XCTFail("Must report failed durable clear") }
        XCTAssertTrue(controller.pendingClear)
        _ = await controller.refresh()
        XCTAssertTrue(controller.entries.isEmpty)
        XCTAssertNotNil(controller.notSaved)
    }
    func testRetryFailedClearDeletesOnlyOldRowsAndKeepsNewSession() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
        defer { try? FileManager.default.removeItem(at:directory) }
        let url = directory.appendingPathComponent("history.sqlite")
        let repository = try RemyHistoryRepository(url:url)
        let controller = RemyHistoryController(repository:repository)
        let old = await begin(controller)
        _ = await controller.recordRaw(old,rawChunk:"old row")
        try executeFixtureSQL(url,"CREATE TRIGGER reject_delete BEFORE DELETE ON sessions BEGIN SELECT RAISE(ABORT,'synthetic'); END")
        let failed = await controller.clear()
        guard case .notSaved = failed else { return XCTFail("Expected failed deletion") }
        XCTAssertTrue(controller.pendingClear)
        let fresh = await begin(controller)
        _ = await controller.recordRaw(fresh,rawChunk:"new row must survive retry")
        try executeFixtureSQL(url,"DROP TRIGGER reject_delete")
        let retried = await controller.retryPendingDeletions()
        XCTAssertEqual(retried,.saved)
        XCTAssertFalse(controller.pendingClear); XCTAssertTrue(controller.pendingDeletionIDs.isEmpty)
        XCTAssertTrue(controller.isValid(fresh))
        XCTAssertFalse(controller.isValid(old))
        let rows = try await repository.page()
        XCTAssertEqual(rows.map(\.id),[fresh.id])
        _ = await controller.refresh()
        XCTAssertEqual(controller.entries.map(\.id),[fresh.id])
        let repeated = await controller.retryPendingDeletions()
        XCTAssertEqual(repeated,.saved)
        let afterRepeated = try await repository.page()
        XCTAssertEqual(afterRepeated.map(\.id),[fresh.id])
    }
    func testRetryFailedSingleDeletionKeepsOtherRowsAndNilRepositoryRemainsNotSaved() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
        defer { try? FileManager.default.removeItem(at:directory) }
        let url = directory.appendingPathComponent("history.sqlite")
        let repository = try RemyHistoryRepository(url:url)
        let controller = RemyHistoryController(repository:repository)
        let deleted = await begin(controller), kept = await begin(controller)
        _ = await controller.recordRaw(deleted,rawChunk:"delete")
        _ = await controller.recordRaw(kept,rawChunk:"keep")
        try executeFixtureSQL(url,"CREATE TRIGGER reject_delete BEFORE DELETE ON sessions BEGIN SELECT RAISE(ABORT,'synthetic'); END")
        _ = await controller.delete(id:deleted.id)
        let failedRetry = await controller.retryPendingDeletions()
        guard case .notSaved = failedRetry else { return XCTFail("Expected failed retry") }
        XCTAssertEqual(controller.pendingDeletionIDs,[deleted.id])
        try executeFixtureSQL(url,"DROP TRIGGER reject_delete")
        let saved = await controller.retryPendingDeletions()
        XCTAssertEqual(saved,.saved); XCTAssertTrue(controller.pendingDeletionIDs.isEmpty)
        XCTAssertTrue(controller.isValid(kept))
        let rows = try await repository.page()
        XCTAssertEqual(rows.map(\.id),[kept.id])
        let memory = RemyHistoryController(repository:nil,openingFailure:.storage(code:13))
        let context = await begin(memory)
        _ = await memory.recordRaw(context,rawChunk:"unsaved")
        _ = await memory.delete(id:context.id)
        let unavailable = await memory.retryPendingDeletions()
        XCTAssertEqual(unavailable,.notSaved(.storage(code:13)))
        XCTAssertEqual(memory.pendingDeletionIDs,[context.id]); XCTAssertTrue(memory.entries.isEmpty)
    }
    private func executeFixtureSQL(_ url: URL, _ sql: String) throws {
        var database: OpaquePointer?
        guard sqlite3_open(url.path,&database) == SQLITE_OK else { throw RemyHistoryFailure.storage(code:1) }
        defer { sqlite3_close(database) }
        guard sqlite3_exec(database,sql,nil,nil,nil) == SQLITE_OK else { throw RemyHistoryFailure.storage(code:1) }
    }
    func testContinuationJoinPreservesUntouchedChunkAndPrefixProvenance() async throws {
        let controller = RemyHistoryController(repository:try fixture())
        let context = await begin(controller)
        let result = await controller.recordRaw(context,rawChunk:"  newest\n",continuationPrefix:"  prior\n")
        XCTAssertEqual(result,.saved)
        XCTAssertEqual(controller.entries.first?.rawText,"  newest\n")
        XCTAssertEqual(controller.entries.first?.selectedText,"prior newest")
        XCTAssertEqual(controller.entries.first?.rawChunk,"  newest\n")
        XCTAssertEqual(controller.entries.first?.continuationPrefix,"  prior\n")
    }
}
