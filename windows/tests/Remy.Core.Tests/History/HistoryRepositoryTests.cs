using Remy.Core.History;
using Xunit;

namespace Remy.Core.Tests.History;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remy-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Combine(string p) => System.IO.Path.Combine(Path, p);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class HistoryRepositoryTests
{
    static HistorySessionMetadata Meta(Guid? id = null, string? prefix = null) =>
        new(id ?? Guid.NewGuid(), DateTimeOffset.UtcNow, "parakeet", "v2", "en", 1, 1, prefix);

    static async Task<Guid> Raw(IHistoryRepository r, string raw = "hello world", HistorySessionMetadata? m = null)
    {
        m ??= Meta();
        Assert.True((await r.BeginSessionAsync(m)).IsDeliveryPermitted);
        Assert.True((await r.SaveRawAsync(m.Id, raw)).IsDeliveryPermitted);
        return m.Id;
    }

    static CleanupAttemptRecord Attempt(Guid session, AttemptStatus st = AttemptStatus.Succeeded, string? output = "Hello, world.") =>
        new(Guid.NewGuid(), session, DateTimeOffset.UtcNow, "hello world", st == AttemptStatus.Succeeded ? output : null, "ollama", "m", 1, null, "prompt", st,
            new CleanupProfileSnapshot("p", "ai", true, false, "casual", "none", 1, "d"));

    [Fact]
    public async Task Lifecycle_raw_attempt_select_delivery()
    {
        using var d = new TempDir();
        using var repo = await SqliteHistoryRepository.OpenAsync(d.Combine("h.sqlite"));
        var id = await Raw(repo);
        var a = Attempt(id);
        Assert.Equal(HistoryAckKind.Saved, (await repo.AppendCleanupAttemptAsync(a)).Kind);
        Assert.Equal(HistoryAckKind.Saved, (await repo.SelectOutputAsync(id, SelectionKind.Attempt, "Hello, world.", a.Id)).Kind);
        var del = Guid.NewGuid();
        Assert.Equal(HistoryAckKind.Saved, (await repo.RecordDeliveryIntentAsync(del, id, "Hello, world.")).Kind);
        Assert.Equal(HistoryAckKind.Saved, (await repo.RecordDeliveryOutcomeAsync(del, DeliveryOutcome.Copied)).Kind);
        Assert.Equal(HistoryAckKind.Saved, (await repo.RecordDeliveryOutcomeAsync(del, DeliveryOutcome.Dispatched)).Kind);
        Assert.Equal(HistoryAckKind.Saved, (await repo.RecordDeliveryOutcomeAsync(del, DeliveryOutcome.Verified, "ok")).Kind);

        var s = (await repo.GetAsync(id)).Value!;
        Assert.Equal("hello world", s.Raw);
        Assert.Equal("Hello, world.", s.Selected);
        Assert.Equal(HistorySessionStatus.Completed, s.Status);
        Assert.Equal(SelectionKind.Attempt, s.SelectionKind);
        Assert.Equal(a.Id, s.SelectedAttemptId);
        var att = Assert.Single(s.Attempts);
        Assert.Equal(1, att.AttemptIndex);
        Assert.Equal("casual", att.Profile!.Style);
        Assert.Equal(DeliveryOutcome.Verified, Assert.Single(s.Deliveries).Outcome);
    }

    [Fact]
    public async Task Raw_is_immutable_and_prefix_joined()
    {
        using var d = new TempDir();
        using var repo = await SqliteHistoryRepository.OpenAsync(d.Combine("h.sqlite"));
        var m = Meta(prefix: "earlier words");
        await repo.BeginSessionAsync(m);
        Assert.True((await repo.SaveRawAsync(m.Id, "new chunk")).IsDeliveryPermitted);
        var again = await repo.SaveRawAsync(m.Id, "different");
        Assert.Equal(HistoryFailureCategory.InvalidTransition, again.Failure!.Category);
        var s = (await repo.GetAsync(m.Id)).Value!;
        Assert.Equal("new chunk", s.Raw);
        Assert.Equal("earlier words new chunk", s.Selected);
    }

