using Remy.Core.History;
using Remy.Core.Recovery;
using Xunit;

namespace Remy.Core.Tests.History;

public class RecoveryTests
{
    static float[] Tone(int n) => Enumerable.Range(0, n).Select(i => (float)Math.Sin(i / 20.0) * 0.5f).ToArray();

    [Fact]
    public async Task Writer_roundtrips_16k_mono_wav()
    {
        using var d = new TempDir();
        var store = new RecoveryStore(d.Combine("Recovery"));
        var id = Guid.NewGuid();
        var w = store.CreateWriter(id);
        var samples = Tone(40000);
        Assert.True(w.TryEnqueue(samples.AsMemory(0, 10000)));
        Assert.True(w.TryEnqueue(samples.AsMemory(10000)));
        var res = await w.CompleteAsync();
        Assert.Null(res.FailureMessage);
        Assert.Equal(40000, res.SamplesWritten);
        Assert.Equal(44 + 80000, new FileInfo(w.Path).Length);
        var back = store.LoadSamples(id);
        Assert.Equal(40000, back.Length);
        Assert.All(back.Zip(samples), p => Assert.InRange(Math.Abs(p.First - p.Second), 0, 1e-3));
        Assert.Equal(40000, store.ReadBlocks(id, 16000).Sum(b => b.Length));
    }

    [Fact]
    public async Task Writer_overflow_sets_sticky_failure_and_raises_event()
    {
        using var d = new TempDir();
        var w = new RecoveryAudioWriter(d.Combine("a.wav"), maxQueuedSamples: 1000);
        string? raised = null;
        w.Failed += m => raised = m;
        var ok = w.TryEnqueue(new float[5000]);
        Assert.False(ok);
        Assert.Equal(RecoveryMessages.WriterOverflow, raised);
        Assert.False(w.TryEnqueue(new float[10]));
        var res = await w.CompleteAsync();
        Assert.Equal(RecoveryMessages.WriterOverflow, res.FailureMessage);
    }

    [Fact]
    public async Task Save_list_load_and_delete()
    {
        using var d = new TempDir();
        var now = DateTimeOffset.UtcNow;
        var store = new RecoveryStore(d.Combine("R"), () => now);
        var id = Guid.NewGuid();
        var w = store.CreateWriter(id);
        w.TryEnqueue(Tone(32000));
        var res = await w.CompleteAsync();
        var save = store.Save(new RecoverySaveRequest(id, now, res.SamplesWritten, "preview text", null, null, "prefix", "slack.exe", 3));
        Assert.NotNull(save.Entry);
        var list = store.List();
        var e = Assert.Single(list);
        Assert.True(e.HasAudio);
        Assert.Equal(5.0, e.DurationSeconds, 3);
        Assert.Equal("preview text", e.Preview);
        Assert.Equal(now + TimeSpan.FromHours(24), e.ExpiresAt);
        Assert.Equal(32000, store.LoadSamples(id).Length);
        Assert.True(store.Delete(id));
        Assert.Empty(store.List());
        Assert.False(File.Exists(store.AudioPath(id)));
    }

    [Fact]
    public void Empty_session_is_discarded()
    {
        using var d = new TempDir();
        var store = new RecoveryStore(d.Combine("R"));
        var id = Guid.NewGuid();
        store.CreateWriter(id).Dispose();
        var r = store.Save(new RecoverySaveRequest(id, DateTimeOffset.UtcNow, 0, null, null, null, null, null));
        Assert.Null(r.Entry);
        Assert.False(File.Exists(store.AudioPath(id)));
    }

    [Fact]
    public async Task Entries_expire_after_24_hours_unless_in_use()
    {
        using var d = new TempDir();
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var store = new RecoveryStore(d.Combine("R"), () => clock);
        var id = Guid.NewGuid();
        var w = store.CreateWriter(id);
        w.TryEnqueue(Tone(16000));
        store.Save(new RecoverySaveRequest(id, now, (await w.CompleteAsync()).SamplesWritten, "p", null, null, null, null));
        clock = now.AddHours(23);
        Assert.Single(store.List());
        clock = now.AddHours(25);
        store.MarkInUse(id);
        Assert.Single(store.List());
        Assert.False(store.Delete(id));
        store.Release(id);
        Assert.Empty(store.List());
        Assert.False(File.Exists(store.AudioPath(id)));
    }

