namespace Remy.Core.History;

/// <summary>
/// Durable (or in-memory) history of dictation sessions. All writes are serialized by a single writer and acknowledged;
/// the coordinator must not deliver text unless the delivery-intent ack has <see cref="HistoryAck.IsDeliveryPermitted"/>.
/// Raw text and attempts are immutable once written. A deleted or cleared session can never be recreated by a late write.
/// </summary>
public interface IHistoryRepository
{
    /// <summary>True for the history-off repository: nothing on disk, acknowledgements are <see cref="HistoryAckKind.HistoryOff"/>.</summary>
    bool IsEphemeral { get; }

    /// <summary>Capture the repository generation for this session in memory. Writes nothing to disk.</summary>
    Task<HistoryAck> BeginSessionAsync(HistorySessionMetadata metadata, CancellationToken ct = default);

    /// <summary>Insert the session row (status Recognized, SelectionKind Raw, selected = prefix + raw). Once only.</summary>
    Task<HistoryAck> SaveRawAsync(Guid sessionId, string rawChunk, CancellationToken ct = default);

    /// <summary>Append a cleanup attempt (Succeeded needs Output; others must not have one). AttemptIndex is assigned.</summary>
    Task<HistoryAck> AppendCleanupAttemptAsync(CleanupAttemptRecord attempt, CancellationToken ct = default);

    /// <summary>Choose the final text and mark the session Completed. Kind Attempt needs a durable Succeeded attemptId.
    /// Raw = no cleanup (mode none), Fallback = cleanup failed, use corrected raw.</summary>
    Task<HistoryAck> SelectOutputAsync(Guid sessionId, SelectionKind kind, string text, Guid? attemptId = null, CancellationToken ct = default);

    /// <summary>Write the payload BEFORE any clipboard or paste effect.</summary>
    Task<HistoryAck> RecordDeliveryIntentAsync(Guid deliveryId, Guid sessionId, string payload, CancellationToken ct = default);

    /// <summary>Move a delivery along: Copied (copy_succeeded), Dispatched (paste_dispatched), CopiedOnly, Blocked, Uncertain, Failed, Verified.</summary>
    Task<HistoryAck> RecordDeliveryOutcomeAsync(Guid deliveryId, DeliveryOutcome outcome, string? reason = null, CancellationToken ct = default);

    /// <summary>Recognized -> Interrupted (cancelled cleanup, quit). No-op when the raw row was never written.</summary>
    Task<HistoryAck> MarkInterruptedAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Recognized -> Completed without changing the selection.</summary>
    Task<HistoryAck> CompleteAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Recognized -> Interrupted with an error category (timeout, unavailable, invalidInput, persistence, provider, unknown).</summary>
    Task<HistoryAck> FailAsync(Guid sessionId, string errorCategory, CancellationToken ct = default);

    /// <summary>"Recover text": insert a Completed row with SelectionKind Recovered. Raw only when known.</summary>
    Task<HistoryAck> RecordRecoveredAsync(HistorySessionMetadata metadata, string text, string? raw, CancellationToken ct = default);

    /// <summary>Newest first, paged. Search is a trimmed substring match on selected OR raw text (case-insensitive unless requested).</summary>
    Task<HistoryResult<HistoryPage>> QueryAsync(HistoryQuery query, CancellationToken ct = default);

    /// <summary>One session with attempts and deliveries. Value is null when absent.</summary>
    Task<HistoryResult<HistorySession?>> GetAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Tombstone and delete one session (attempts and deliveries cascade). Late writes are rejected.</summary>
    Task<HistoryAck> DeleteAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Delete every row and bump the generation so older in-flight sessions are rejected.</summary>
    Task<HistoryAck> ClearAllAsync(CancellationToken ct = default);
}
