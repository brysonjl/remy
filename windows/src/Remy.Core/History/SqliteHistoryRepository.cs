using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Remy.Core.History;

/// <summary>
/// SQLite history (schema v2, pipeline.md 6.2). One long-lived connection guarded by a SemaphoreSlim (single writer).
/// Journal mode is deliberately DELETE (not WAL) with synchronous=FULL, as on the Mac: one writer, small data, no -wal/-shm
/// sidecar files holding transcript text after a delete, and a plain file copy is a consistent backup.
/// Every write runs in BEGIN IMMEDIATE ... COMMIT and returns a typed acknowledgement.
/// </summary>
public class SqliteHistoryRepository : IHistoryRepository, IDisposable
{
    public const int SchemaVersion = 2;
    const int MaxPromptBytes = 65536, MaxProviderBytes = 256, MaxFailureBytes = 128, MaxProfileBytes = 128;

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly SqliteConnection _db;
    readonly Dictionary<Guid, SessionContext> _contexts = new();
    readonly Func<DateTimeOffset> _clock;
    readonly bool _ephemeral;
    long _generation;
    bool _disposed;

    sealed record SessionContext(HistorySessionMetadata Metadata, long Generation);
    readonly record struct Step(HistoryFailure? Failure, Action? After = null);

    public bool IsEphemeral => _ephemeral;
    public StartupRecoveryResult StartupRecovery { get; private set; } = new(0, 0);
    HistoryAck Saved => _ephemeral ? HistoryAck.HistoryOff : HistoryAck.Saved;

