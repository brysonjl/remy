import Foundation
import Observation

/// Closed, credential-free metadata captured before cleanup. Never contains endpoint/key references.
nonisolated struct RemyHistoryConfiguration: Sendable {
    let engine: String
    let model: String
    let language: String
    let settingsVersion: Int
    let instructionVersion: Int
    let provider: String
    let cleanupModel: String
    let prompt: String
    let profile: RemyHistoryProfileSnapshot
}

nonisolated struct RemyHistorySessionContext: Sendable {
    let id: UUID
    let createdAt: Date
    let generation: Int64?
    let enabled: Bool
    let configuration: RemyHistoryConfiguration
    fileprivate let revision: UInt64
}

/// Memory projection plus explicit durability acknowledgement. Does not access preferences,
/// credentials, audio, output adapters or UI. A failed repository open is never history-off.
@MainActor @Observable final class RemyHistoryController {
    private(set) var entries: [RemyHistorySession] = []
    private(set) var notSaved: RemyHistoryFailure?
    private(set) var unsavedAttempts: [RemyHistoryAttempt] = []
    /// Local revocation is immediate; these flags mean disk deletion is not yet confirmed.
    /// They are process-local, not a durable retry queue. Failed deletions may reappear after restart.
    private(set) var pendingDeletionIDs: Set<UUID> = []
    private(set) var pendingClear = false
    @ObservationIgnored private let repository: RemyHistoryRepository?
    @ObservationIgnored private let openingFailure: RemyHistoryFailure?
    @ObservationIgnored private let projectionLoader: (() async throws -> [RemyHistorySession])?
    @ObservationIgnored private let sessionGenerationLoader: (() async throws -> Int64)?
    @ObservationIgnored private var memory: [UUID: RemyHistorySession] = [:]
    @ObservationIgnored private var savedRaw: Set<UUID> = []
    @ObservationIgnored private var attemptIndices: [UUID: Int] = [:]
    @ObservationIgnored private var deliveryIDs: [UUID: UUID] = [:]
    @ObservationIgnored private var deletedIDs: Set<UUID> = []
    @ObservationIgnored private var revision: UInt64 = 0
    @ObservationIgnored private var hasClearedLocally = false
    @ObservationIgnored private var postClearIDs: Set<UUID> = []

    init(repository: RemyHistoryRepository?, openingFailure: RemyHistoryFailure? = nil,
         projectionLoader: (() async throws -> [RemyHistorySession])? = nil,
         sessionGenerationLoader: (() async throws -> Int64)? = nil) {
        self.repository = repository
        self.projectionLoader = projectionLoader
        self.sessionGenerationLoader = sessionGenerationLoader
        self.openingFailure = openingFailure ?? (repository == nil ? .storage(code: 14) : nil)
        self.notSaved = self.openingFailure
    }

    @discardableResult func prepare(importLegacyData: Data? = nil) async -> RemyHistoryAcknowledgement {
        guard let repository else {
            if let importLegacyData { retainLegacyProjection(importLegacyData) }
            return observe(.notSaved(unavailable))
        }
        if let importLegacyData {
            let imported = expectedEnabled(await repository.importLegacy(importLegacyData))
            guard imported == .saved else {
                _ = await refresh()
                retainLegacyProjection(importLegacyData)
                return observe(imported)
            }
        }
        return await refresh()
    }

    func beginSession(id: UUID, createdAt: Date, enabled: Bool,
                      configuration: RemyHistoryConfiguration) async -> RemyHistorySessionContext {
        let capturedRevision = revision
        var generation: Int64?
        if enabled, let repository {
            do {
                if let sessionGenerationLoader { generation = try await sessionGenerationLoader() }
                else { generation = try await repository.generation() }
            }
            catch { _ = observe(.notSaved(classify(error))) }
        }
        return .init(id:id,createdAt:createdAt,generation:generation,enabled:enabled,configuration:configuration,revision:capturedRevision)
    }

