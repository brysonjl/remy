using Microsoft.Data.Sqlite;

namespace Remy.Core.History;

/// <summary>
/// History off: same semantics as the SQLite repository, backed by a private in-memory SQLite database (nothing on disk).
/// Acknowledgements are <see cref="HistoryAckKind.HistoryOff"/> so delivery is permitted. Everything is lost at quit.
/// UI label: <see cref="HistoryMessages.HistoryOffLabel"/>.
/// </summary>
public sealed class EphemeralHistoryRepository(Func<DateTimeOffset>? clock = null)
    : SqliteHistoryRepository(new SqliteConnectionStringBuilder { DataSource = ":memory:", Pooling = false }.ToString(), true, clock);

/// <summary>
/// Used when the database cannot be opened or migrated at startup: never silently "history off". Every write is NotSaved
/// (delivery not permitted automatically), reads are empty.
/// </summary>
public sealed class UnavailableHistoryRepository(string reason) : IHistoryRepository
{
    public bool IsEphemeral => false;
    public string Reason { get; } = reason;

    HistoryAck Nope() => HistoryAck.NotSaved(HistoryFailureCategory.Unavailable, Reason);
    Task<HistoryAck> NopeAsync() => Task.FromResult(Nope());

    public Task<HistoryAck> BeginSessionAsync(HistorySessionMetadata m, CancellationToken ct = default) => NopeAsync();
    public Task<HistoryAck> SaveRawAsync(Guid sessionId, string rawChunk, CancellationToken ct = default) => NopeAsync();
    public Task<HistoryAck> AppendCleanupAttemptAsync(CleanupAttemptRecord a, CancellationToken ct = default) => NopeAsync();
    public Task<HistoryAck> SelectOutputAsync(Guid sessionId, SelectionKind kind, string text, Guid? attemptId = null, CancellationToken ct = default) => NopeAsync();
    public Task<HistoryAck> RecordDeliveryIntentAsync(Guid deliveryId, Guid sessionId, string payload, CancellationToken ct = default) => NopeAsync();
    public Task<HistoryAck> RecordDeliveryOutcomeAsync(Guid deliveryId, DeliveryOutcome outcome, string? reason = null, CancellationToken ct = default) => NopeAsync();
    public Task<HistoryAck> MarkInterruptedAsync(Guid sessionId, CancellationToken ct = default) => NopeAsync();
    public Task<HistoryAck> CompleteAsync(Guid sessionId, CancellationToken ct = default) => NopeAsync();
    public Task<HistoryAck> FailAsync(Guid sessionId, string errorCategory, CancellationToken ct = default) => NopeAsync();
    public Task<HistoryAck> RecordRecoveredAsync(HistorySessionMetadata m, string text, string? raw, CancellationToken ct = default) => NopeAsync();
    public Task<HistoryResult<HistoryPage>> QueryAsync(HistoryQuery query, CancellationToken ct = default) =>
        Task.FromResult(new HistoryResult<HistoryPage>(Nope(), new HistoryPage([], null)));
    public Task<HistoryResult<HistorySession?>> GetAsync(Guid sessionId, CancellationToken ct = default) =>
        Task.FromResult(new HistoryResult<HistorySession?>(Nope(), null));
    public Task<HistoryAck> DeleteAsync(Guid sessionId, CancellationToken ct = default) => NopeAsync();
    public Task<HistoryAck> ClearAllAsync(CancellationToken ct = default) => NopeAsync();
}