    /// <summary>Open (creating and migrating) the database file. Throws <see cref="HistoryStorageException"/> when it cannot be opened,
    /// in which case the coordinator should run with <see cref="UnavailableHistoryRepository"/> (never silently history off).</summary>
    public static Task<SqliteHistoryRepository> OpenAsync(string databasePath, Func<DateTimeOffset>? clock = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var cs = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
                return new SqliteHistoryRepository(cs, false, clock);
            }
            catch (HistoryStorageException) { throw; }
            catch (Exception ex) { throw new HistoryStorageException("History database could not be opened: " + ex.Message, ex); }
        }, ct);

    protected SqliteHistoryRepository(string connectionString, bool ephemeral, Func<DateTimeOffset>? clock)
    {
        _ephemeral = ephemeral;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _db = new SqliteConnection(connectionString);
        try
        {
            _db.Open();
            _db.CreateFunction<string?, string?, long, long>("remy_contains", (hay, needle, cs) =>
                hay != null && needle != null && hay.Contains(needle, cs != 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) ? 1 : 0, isDeterministic: true);
            Exec("PRAGMA busy_timeout=2000");
            Exec("PRAGMA foreign_keys=ON");
            Exec("PRAGMA journal_mode=DELETE");
            Exec("PRAGMA synchronous=FULL");
            Migrate();
            _generation = Convert.ToInt64(Scalar("SELECT generation FROM repository_state WHERE id=1"));
            RunStartupRecovery();
        }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ schema

    void Migrate()
    {
        var version = Convert.ToInt64(Scalar("PRAGMA user_version"));
        if (version > SchemaVersion) throw new HistoryStorageException($"History database schema {version} is newer than this version of Remy supports.");
        if (version == SchemaVersion) return;
        Exec("BEGIN IMMEDIATE");
        try
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS schema_migrations(id TEXT PRIMARY KEY, applied REAL NOT NULL);
                CREATE TABLE IF NOT EXISTS sessions(
                    id TEXT PRIMARY KEY, created REAL NOT NULL, raw TEXT NULL, selected TEXT NOT NULL,
                    engine TEXT NOT NULL, model TEXT NOT NULL, language TEXT NULL, settings_version INTEGER NULL, instruction_version INTEGER NULL,
                    status TEXT NOT NULL CHECK(status IN ('recognized','completed','interrupted')),
                    finished REAL NULL, selected_attempt TEXT NULL, raw_chunk TEXT NULL, continuation_prefix TEXT NULL,
                    selection_kind TEXT NOT NULL CHECK(selection_kind IN ('raw','attempt','fallback','legacy','recovered')),
                    error_category TEXT NULL);
                CREATE INDEX IF NOT EXISTS sessions_created ON sessions(created DESC, id DESC);
                CREATE TABLE IF NOT EXISTS attempts(
                    id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                    created REAL NOT NULL, input TEXT NOT NULL, output TEXT NULL, provider TEXT NULL, model TEXT NULL,
                    instruction_version INTEGER NULL, failure TEXT NULL, prompt TEXT NULL,
                    status TEXT NOT NULL CHECK(status IN ('succeeded','failed','cancelled')),
                    attempt_index INTEGER NOT NULL, profile TEXT NULL, started REAL NULL, duration_ms INTEGER NULL,
                    UNIQUE(session_id, attempt_index),
                    CHECK((status='succeeded') = (output IS NOT NULL)));
                CREATE TABLE IF NOT EXISTS deliveries(
                    id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                    created REAL NOT NULL, payload TEXT NOT NULL, outcome TEXT NOT NULL, reason TEXT NULL, updated REAL NOT NULL);
                CREATE INDEX IF NOT EXISTS deliveries_session ON deliveries(session_id);
                CREATE TABLE IF NOT EXISTS tombstones(id TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS repository_state(id INTEGER PRIMARY KEY CHECK(id=1), generation INTEGER NOT NULL);
                INSERT OR IGNORE INTO repository_state(id, generation) VALUES(1, 0);
                """);
            Exec("INSERT OR IGNORE INTO schema_migrations(id, applied) VALUES('001_schema_v2', @t)", ("@t", ToDb(_clock())));
            Exec($"PRAGMA user_version={SchemaVersion}");
            Exec("COMMIT");
        }
        catch
        {
            try { Exec("ROLLBACK"); } catch { }
            throw;
        }
    }

    /// <summary>A restart never replays an output: recognized sessions become interrupted, in-flight deliveries become uncertain.</summary>
    void RunStartupRecovery()
    {
        var now = ToDb(_clock());
        Exec("BEGIN IMMEDIATE");
        try
        {
            var s = Exec("UPDATE sessions SET status='interrupted', finished=COALESCE(finished,@t) WHERE status='recognized'", ("@t", now));
            var d = Exec("UPDATE deliveries SET outcome='uncertain', reason=COALESCE(reason,'Interrupted by restart'), updated=@t WHERE outcome IN ('intent','copied','dispatched')", ("@t", now));
            Exec("COMMIT");
            StartupRecovery = new StartupRecoveryResult(s, d);
        }
        catch
        {
            try { Exec("ROLLBACK"); } catch { }
            throw;
        }
    }

    // ------------------------------------------------------------------ writes

    public Task<HistoryAck> BeginSessionAsync(HistorySessionMetadata m, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            if (Tombstoned(m.Id)) return Fail(HistoryFailureCategory.SessionDeleted, "Session was deleted.");
            var gen = _generation;
            return new Step(null, () => _contexts[m.Id] = new SessionContext(m, gen));
        }, ct);

    public Task<HistoryAck> SaveRawAsync(Guid id, string rawChunk, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            if (CheckLive(id) is { } f) return new Step(f);
            if (!_contexts.TryGetValue(id, out var ctx)) return Fail(HistoryFailureCategory.NotFound, "Session was not begun.");
            if (RowStatus(id) != null) return Fail(HistoryFailureCategory.InvalidTransition, "Raw text is already recorded and immutable.");
            var m = ctx.Metadata;
            var selected = Join(m.ContinuationPrefix, rawChunk);
            InsertSession(m, rawChunk, selected, "recognized", "raw", null, rawChunk, m.ContinuationPrefix);
            return default;
        }, ct);

    public Task<HistoryAck> RecordRecoveredAsync(HistorySessionMetadata m, string text, string? raw, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            if (Tombstoned(m.Id)) return Fail(HistoryFailureCategory.SessionDeleted, "Session was deleted.");
            if (RowStatus(m.Id) != null) return Fail(HistoryFailureCategory.InvalidTransition, "Session already recorded.");
            InsertSession(m, raw, text, "completed", "recovered", ToDb(_clock()), null, null);
            return default;
        }, ct);

    void InsertSession(HistorySessionMetadata m, string? raw, string selected, string status, string kind, double? finished, string? rawChunk, string? prefix) =>
        Exec("""
            INSERT INTO sessions(id, created, raw, selected, engine, model, language, settings_version, instruction_version, status, finished, selected_attempt, raw_chunk, continuation_prefix, selection_kind)
            VALUES(@id,@c,@raw,@sel,@e,@m,@l,@sv,@iv,@st,@fin,NULL,@rc,@cp,@k)
            """,
            ("@id", Id(m.Id)), ("@c", ToDb(m.Created)), ("@raw", raw), ("@sel", selected), ("@e", m.Engine), ("@m", m.Model), ("@l", m.Language),
            ("@sv", m.SettingsVersion), ("@iv", m.InstructionVersion), ("@st", status), ("@fin", finished), ("@rc", rawChunk), ("@cp", prefix), ("@k", kind));

    public Task<HistoryAck> AppendCleanupAttemptAsync(CleanupAttemptRecord a, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            if ((a.Status == AttemptStatus.Succeeded) != (a.Output != null)) return Fail(HistoryFailureCategory.InvalidInput, "A succeeded attempt must have output, and only a succeeded attempt may.");
            if (Bytes(a.Prompt) > MaxPromptBytes || Bytes(a.Provider) > MaxProviderBytes || Bytes(a.Model) > MaxProviderBytes || Bytes(a.Failure) > MaxFailureBytes)
                return Fail(HistoryFailureCategory.InvalidInput, "Attempt field too large.");
            var p = a.Profile;
            if (p != null && new[] { p.ProfileID, p.CleanupMode, p.Style, p.Structure, p.DestinationMode }.Any(s => Bytes(s) > MaxProfileBytes))
                return Fail(HistoryFailureCategory.InvalidInput, "Profile snapshot field too large.");
            if (CheckLive(a.SessionId) is { } f) return new Step(f);
            if (RowStatus(a.SessionId) == null) return Fail(HistoryFailureCategory.NotFound, "Raw text must be saved before cleanup attempts.");
            if (Scalar("SELECT 1 FROM attempts WHERE id=@id", ("@id", Id(a.Id))) != null) return Fail(HistoryFailureCategory.InvalidInput, "Attempts are append-only; id already exists.");
            var index = Convert.ToInt32(Scalar("SELECT COALESCE(MAX(attempt_index),0)+1 FROM attempts WHERE session_id=@s", ("@s", Id(a.SessionId))));
            Exec("""
                INSERT INTO attempts(id, session_id, created, input, output, provider, model, instruction_version, failure, prompt, status, attempt_index, profile, started, duration_ms)
                VALUES(@id,@s,@c,@in,@out,@pr,@mo,@iv,@fa,@pm,@st,@ix,@pf,@sa,@du)
                """,
                ("@id", Id(a.Id)), ("@s", Id(a.SessionId)), ("@c", ToDb(a.Created)), ("@in", a.Input), ("@out", a.Output), ("@pr", a.Provider), ("@mo", a.Model),
                ("@iv", a.InstructionVersion), ("@fa", a.Failure), ("@pm", a.Prompt), ("@st", AttemptToDb(a.Status)), ("@ix", index),
                ("@pf", p == null ? null : JsonSerializer.Serialize(p)), ("@sa", a.Started is { } st ? ToDb(st) : null), ("@du", a.DurationMs));
            return default;
        }, ct);

    public Task<HistoryAck> SelectOutputAsync(Guid id, SelectionKind kind, string text, Guid? attemptId = null, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            if (CheckLive(id) is { } f) return new Step(f);
            var status = RowStatus(id);
            if (status == null) return Fail(HistoryFailureCategory.NotFound, "Session has no saved raw text.");
            if (status != "recognized") return Fail(HistoryFailureCategory.InvalidTransition, "Session is already " + status + ".");
            if (kind is SelectionKind.Recovered or SelectionKind.Legacy) return Fail(HistoryFailureCategory.InvalidInput, "Selection kind not allowed here.");
            if (kind == SelectionKind.Attempt)
            {
                if (attemptId == null || Scalar("SELECT 1 FROM attempts WHERE id=@a AND session_id=@s AND status='succeeded'", ("@a", Id(attemptId.Value)), ("@s", Id(id))) == null)
                    return Fail(HistoryFailureCategory.InvalidInput, "Selected attempt must be a durable succeeded attempt of this session.");
            }
            Exec("UPDATE sessions SET selected=@t, selected_attempt=@a, selection_kind=@k, status='completed', finished=@now WHERE id=@id",
                ("@t", text), ("@a", kind == SelectionKind.Attempt ? Id(attemptId!.Value) : null), ("@k", KindToDb(kind)), ("@now", ToDb(_clock())), ("@id", Id(id)));
            return default;
        }, ct);

    public Task<HistoryAck> RecordDeliveryIntentAsync(Guid deliveryId, Guid sessionId, string payload, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            if (CheckLive(sessionId) is { } f) return new Step(f);
            if (RowStatus(sessionId) == null) return Fail(HistoryFailureCategory.NotFound, "Session has no saved row.");
            if (Scalar("SELECT 1 FROM deliveries WHERE id=@d", ("@d", Id(deliveryId))) != null) return Fail(HistoryFailureCategory.InvalidInput, "Delivery already recorded.");
            var now = ToDb(_clock());
            Exec("INSERT INTO deliveries(id, session_id, created, payload, outcome, reason, updated) VALUES(@d,@s,@t,@p,'intent',NULL,@t)",
                ("@d", Id(deliveryId)), ("@s", Id(sessionId)), ("@t", now), ("@p", payload));
            return default;
        }, ct);

    public Task<HistoryAck> RecordDeliveryOutcomeAsync(Guid deliveryId, DeliveryOutcome outcome, string? reason = null, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            if (outcome == DeliveryOutcome.Intent) return Fail(HistoryFailureCategory.InvalidInput, "Intent is recorded with RecordDeliveryIntent.");
            string? sid = null, cur = null;
            using (var c = Cmd("SELECT session_id, outcome FROM deliveries WHERE id=@d", ("@d", Id(deliveryId))))
            using (var r = c.ExecuteReader())
                if (r.Read()) { sid = r.GetString(0); cur = r.GetString(1); }
            if (sid == null) return Fail(HistoryFailureCategory.NotFound, "Delivery was not recorded (the session may have been deleted).");
            if (CheckLive(Guid.Parse(sid)) is { } f) return new Step(f);
            if (!AllowedTransition(OutcomeFromDb(cur!), outcome)) return Fail(HistoryFailureCategory.InvalidTransition, $"Delivery cannot move from {cur} to {OutcomeToDb(outcome)}.");
            Exec("UPDATE deliveries SET outcome=@o, reason=@r, updated=@t WHERE id=@d", ("@o", OutcomeToDb(outcome)), ("@r", reason), ("@t", ToDb(_clock())), ("@d", Id(deliveryId)));
            return default;
        }, ct);

    static bool AllowedTransition(DeliveryOutcome from, DeliveryOutcome to) => from switch
    {
        DeliveryOutcome.Intent => to != DeliveryOutcome.Intent,
        DeliveryOutcome.Copied => to is DeliveryOutcome.Dispatched or DeliveryOutcome.Verified or DeliveryOutcome.Uncertain or DeliveryOutcome.Failed or DeliveryOutcome.CopiedOnly or DeliveryOutcome.Blocked,
        DeliveryOutcome.Dispatched => to is DeliveryOutcome.Verified or DeliveryOutcome.Uncertain or DeliveryOutcome.Failed,
        _ => false, // terminal
    };

    public Task<HistoryAck> MarkInterruptedAsync(Guid id, CancellationToken ct = default) => Finish(id, null, ct);
    public Task<HistoryAck> FailAsync(Guid id, string errorCategory, CancellationToken ct = default) => Finish(id, errorCategory, ct);

    Task<HistoryAck> Finish(Guid id, string? error, CancellationToken ct) =>
        WriteAsync(() =>
        {
            if (CheckLive(id) is { } f) return new Step(f);
            var status = RowStatus(id);
            if (status == null) return new Step(null, () => _contexts.Remove(id)); // nothing was persisted
            if (status == "interrupted") return default;
            if (status == "completed") return Fail(HistoryFailureCategory.InvalidTransition, "Session is already completed.");
            Exec("UPDATE sessions SET status='interrupted', finished=@t, error_category=COALESCE(@e,error_category) WHERE id=@id", ("@t", ToDb(_clock())), ("@e", error), ("@id", Id(id)));
            return default;
        }, ct);

    public Task<HistoryAck> CompleteAsync(Guid id, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            if (CheckLive(id) is { } f) return new Step(f);
            var status = RowStatus(id);
            if (status == null) return Fail(HistoryFailureCategory.NotFound, "Session has no saved row.");
            if (status == "completed") return default;
            if (status != "recognized") return Fail(HistoryFailureCategory.InvalidTransition, "Session is already " + status + ".");
            Exec("UPDATE sessions SET status='completed', finished=@t WHERE id=@id", ("@t", ToDb(_clock())), ("@id", Id(id)));
            return default;
        }, ct);

    public Task<HistoryAck> DeleteAsync(Guid id, CancellationToken ct = default) =>
        WriteAsync(() =>
        {
            Exec("INSERT OR IGNORE INTO tombstones(id) VALUES(@id)", ("@id", Id(id)));
            Exec("DELETE FROM sessions WHERE id=@id", ("@id", Id(id)));
            return new Step(null, () => _contexts.Remove(id));
        }, ct);

    public async Task<HistoryAck> ClearAllAsync(CancellationToken ct = default)
    {
        var ack = await WriteAsync(() =>
        {
            Exec("INSERT OR IGNORE INTO tombstones(id) SELECT id FROM sessions");
            Exec("DELETE FROM sessions");
            Exec("UPDATE repository_state SET generation=generation+1 WHERE id=1");
            return new Step(null, () => { _generation++; });
        }, ct).ConfigureAwait(false);
        if (ack.Kind == HistoryAckKind.NotSaved || _ephemeral) return ack;
        // Best effort: rewrite the file so deleted text is not left in free pages.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { if (!_disposed) Exec("VACUUM"); } catch (SqliteException) { } finally { _gate.Release(); }
        return ack;
    }

    // ------------------------------------------------------------------ reads

    public Task<HistoryResult<HistorySession?>> GetAsync(Guid id, CancellationToken ct = default) =>
        ReadAsync(() => LoadSession(id, true), ct);

    public Task<HistoryResult<HistoryPage>> QueryAsync(HistoryQuery q, CancellationToken ct = default) =>
        ReadAsync(() =>
        {
            var limit = Math.Clamp(q.Limit, 1, 1000);
            var search = string.IsNullOrWhiteSpace(q.Search) ? null : q.Search.Trim();
            var sql = $"SELECT {SessionColumns} FROM sessions WHERE 1=1";
            var ps = new List<(string, object?)>();
            if (q.After != null)
            {
                sql += " AND (created < @cc OR (created = @cc AND id < @ci))";
                ps.Add(("@cc", q.After.Created)); ps.Add(("@ci", q.After.Id));
            }
            if (search != null)
            {
                sql += " AND (remy_contains(selected,@s,@cs) OR remy_contains(raw,@s,@cs))";
                ps.Add(("@s", search)); ps.Add(("@cs", q.CaseSensitive ? 1L : 0L));
            }
            sql += " ORDER BY created DESC, id DESC LIMIT @lim";
            ps.Add(("@lim", limit + 1));
            var rows = new List<(HistorySession S, double Created)>();
            using (var c = Cmd(sql, ps.ToArray()))
            using (var r = c.ExecuteReader())
                while (r.Read()) rows.Add((ReadSession(r), r.GetDouble(1)));
            var more = rows.Count > limit;
            if (more) rows.RemoveAt(rows.Count - 1);
            var items = rows.Select(x => q.IncludeDetails ? x.S with { Attempts = LoadAttempts(x.S.Id), Deliveries = LoadDeliveries(x.S.Id) } : x.S).ToList();
            HistoryCursor? next = more && rows.Count > 0 ? new HistoryCursor(rows[^1].Created, Id(rows[^1].S.Id)) : null;
            return new HistoryPage(items, next);
        }, ct);

    const string SessionColumns = "id, created, finished, raw, selected, engine, model, language, settings_version, instruction_version, status, selected_attempt, raw_chunk, continuation_prefix, selection_kind, error_category";

    HistorySession? LoadSession(Guid id, bool details)
    {
        using var c = Cmd($"SELECT {SessionColumns} FROM sessions WHERE id=@id", ("@id", Id(id)));
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        var s = ReadSession(r);
        r.Close();
        return details ? s with { Attempts = LoadAttempts(id), Deliveries = LoadDeliveries(id) } : s;
    }

    static HistorySession ReadSession(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), FromDb(r.GetDouble(1)), r.IsDBNull(2) ? null : FromDb(r.GetDouble(2)),
        r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetInt32(9),
        r.GetString(10) switch { "completed" => HistorySessionStatus.Completed, "interrupted" => HistorySessionStatus.Interrupted, _ => HistorySessionStatus.Recognized },
        r.IsDBNull(11) ? null : Guid.Parse(r.GetString(11)), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13),
        KindFromDb(r.GetString(14)), r.IsDBNull(15) ? null : r.GetString(15), [], []);

    List<CleanupAttemptRecord> LoadAttempts(Guid id)
    {
        var list = new List<CleanupAttemptRecord>();
        using var c = Cmd("SELECT id, session_id, created, input, output, provider, model, instruction_version, failure, prompt, status, profile, started, duration_ms, attempt_index FROM attempts WHERE session_id=@s ORDER BY attempt_index", ("@s", Id(id)));
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            list.Add(new CleanupAttemptRecord(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), FromDb(r.GetDouble(2)), r.GetString(3), Str(4), Str(5), Str(6),
                r.IsDBNull(7) ? null : r.GetInt32(7), Str(8), Str(9), AttemptFromDb(r.GetString(10)),
                Str(11) is { } pj ? JsonSerializer.Deserialize<CleanupProfileSnapshot>(pj) : null,
                r.IsDBNull(12) ? null : FromDb(r.GetDouble(12)), r.IsDBNull(13) ? null : r.GetInt32(13), r.GetInt32(14)));
        }
        return list;
    }

    List<DeliveryRecord> LoadDeliveries(Guid id)
    {
        var list = new List<DeliveryRecord>();
        using var c = Cmd("SELECT id, session_id, created, payload, outcome, reason, updated FROM deliveries WHERE session_id=@s ORDER BY created, rowid", ("@s", Id(id)));
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new DeliveryRecord(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), FromDb(r.GetDouble(2)), r.GetString(3), OutcomeFromDb(r.GetString(4)),
                r.IsDBNull(5) ? null : r.GetString(5), FromDb(r.GetDouble(6))));
        return list;
    }

    // ------------------------------------------------------------------ plumbing

    async Task<HistoryAck> WriteAsync(Func<Step> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return HistoryAck.NotSaved(HistoryFailureCategory.Unavailable, "History is closed.");
            Exec("BEGIN IMMEDIATE");
            Step step;
            try { step = work(); }
            catch { TryRollback(); throw; }
            if (step.Failure != null) { TryRollback(); return new HistoryAck(HistoryAckKind.NotSaved, step.Failure); }
            Exec("COMMIT");
            step.After?.Invoke();
            return Saved;
        }
        catch (SqliteException ex)
        {
            TryRollback();
            return HistoryAck.NotSaved(HistoryFailureCategory.Persistence, "History could not be written: " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    async Task<HistoryResult<T>> ReadAsync<T>(Func<T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return new(HistoryAck.NotSaved(HistoryFailureCategory.Unavailable, "History is closed."), default);
            return new(Saved, work());
        }
        catch (SqliteException ex)
        {
            return new(HistoryAck.NotSaved(HistoryFailureCategory.Persistence, "History could not be read: " + ex.Message), default);
        }
        finally { _gate.Release(); }
    }

    void TryRollback() { try { Exec("ROLLBACK"); } catch (SqliteException) { } }

    static Step Fail(HistoryFailureCategory c, string m) => new(new HistoryFailure(c, m));

    bool Tombstoned(Guid id) => Scalar("SELECT 1 FROM tombstones WHERE id=@id", ("@id", Id(id))) != null;

    HistoryFailure? CheckLive(Guid id)
    {
        if (Tombstoned(id)) return new(HistoryFailureCategory.SessionDeleted, "Session was deleted.");
        if (_contexts.TryGetValue(id, out var c) && c.Generation != _generation) return new(HistoryFailureCategory.StaleGeneration, "History was cleared after this session began.");
        return null;
    }

    string? RowStatus(Guid id) => Scalar("SELECT status FROM sessions WHERE id=@id", ("@id", Id(id))) as string;

    SqliteCommand Cmd(string sql, params (string Name, object? Value)[] ps)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return c;
    }

    int Exec(string sql, params (string, object?)[] ps) { using var c = Cmd(sql, ps); return c.ExecuteNonQuery(); }
    object? Scalar(string sql, params (string, object?)[] ps) { using var c = Cmd(sql, ps); var v = c.ExecuteScalar(); return v is DBNull ? null : v; }

    static string Id(Guid g) => g.ToString("D").ToUpperInvariant();
    static double ToDb(DateTimeOffset t) => t.ToUnixTimeMilliseconds() / 1000.0;
    static DateTimeOffset FromDb(double s) => DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(s * 1000));
    static int Bytes(string? s) => s == null ? 0 : Encoding.UTF8.GetByteCount(s);
    static string Join(string? prefix, string chunk) => string.IsNullOrEmpty(prefix) ? chunk : string.IsNullOrEmpty(chunk) ? prefix : prefix.TrimEnd() + " " + chunk;

    static string AttemptToDb(AttemptStatus s) => s switch { AttemptStatus.Succeeded => "succeeded", AttemptStatus.Failed => "failed", _ => "cancelled" };
    static AttemptStatus AttemptFromDb(string s) => s switch { "succeeded" => AttemptStatus.Succeeded, "failed" => AttemptStatus.Failed, _ => AttemptStatus.Cancelled };
    static string KindToDb(SelectionKind k) => k.ToString().ToLowerInvariant();
    static SelectionKind KindFromDb(string s) => Enum.TryParse<SelectionKind>(s, true, out var k) ? k : SelectionKind.Raw;
    static string OutcomeToDb(DeliveryOutcome o) => o switch { DeliveryOutcome.CopiedOnly => "copied_only", _ => o.ToString().ToLowerInvariant() };
    static DeliveryOutcome OutcomeFromDb(string s) => s switch
    {
        "intent" => DeliveryOutcome.Intent, "copied" => DeliveryOutcome.Copied, "dispatched" => DeliveryOutcome.Dispatched, "verified" => DeliveryOutcome.Verified,
        "failed" => DeliveryOutcome.Failed, "copied_only" => DeliveryOutcome.CopiedOnly, "blocked" => DeliveryOutcome.Blocked, _ => DeliveryOutcome.Uncertain,
    };

    public void Dispose()
    {
        _gate.Wait();
        try { if (!_disposed) { _disposed = true; _db.Dispose(); } }
        finally { _gate.Release(); }
    }
}