    @discardableResult func recordRaw(_ context: RemyHistorySessionContext, rawChunk: String,
                                      continuationPrefix: String = "") async -> RemyHistoryAcknowledgement {
        guard context.enabled else { return .historyOff }
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        let selected = continuationPrefix.isEmpty ? rawChunk : CancelledDictation.joining(continuationPrefix, rawChunk)
        let config = context.configuration
        let value = RemyHistorySession(id:context.id,createdAt:context.createdAt,rawText:rawChunk,selectedText:selected,
            engine:config.engine,model:config.model,language:config.language,settingsVersion:config.settingsVersion,
            instructionVersion:config.instructionVersion,rawChunk:rawChunk,continuationPrefix:continuationPrefix)
        guard memory[context.id] == nil, !savedRaw.contains(context.id) else { return observe(.notSaved(.invalidTransition)) }
        if hasClearedLocally { postClearIDs.insert(context.id) }
        memory[context.id] = value; project()
        guard let repository, let generation = context.generation else { return observe(.notSaved(unavailable)) }
        let result = expectedEnabled(await repository.commitRaw(value,generation:generation))
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        if result == .saved { savedRaw.insert(context.id) }
        return observe(result)
    }

    /// Restore selected recovery text without claiming its original ASR output is known.
    @discardableResult func recordRecovered(_ context: RemyHistorySessionContext, text: String,
                                           rawText: String? = nil, at date: Date = Date()) async -> RemyHistoryAcknowledgement {
        guard context.enabled else { return .historyOff }
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        guard memory[context.id] == nil, !savedRaw.contains(context.id) else { return observe(.notSaved(.invalidTransition)) }
        let config = context.configuration
        let value = RemyHistorySession(id:context.id,createdAt:context.createdAt,rawText:rawText,selectedText:text,
            engine:config.engine,model:config.model,language:config.language,settingsVersion:config.settingsVersion,
            instructionVersion:config.instructionVersion,status:.completed,finishedAt:date,selectionKind:.recovered)
        if hasClearedLocally { postClearIDs.insert(context.id) }
        memory[context.id] = value; project()
        guard let repository, let generation = context.generation else { return observe(.notSaved(unavailable)) }
        let result = expectedEnabled(await repository.commitRecovered(value,generation:generation))
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        if result == .saved { savedRaw.insert(context.id) }
        return observe(result)
    }

    @discardableResult func recordAttempt(_ context: RemyHistorySessionContext, input: String, output: String?,
                                          status: RemyHistoryAttempt.Status, failureCategory: String? = nil,
                                          selectOutput: Bool, at date: Date = Date(),
                                          startedAt: Date? = nil, durationMilliseconds: Int? = nil) async -> RemyHistoryAcknowledgement {
        guard context.enabled else { return .historyOff }
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        let config = context.configuration
        let index = (attemptIndices[context.id] ?? 0) + 1
        attemptIndices[context.id] = index // Reserve synchronously across actor reentrancy.
        let attempt = RemyHistoryAttempt(id:UUID(),sessionID:context.id,createdAt:date,input:input,output:output,
            provider:config.provider,model:config.cleanupModel,instructionVersion:config.instructionVersion,
            failureCategory:failureCategory,prompt:config.prompt,status:status,index:index,profile:config.profile,
            startedAt:startedAt,durationMilliseconds:durationMilliseconds)
        if selectOutput, status == .succeeded, let output { selectMemory(context.id,text:output,kind:.attempt,attemptID:attempt.id,at:date) }
        unsavedAttempts.append(attempt)
        guard let repository, savedRaw.contains(context.id) else { return observe(.notSaved(unavailable)) }
        let result = expectedEnabled(await repository.commitFinal(attempt,selectOutput:selectOutput))
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        if result == .saved { unsavedAttempts.removeAll { $0.id == attempt.id } }
        return observe(result)
    }