    [Fact]
    public async Task Attempts_append_only_and_indexed_and_validated()
    {
        using var d = new TempDir();
        using var repo = await SqliteHistoryRepository.OpenAsync(d.Combine("h.sqlite"));
        var id = await Raw(repo);
        var a1 = Attempt(id, AttemptStatus.Failed);
        var a2 = Attempt(id);
        Assert.True((await repo.AppendCleanupAttemptAsync(a1)).IsDeliveryPermitted);
        Assert.True((await repo.AppendCleanupAttemptAsync(a2)).IsDeliveryPermitted);
        Assert.False((await repo.AppendCleanupAttemptAsync(a2)).IsDeliveryPermitted); // duplicate id
        Assert.False((await repo.AppendCleanupAttemptAsync(a1 with { Id = Guid.NewGuid(), Output = "x" })).IsDeliveryPermitted); // failed with output
        Assert.False((await repo.AppendCleanupAttemptAsync(a1 with { Id = Guid.NewGuid(), Prompt = new string('x', 70000) })).IsDeliveryPermitted);
        var s = (await repo.GetAsync(id)).Value!;
        Assert.Equal([1, 2], s.Attempts.Select(x => x.AttemptIndex).ToArray());
        // selecting a failed attempt is refused
        var bad = await repo.SelectOutputAsync(id, SelectionKind.Attempt, "x", a1.Id);
        Assert.Equal(HistoryFailureCategory.InvalidInput, bad.Failure!.Category);
        // fallback after failure
        Assert.True((await repo.SelectOutputAsync(id, SelectionKind.Fallback, "hello world")).IsDeliveryPermitted);
        Assert.Equal(SelectionKind.Fallback, (await repo.GetAsync(id)).Value!.SelectionKind);
    }

    [Fact]
    public async Task Delete_tombstones_and_rejects_late_writes()
    {
        using var d = new TempDir();
        using var repo = await SqliteHistoryRepository.OpenAsync(d.Combine("h.sqlite"));
        var id = await Raw(repo);
        Assert.True((await repo.DeleteAsync(id)).IsDeliveryPermitted);
        Assert.Null((await repo.GetAsync(id)).Value);
        var late = await repo.AppendCleanupAttemptAsync(Attempt(id));
        Assert.Equal(HistoryFailureCategory.SessionDeleted, late.Failure!.Category);
        Assert.False((await repo.RecordDeliveryIntentAsync(Guid.NewGuid(), id, "x")).IsDeliveryPermitted);
        Assert.False((await repo.SaveRawAsync(id, "again")).IsDeliveryPermitted);
        Assert.Empty((await repo.QueryAsync(new HistoryQuery())).Value!.Items);
    }

    [Fact]
    public async Task Delete_before_raw_blocks_save_raw()
    {
        using var d = new TempDir();
        using var repo = await SqliteHistoryRepository.OpenAsync(d.Combine("h.sqlite"));
        var m = Meta();
        await repo.BeginSessionAsync(m);
        await repo.DeleteAsync(m.Id);
        Assert.False((await repo.SaveRawAsync(m.Id, "x")).IsDeliveryPermitted);
    }

    [Fact]
    public async Task ClearAll_rejects_older_sessions_but_allows_new_ones()
    {
        using var d = new TempDir();
        var path = d.Combine("h.sqlite");
        using var repo = await SqliteHistoryRepository.OpenAsync(path);
        var old = Meta();
        await repo.BeginSessionAsync(old);
        var saved = await Raw(repo, "keep then clear");
        Assert.True((await repo.ClearAllAsync()).IsDeliveryPermitted);
        Assert.Empty((await repo.QueryAsync(new HistoryQuery())).Value!.Items);
        Assert.Equal(HistoryFailureCategory.StaleGeneration, (await repo.SaveRawAsync(old.Id, "late")).Failure!.Category);
        Assert.False((await repo.AppendCleanupAttemptAsync(Attempt(saved))).IsDeliveryPermitted);
        var fresh = await Raw(repo, "fresh");
        Assert.Equal("fresh", (await repo.GetAsync(fresh)).Value!.Raw);
    }

