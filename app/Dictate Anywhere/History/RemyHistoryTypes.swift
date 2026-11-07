import Foundation

nonisolated enum RemyHistoryAcknowledgement: Equatable, Sendable {
    case saved
    case historyOff
    case notSaved(RemyHistoryFailure)
}

nonisolated enum RemyHistoryFailure: Error, Equatable, Sendable {
    case storage(code: Int32)
    case unsupportedSchema(Int)
    case malformedLegacy
    case conflictingLegacyID
    case deleted
    case missingSession
    case invalidRecord
    case invalidTransition
}

/// Ephemeral configuration values only: callers must never include credentials here.
nonisolated struct RemyHistorySession: Equatable, Sendable {
    let id: UUID
    let createdAt: Date
    let rawText: String?
    let selectedText: String
    let engine: String?
    let model: String?
    let language: String?
    let settingsVersion: Int?
    let instructionVersion: Int?
    enum Status: String, Sendable { case recognized, completed, interrupted }
    let status: Status
    let finishedAt: Date?
    let selectedAttemptID: UUID?
    let rawChunk: String?
    let continuationPrefix: String?
    enum SelectionKind: String, Sendable { case raw, attempt, fallback, legacy, recovered }
    let selectionKind: SelectionKind

    init(id: UUID, createdAt: Date, rawText: String?, selectedText: String,
         engine: String? = nil, model: String? = nil, language: String? = nil,
         settingsVersion: Int? = nil, instructionVersion: Int? = nil, status: Status = .recognized, finishedAt: Date? = nil, selectedAttemptID: UUID? = nil, rawChunk: String? = nil, continuationPrefix: String? = nil, selectionKind: SelectionKind = .raw) {
        self.id = id; self.createdAt = createdAt; self.rawText = rawText
        self.selectedText = selectedText; self.engine = engine; self.model = model
        self.language = language; self.settingsVersion = settingsVersion
        self.instructionVersion = instructionVersion
        self.status = status; self.finishedAt = finishedAt; self.selectedAttemptID = selectedAttemptID
        self.rawChunk = rawChunk; self.continuationPrefix = continuationPrefix; self.selectionKind = selectionKind
    }
}

nonisolated struct RemyHistoryAttempt: Equatable, Sendable {
    let id: UUID
    let sessionID: UUID
    let createdAt: Date
    let input: String
    let output: String?
    let provider: String?
    let model: String?
    let instructionVersion: Int?
    let failureCategory: String?
    let prompt: String?
    enum Status: String, Sendable { case succeeded, failed, cancelled }
    let status: Status
    let index: Int
    let profile: RemyHistoryProfileSnapshot?
    /// Nil for older records; never infer AI duration from recording/session time.
    let startedAt: Date?
    let durationMilliseconds: Int?

    init(id: UUID, sessionID: UUID, createdAt: Date, input: String, output: String?,
         provider: String?, model: String?, instructionVersion: Int?, failureCategory: String?, prompt: String? = nil, status: Status? = nil, index: Int = 1, profile: RemyHistoryProfileSnapshot? = nil,
         startedAt: Date? = nil, durationMilliseconds: Int? = nil) {
        self.id = id; self.sessionID = sessionID; self.createdAt = createdAt
        self.input = input; self.output = output; self.provider = provider; self.model = model
        self.instructionVersion = instructionVersion; self.failureCategory = failureCategory; self.prompt = prompt
        self.status = status ?? (output == nil ? .failed : .succeeded); self.index = index; self.profile = profile
        self.startedAt = startedAt; self.durationMilliseconds = durationMilliseconds
    }
}

nonisolated struct RemyHistoryDelivery: Equatable, Sendable {
    enum Outcome: String, Sendable { case intent, copied, dispatched, verified, uncertain, failed }
    let id: UUID
    let sessionID: UUID
    let createdAt: Date
    let payload: String
    let outcome: Outcome
}

nonisolated struct RemyHistoryCursor: Equatable, Sendable {
    let createdAt: Date
    let id: UUID
}

/// A deliberately closed, credential-free snapshot schema; no URL, key, arbitrary dictionary or headers.
nonisolated struct RemyHistoryProfileSnapshot: Codable, Equatable, Sendable {
    let profileID: UUID?
    let cleanupMode: String
    let reasoningEnabled: Bool
    let contextEnabled: Bool
    let style: String?
    let structure: String?
    var profileRevision: UInt64? = nil
    var destinationMode: String? = nil

    var isBounded: Bool {
        [cleanupMode, style ?? "", structure ?? "", destinationMode ?? ""].allSatisfy { $0.utf8.count <= 128 }
    }
}
