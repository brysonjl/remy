// History record and result types (pipeline.md section 6.2). Mirrors Mac schema v2 semantics.

namespace Remy.Core.History;

public enum HistorySessionStatus { Recognized, Completed, Interrupted }

public enum SelectionKind { Raw, Attempt, Fallback, Legacy, Recovered }

public enum AttemptStatus { Succeeded, Failed, Cancelled }

/// <summary>Delivery outcome as stored. Mac: intent, copied, dispatched, verified, uncertain, failed.
/// Windows adds CopiedOnly and Blocked as terminal outcomes (copy-only delivery, paste refused).</summary>
public enum DeliveryOutcome { Intent, Copied, Dispatched, Verified, Uncertain, Failed, CopiedOnly, Blocked }

public enum HistoryAckKind { Saved, HistoryOff, NotSaved }

public enum HistoryFailureCategory { Persistence, SessionDeleted, StaleGeneration, NotFound, InvalidTransition, InvalidInput, Unavailable }

public sealed record HistoryFailure(HistoryFailureCategory Category, string Message);

/// <summary>Acknowledgement of a history write. Delivery is permitted for Saved and HistoryOff only.</summary>
public sealed record HistoryAck(HistoryAckKind Kind, HistoryFailure? Failure = null)
{
    public bool IsDeliveryPermitted => Kind != HistoryAckKind.NotSaved;
    public static HistoryAck Saved { get; } = new(HistoryAckKind.Saved);
    public static HistoryAck HistoryOff { get; } = new(HistoryAckKind.HistoryOff);
    public static HistoryAck NotSaved(HistoryFailureCategory c, string message) => new(HistoryAckKind.NotSaved, new HistoryFailure(c, message));
}

public sealed record HistoryResult<T>(HistoryAck Ack, T? Value)
{
    public bool Ok => Ack.Kind != HistoryAckKind.NotSaved;
}

/// <summary>Metadata captured at session start. Nothing is written to disk until SaveRaw.</summary>
public sealed record HistorySessionMetadata(
    Guid Id,
    DateTimeOffset Created,
    string Engine,
    string Model,
    string? Language = null,
    int? SettingsVersion = null,
    int? InstructionVersion = null,
    string? ContinuationPrefix = null);

/// <summary>Profile snapshot stored with an attempt. No credentials. Strings are limited to 128 bytes.</summary>
public sealed record CleanupProfileSnapshot(
    string? ProfileID,
    string? CleanupMode,
    bool? ReasoningEnabled,
    bool? ContextEnabled,
    string? Style,
    string? Structure,
    int? ProfileRevision,
    string? DestinationMode);

public sealed record CleanupAttemptRecord(
    Guid Id,
    Guid SessionId,
    DateTimeOffset Created,
    string Input,
    string? Output,
    string? Provider,
    string? Model,
    int? InstructionVersion,
    string? Failure,            // failure category (<= 128 bytes), never a response body
    string? Prompt,             // <= 65 536 bytes
    AttemptStatus Status,
    CleanupProfileSnapshot? Profile = null,
    DateTimeOffset? Started = null,
    int? DurationMs = null,
    int AttemptIndex = 0);      // assigned by the repository on append

public sealed record DeliveryRecord(
    Guid Id,
    Guid SessionId,
    DateTimeOffset Created,
    string Payload,
    DeliveryOutcome Outcome,
    string? Reason,
    DateTimeOffset Updated);

/// <summary>A history row. Attempts and Deliveries are empty unless details were requested (Get always loads them).</summary>
public sealed record HistorySession(
    Guid Id,
    DateTimeOffset Created,
    DateTimeOffset? Finished,
    string? Raw,
    string Selected,
    string Engine,
    string Model,
    string? Language,
    int? SettingsVersion,
    int? InstructionVersion,
    HistorySessionStatus Status,
    Guid? SelectedAttemptId,
    string? RawChunk,
    string? ContinuationPrefix,
    SelectionKind SelectionKind,
    string? ErrorCategory,
    IReadOnlyList<CleanupAttemptRecord> Attempts,
    IReadOnlyList<DeliveryRecord> Deliveries);

/// <summary>Cursor for paging newest first: pass the previous page's NextCursor.</summary>
public sealed record HistoryCursor(double Created, string Id);

public sealed record HistoryQuery(string? Search = null, int Limit = 1000, HistoryCursor? After = null, bool IncludeDetails = false, bool CaseSensitive = false);

public sealed record HistoryPage(IReadOnlyList<HistorySession> Items, HistoryCursor? NextCursor);

public sealed record StartupRecoveryResult(int SessionsInterrupted, int DeliveriesMarkedUncertain);

public sealed class HistoryStorageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>User-facing strings (verbatim from the Mac spec).</summary>
public static class HistoryMessages
{
    public const string HistoryOffLabel = "History off";
    public const string HistoryOffHint = "History off. New transcripts stay in memory for delivery and cannot be recovered after Remy quits.";
    public const string NotSavedWarning = "Some history changes were not saved. Copy any unsaved text you need before quitting Remy. A paste that was already sent is never automatically retried.";
    public const string DeletionNotConfirmed = "Deleted history is hidden for this session, but its deletion from disk is not confirmed. It may return after Remy restarts.";
    public const string ClearAllConfirmMessage = "Delete this app's saved transcripts, recovery recordings and history rollback backups. This removes history rollback options. Models, settings and API keys are kept. Copies you exported, the clipboard and system backups are not cleared. This cannot be undone.";
    public const string ClearBlockedByRecovery = "History could not be cleared while recovery audio is in use. Try Clear All again after recovery finishes.";
    public const string ClearPartialFailure = "Some stored history could not be deleted. Saved transcripts, recovery files or rollback backups may remain and return after restart. Repair folder access, then use Clear All again.";
    public const string NoDeliveryRecorded = "No delivery result recorded. Older records may not include delivery details.";
}