    @discardableResult func selectFallback(_ context: RemyHistorySessionContext, text: String,
                                           at date: Date = Date()) async -> RemyHistoryAcknowledgement {
        guard context.enabled else { return .historyOff }
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        selectMemory(context.id,text:text,kind:.fallback,at:date)
        guard let repository, savedRaw.contains(context.id) else { return observe(.notSaved(unavailable)) }
        let result = expectedEnabled(await repository.selectFallback(sessionID:context.id,text:text,at:date))
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        return observe(result)
    }

    /// Deterministic filler/vocabulary-only processing is not a provider attempt or fallback.
    @discardableResult func completeRawSelection(_ context: RemyHistorySessionContext, text: String,
                                                at date: Date = Date()) async -> RemyHistoryAcknowledgement {
        guard context.enabled else { return .historyOff }
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        selectMemory(context.id,text:text,kind:.raw,at:date)
        guard let repository, savedRaw.contains(context.id) else { return observe(.notSaved(unavailable)) }
        let result = expectedEnabled(await repository.completeRawSelection(sessionID:context.id,text:text,at:date))
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        return observe(result)
    }

    /// Recheck after every suspension and immediately before dispatching an output effect.
    func isValid(_ context: RemyHistorySessionContext) -> Bool { valid(context) }

    /// The caller may dispatch output only after this acknowledgement (or intentional historyOff).
    func recordDeliveryIntent(_ context: RemyHistorySessionContext, id: UUID, payload: String,
                              at date: Date = Date()) async -> RemyHistoryAcknowledgement {
        guard context.enabled else { return .historyOff }
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        guard let repository, savedRaw.contains(context.id) else { return observe(.notSaved(unavailable)) }
        let result = expectedEnabled(await repository.recordDelivery(.init(id:id,sessionID:context.id,createdAt:date,payload:payload,outcome:.intent)))
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        if result == .saved { deliveryIDs[id] = context.id }
        return observe(result)
    }

    func recordDeliveryResult(_ context: RemyHistorySessionContext, id: UUID,
                              outcome: RemyHistoryDelivery.Outcome) async -> RemyHistoryAcknowledgement {
        guard context.enabled else { return .historyOff }
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        guard let repository, deliveryIDs[id] == context.id else { return observe(.notSaved(.invalidTransition)) }
        let result = expectedEnabled(await repository.finishDelivery(id:id,outcome:outcome))
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        return observe(result)
    }

    /// Detail reads respect immediate local deletion, including a failed disk deletion.
    func attempts(sessionID: UUID) async throws -> [RemyHistoryAttempt] {
        let capturedRevision = revision
        guard detailIsVisible(sessionID) else { throw RemyHistoryFailure.deleted }
        guard let repository else { throw unavailable }
        let saved = try await repository.attempts(sessionID: sessionID)
        guard capturedRevision == revision, detailIsVisible(sessionID) else { throw RemyHistoryFailure.deleted }
        let savedIDs = Set(saved.map(\.id))
        return (saved + unsavedAttempts.filter { $0.sessionID == sessionID && !savedIDs.contains($0.id) })
            .sorted { $0.index < $1.index }
    }

    func deliveries(sessionID: UUID) async throws -> [RemyHistoryDelivery] {
        let capturedRevision = revision
        guard detailIsVisible(sessionID) else { throw RemyHistoryFailure.deleted }
        guard let repository else { throw unavailable }
        let saved = try await repository.deliveries(sessionID: sessionID)
        guard capturedRevision == revision, detailIsVisible(sessionID) else { throw RemyHistoryFailure.deleted }
        return saved
    }

    private func detailIsVisible(_ id: UUID) -> Bool {
        !deletedIDs.contains(id) && (!pendingClear || postClearIDs.contains(id)) && entries.contains { $0.id == id }
    }