    [Fact]
    public async Task Search_pages_newest_first_and_matches_selected_or_raw()
    {
        using var d = new TempDir();
        using var repo = await SqliteHistoryRepository.OpenAsync(d.Combine("h.sqlite"));
        var t = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var m = Meta() with { Created = t.AddMinutes(i) };
            await repo.BeginSessionAsync(m);
            await repo.SaveRawAsync(m.Id, i == 2 ? "needle in raw" : $"text {i}");
            await repo.SelectOutputAsync(m.Id, SelectionKind.Raw, i == 3 ? "Another NEEDLE here" : $"Text {i}.");
            ids.Add(m.Id);
        }
        var all = (await repo.QueryAsync(new HistoryQuery(Limit: 2))).Value!;
        Assert.Equal([ids[4], ids[3]], all.Items.Select(x => x.Id).ToArray());
        Assert.NotNull(all.NextCursor);
        var p2 = (await repo.QueryAsync(new HistoryQuery(Limit: 2, After: all.NextCursor))).Value!;
        Assert.Equal([ids[2], ids[1]], p2.Items.Select(x => x.Id).ToArray());
        var p3 = (await repo.QueryAsync(new HistoryQuery(Limit: 2, After: p2.NextCursor))).Value!;
        Assert.Equal([ids[0]], p3.Items.Select(x => x.Id).ToArray());
        Assert.Null(p3.NextCursor);