    [Fact]
    public async Task Orphan_audio_is_discovered_and_old_orphans_deleted()
    {
        using var d = new TempDir();
        var dir = d.Combine("R");
        var id = Guid.NewGuid();
        var spool = new RecoveryStore(dir);
        var w = spool.CreateWriter(id);
        w.TryEnqueue(Tone(16000));
        await w.CompleteAsync();   // no Save: simulates a crash leaving the wav behind
        var store = new RecoveryStore(dir);
        var e = Assert.Single(store.List());
        Assert.Equal(RecoveryMessages.InterruptedPreview, e.Preview);
        Assert.Equal(RecoveryMessages.OrphanCaptureError, e.CaptureError);
        Assert.True(File.Exists(Path.Combine(dir, id.ToString("D") + ".json")));

        var old = Guid.NewGuid();
        var oldPath = Path.Combine(dir, old.ToString("D") + ".wav");
        File.Copy(spool.AudioPath(id), oldPath);
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddHours(-30));
        Assert.Single(store.List());
        Assert.False(File.Exists(oldPath));
    }

    [Fact]
    public async Task Unpatched_header_after_crash_still_reads()
    {
        using var d = new TempDir();
        var dir = d.Combine("R");
        var store = new RecoveryStore(dir);
        var id = Guid.NewGuid();
        var w = store.CreateWriter(id);
        w.TryEnqueue(Tone(8000));
        await w.CompleteAsync();
        using (var f = new FileStream(store.AudioPath(id), FileMode.Open, FileAccess.Write)) RecoveryAudioWriter.WriteHeader(f, 0);
        Assert.Equal(8000, store.LoadSamples(id).Length);
    }

    [Fact]
    public void Clear_all_removes_recognized_files_only_and_blocks_while_in_use()
    {
        using var d = new TempDir();
        var dir = d.Combine("R");
        var store = new RecoveryStore(dir);
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir, id + ".wav"), "x");
        File.WriteAllText(Path.Combine(dir, id + ".json"), "{}");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "keep");
        store.MarkInUse(id);
        Assert.True(store.ClearAll().BlockedByUse);
        store.Release(id);
        var res = store.ClearAll();
        Assert.True(res.Complete);
        Assert.Equal(2, res.Removed);
        Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
    }

    [Fact]
    public async Task StoredHistoryDeletion_clears_rows_recovery_and_backups()
    {
        using var d = new TempDir();
        var hdir = d.Combine("History");
        using var repo = await SqliteHistoryRepository.OpenAsync(Path.Combine(hdir, "history.sqlite"));
        var m = new HistorySessionMetadata(Guid.NewGuid(), DateTimeOffset.UtcNow, "e", "m");
        await repo.BeginSessionAsync(m);
        await repo.SaveRawAsync(m.Id, "text");
        var store = new RecoveryStore(d.Combine("Recovery"));
        var rid = Guid.NewGuid();
        var w = store.CreateWriter(rid);
        w.TryEnqueue(Tone(16000));
        store.Save(new RecoverySaveRequest(rid, DateTimeOffset.UtcNow, (await w.CompleteAsync()).SamplesWritten, "p", null, null, null, null));
        var backup = Path.Combine(hdir, $"history-before-v2-{Guid.NewGuid()}.sqlite");
        File.WriteAllText(backup, "x");
        var unknown = Path.Combine(hdir, "other.txt");
        File.WriteAllText(unknown, "x");

        var result = await StoredHistoryDeletion.ClearAllAsync(repo, store, hdir);
        Assert.True(result.Complete, result.Message);
        Assert.Empty((await repo.QueryAsync(new HistoryQuery())).Value!.Items);
        Assert.Empty(store.List());
        Assert.False(File.Exists(backup));
        Assert.True(File.Exists(unknown));
        Assert.True(File.Exists(Path.Combine(hdir, "history.sqlite")));
    }
}