    @discardableResult func interruptSession(_ context: RemyHistorySessionContext,
                                             at date: Date = Date()) async -> RemyHistoryAcknowledgement {
        guard context.enabled else { return .historyOff }
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        guard let old = memory[context.id], old.status == .recognized else { return .saved }
        memory[context.id] = .init(id:old.id,createdAt:old.createdAt,rawText:old.rawText,selectedText:old.selectedText,
            engine:old.engine,model:old.model,language:old.language,settingsVersion:old.settingsVersion,
            instructionVersion:old.instructionVersion,status:.interrupted,finishedAt:date,
            rawChunk:old.rawChunk,continuationPrefix:old.continuationPrefix,selectionKind:old.selectionKind)
        project()
        guard let repository, savedRaw.contains(context.id) else { return observe(.notSaved(unavailable)) }
        let result = expectedEnabled(await repository.finishSession(id:context.id,at:date,interrupted:true))
        guard valid(context) else { return observe(.notSaved(.deleted)) }
        return observe(result)
    }

    @discardableResult func refresh() async -> RemyHistoryAcknowledgement {
        guard let repository else { return observe(.notSaved(unavailable)) }
        let capturedRevision = revision
        do {
            let rows: [RemyHistorySession]
            if let projectionLoader { rows = try await projectionLoader() }
            else { rows = try await repository.page(limit:1000) }
            guard revision == capturedRevision else { return .notSaved(.deleted) }
            var values = Dictionary(uniqueKeysWithValues:rows.filter { !deletedIDs.contains($0.id) && (!pendingClear || postClearIDs.contains($0.id)) }.map { ($0.id,$0) })
            for (id,value) in memory where !deletedIDs.contains(id) { values[id] = value }
            entries = values.values.sorted { $0.createdAt == $1.createdAt ? $0.id.uuidString > $1.id.uuidString : $0.createdAt > $1.createdAt }
            return .saved
        } catch { return observe(.notSaved(classify(error))) }
    }

    @discardableResult func delete(id: UUID) async -> RemyHistoryAcknowledgement {
        // Revoke before ANY await, even when the database is unavailable/read-only.
        pendingDeletionIDs.insert(id)
        deletedIDs.insert(id); memory.removeValue(forKey:id); savedRaw.remove(id)
        attemptIndices.removeValue(forKey:id); entries.removeAll { $0.id == id }
        unsavedAttempts.removeAll { $0.sessionID == id }
        deliveryIDs = deliveryIDs.filter { $0.value != id }
        postClearIDs.remove(id)
        guard let repository else { return observe(.notSaved(unavailable)) }
        let result = expectedEnabled(await repository.delete(sessionID:id))
        if result == .saved { pendingDeletionIDs.remove(id) }
        return observe(result)
    }

    @discardableResult func clear() async -> RemyHistoryAcknowledgement {
        revision &+= 1
        deletedIDs.formUnion(entries.map(\.id))
        deletedIDs.formUnion(memory.keys)
        let clearingRevision = revision
        hasClearedLocally = true; pendingClear = true; postClearIDs = []
        entries = []; memory = [:]; savedRaw = []; attemptIndices = [:]; deliveryIDs = [:]; unsavedAttempts = []
        guard let repository else { return observe(.notSaved(unavailable)) }
        let result = expectedEnabled(await repository.clearAll())
        if result == .saved, revision == clearingRevision { pendingClear = false; pendingDeletionIDs = [] }
        return observe(result)
    }