        var hits = (await repo.QueryAsync(new HistoryQuery("  needle "))).Value!.Items;
        Assert.Equal([ids[3], ids[2]], hits.Select(x => x.Id).ToArray());
        Assert.Single((await repo.QueryAsync(new HistoryQuery("NEEDLE", CaseSensitive: true))).Value!.Items);
        Assert.Empty((await repo.QueryAsync(new HistoryQuery("absent"))).Value!.Items);
    }

    [Fact]
    public async Task Query_can_include_details()
    {
        using var d = new TempDir();
        using var repo = await SqliteHistoryRepository.OpenAsync(d.Combine("h.sqlite"));
        var id = await Raw(repo);
        await repo.AppendCleanupAttemptAsync(Attempt(id));
        Assert.Empty((await repo.QueryAsync(new HistoryQuery())).Value!.Items[0].Attempts);
        Assert.Single((await repo.QueryAsync(new HistoryQuery(IncludeDetails: true))).Value!.Items[0].Attempts);
    }

    [Fact]
    public async Task Interrupted_and_failed()
    {
        using var d = new TempDir();
        using var repo = await SqliteHistoryRepository.OpenAsync(d.Combine("h.sqlite"));
        var a = await Raw(repo);
        var b = await Raw(repo);
        Assert.True((await repo.MarkInterruptedAsync(a)).IsDeliveryPermitted);
        Assert.True((await repo.FailAsync(b, "timeout")).IsDeliveryPermitted);
        Assert.Equal(HistorySessionStatus.Interrupted, (await repo.GetAsync(a)).Value!.Status);
        Assert.Equal("timeout", (await repo.GetAsync(b)).Value!.ErrorCategory);
        Assert.False((await repo.SelectOutputAsync(a, SelectionKind.Raw, "x")).IsDeliveryPermitted);
    }

    [Fact]
    public async Task Delivery_transitions_enforced()
    {
        using var d = new TempDir();
        using var repo = await SqliteHistoryRepository.OpenAsync(d.Combine("h.sqlite"));
        var id = await Raw(repo);
        var del = Guid.NewGuid();
        await repo.RecordDeliveryIntentAsync(del, id, "p");
        Assert.True((await repo.RecordDeliveryOutcomeAsync(del, DeliveryOutcome.Copied)).IsDeliveryPermitted);
        Assert.True((await repo.RecordDeliveryOutcomeAsync(del, DeliveryOutcome.CopiedOnly, "secure field")).IsDeliveryPermitted);
        Assert.Equal(HistoryFailureCategory.InvalidTransition, (await repo.RecordDeliveryOutcomeAsync(del, DeliveryOutcome.Dispatched)).Failure!.Category);
        var rec = Assert.Single((await repo.GetAsync(id)).Value!.Deliveries);
        Assert.Equal("secure field", rec.Reason);
    }

    [Fact]
    public async Task Startup_recovery_marks_pending_deliveries_uncertain_and_recognized_interrupted()
    {
        using var d = new TempDir();
        var path = d.Combine("h.sqlite");
        Guid id, del, done;
        using (var repo = await SqliteHistoryRepository.OpenAsync(path))
        {
            id = await Raw(repo);
            del = Guid.NewGuid();
            await repo.RecordDeliveryIntentAsync(del, id, "pending");
            done = Guid.NewGuid();
            await repo.RecordDeliveryIntentAsync(done, id, "done");
            await repo.RecordDeliveryOutcomeAsync(done, DeliveryOutcome.Failed, "x");
        }
        using var reopened = await SqliteHistoryRepository.OpenAsync(path);
        Assert.Equal(new StartupRecoveryResult(1, 1), reopened.StartupRecovery);
        var s = (await reopened.GetAsync(id)).Value!;
        Assert.Equal(HistorySessionStatus.Interrupted, s.Status);
        Assert.NotNull(s.Finished);
        Assert.Equal(DeliveryOutcome.Uncertain, s.Deliveries.Single(x => x.Id == del).Outcome);
        Assert.Equal(DeliveryOutcome.Failed, s.Deliveries.Single(x => x.Id == done).Outcome);
    }

    [Fact]
    public async Task Tombstones_survive_restart()
    {
        using var d = new TempDir();
        var path = d.Combine("h.sqlite");
        Guid id;
        using (var repo = await SqliteHistoryRepository.OpenAsync(path)) { id = await Raw(repo); await repo.DeleteAsync(id); }
        using var again = await SqliteHistoryRepository.OpenAsync(path);
        Assert.False((await again.RecordDeliveryIntentAsync(Guid.NewGuid(), id, "x")).IsDeliveryPermitted);
    }

    [Fact]
    public async Task Newer_schema_refuses_to_open()
    {
        using var d = new TempDir();
        var path = d.Combine("h.sqlite");
        using (var repo = await SqliteHistoryRepository.OpenAsync(path)) { }
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version=9"; cmd.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<HistoryStorageException>(() => SqliteHistoryRepository.OpenAsync(path));
    }

    [Fact]
    public async Task Recovered_row()
    {
        using var d = new TempDir();
        using var repo = await SqliteHistoryRepository.OpenAsync(d.Combine("h.sqlite"));
        var m = Meta();
        Assert.True((await repo.RecordRecoveredAsync(m, "recovered text", null)).IsDeliveryPermitted);
        var s = (await repo.GetAsync(m.Id)).Value!;
        Assert.Equal(SelectionKind.Recovered, s.SelectionKind);
        Assert.Null(s.Raw);
        Assert.Equal(HistorySessionStatus.Completed, s.Status);
    }

    [Fact]
    public async Task History_off_is_in_memory_and_acks_history_off()
    {
        using var repo = new EphemeralHistoryRepository();
        Assert.True(repo.IsEphemeral);
        var m = Meta();
        Assert.Equal(HistoryAckKind.HistoryOff, (await repo.BeginSessionAsync(m)).Kind);
        var ack = await repo.SaveRawAsync(m.Id, "secret");
        Assert.Equal(HistoryAckKind.HistoryOff, ack.Kind);
        Assert.True(ack.IsDeliveryPermitted);
        Assert.Equal(HistoryAckKind.HistoryOff, (await repo.RecordDeliveryIntentAsync(Guid.NewGuid(), m.Id, "secret")).Kind);
        Assert.Single((await repo.QueryAsync(new HistoryQuery("secret"))).Value!.Items);
        Assert.True((await repo.DeleteAsync(m.Id)).IsDeliveryPermitted);
        Assert.False((await repo.SaveRawAsync(m.Id, "x")).IsDeliveryPermitted);
    }

    [Fact]
    public async Task Unavailable_repository_is_not_saved_never_history_off()
    {
        var repo = new UnavailableHistoryRepository("disk");
        var ack = await repo.BeginSessionAsync(Meta());
        Assert.Equal(HistoryAckKind.NotSaved, ack.Kind);
        Assert.False(ack.IsDeliveryPermitted);
    }

    [Fact]
    public async Task Concurrent_writes_are_serialized()
    {
        using var d = new TempDir();
        using var repo = await SqliteHistoryRepository.OpenAsync(d.Combine("h.sqlite"));
        var id = await Raw(repo);
        var acks = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => repo.AppendCleanupAttemptAsync(Attempt(id))));
        Assert.All(acks, a => Assert.True(a.IsDeliveryPermitted));
        Assert.Equal(Enumerable.Range(1, 20), (await repo.GetAsync(id)).Value!.Attempts.Select(x => x.AttemptIndex));
    }
}