    /// Explicitly retries only the earlier deletion intent. Never runs clearAll again:
    /// sessions created after the local clear are excluded from its old deletion target.
    @discardableResult func retryPendingDeletions() async -> RemyHistoryAcknowledgement {
        guard pendingClear || !pendingDeletionIDs.isEmpty else { return .saved }
        guard let repository else { return observe(.notSaved(unavailable)) }
        let retryRevision = revision
        var targets = pendingDeletionIDs
        if pendingClear {
            do {
                var cursor: RemyHistoryCursor?
                while true {
                    let rows = try await repository.page(before:cursor,limit:1000)
                    guard revision == retryRevision else { return observe(.notSaved(.deleted)) }
                    for row in rows where !postClearIDs.contains(row.id) { targets.insert(row.id) }
                    guard let last = rows.last else { break }
                    cursor = .init(createdAt:last.createdAt,id:last.id)
                }
            } catch { return observe(.notSaved(classify(error))) }
        }
        targets = targets.filter { !postClearIDs.contains($0) || deletedIDs.contains($0) }
        // Also protect against a refresh that fetched old/unloaded rows before this retry.
        deletedIDs.formUnion(targets)
        entries.removeAll { targets.contains($0.id) }
        var firstFailure: RemyHistoryFailure?
        for id in targets {
            guard revision == retryRevision else { return observe(.notSaved(.deleted)) }
            // A new explicit post-clear session is protected unless separately deleted.
            if postClearIDs.contains(id), !deletedIDs.contains(id) { continue }
            let result = expectedEnabled(await repository.delete(sessionID:id))
            guard revision == retryRevision else { return observe(.notSaved(.deleted)) }
            if result == .saved { pendingDeletionIDs.remove(id) }
            else if case .notSaved(let failure) = result { pendingDeletionIDs.insert(id); firstFailure = firstFailure ?? failure }
        }
        if let firstFailure { return observe(.notSaved(firstFailure)) }
        pendingClear = false
        return .saved
    }

    private func expectedEnabled(_ result: RemyHistoryAcknowledgement) -> RemyHistoryAcknowledgement {
        result == .historyOff ? .notSaved(.invalidTransition) : result
    }

    /// Read-only memory recovery from explicitly supplied bytes. Database import remains authoritative;
    /// malformed/conflicting input never silently changes a previously retained entry.
    private func retainLegacyProjection(_ data: Data) {
        guard !hasClearedLocally else { return }
        struct Legacy: Decodable { let id: UUID; let text: String; let createdAt: Date; let rawText: String? }
        guard let legacy = try? JSONDecoder().decode([Legacy].self,from:data),
              Set(legacy.map(\.id)).count == legacy.count else {
            _ = observe(.notSaved(.malformedLegacy)); return
        }
        for entry in legacy where memory[entry.id] == nil && !entries.contains(where: { $0.id == entry.id }) && !deletedIDs.contains(entry.id) {
            memory[entry.id] = .init(id:entry.id,createdAt:entry.createdAt,rawText:entry.rawText,
                selectedText:entry.text,status:.completed,finishedAt:entry.createdAt,selectionKind:.legacy)
        }
        project()
    }

    private var unavailable: RemyHistoryFailure { openingFailure ?? notSaved ?? .missingSession }
    private func valid(_ context: RemyHistorySessionContext) -> Bool { context.revision == revision && !deletedIDs.contains(context.id) }
    private func classify(_ error: Error) -> RemyHistoryFailure { error as? RemyHistoryFailure ?? .storage(code:1) }
    private func observe(_ result: RemyHistoryAcknowledgement) -> RemyHistoryAcknowledgement {
        if case .notSaved(let failure) = result { notSaved = failure }
        return result
    }
    private func project() {
        var values = Dictionary(uniqueKeysWithValues:entries.map { ($0.id,$0) })
        for (id,value) in memory where !deletedIDs.contains(id) { values[id] = value }
        entries = values.values.sorted { $0.createdAt == $1.createdAt ? $0.id.uuidString > $1.id.uuidString : $0.createdAt > $1.createdAt }
    }
    private func selectMemory(_ id: UUID, text: String, kind: RemyHistorySession.SelectionKind,
                              attemptID: UUID? = nil, at date: Date) {
        guard let old = memory[id] else { return }
        memory[id] = .init(id:old.id,createdAt:old.createdAt,rawText:old.rawText,selectedText:text,engine:old.engine,
            model:old.model,language:old.language,settingsVersion:old.settingsVersion,instructionVersion:old.instructionVersion,
            status:.completed,finishedAt:date,selectedAttemptID:attemptID,rawChunk:old.rawChunk,
            continuationPrefix:old.continuationPrefix,selectionKind:kind)
        project()
    }
}
