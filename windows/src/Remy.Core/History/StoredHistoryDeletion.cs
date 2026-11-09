using Remy.Core.Recovery;

namespace Remy.Core.History;

public sealed record ClearAllResult(bool Complete, string? Message);

/// <summary>
/// Clear All (settings-models.md 7, pipeline.md 6.3): rows (generation++ and tombstones), recovery files, and recognized
/// rollback backups. Never touches the open database file, models, settings or credentials. Not retried automatically.
/// The caller must cancel any in-flight dictation first.
/// </summary>
public static class StoredHistoryDeletion
{
    public static async Task<ClearAllResult> ClearAllAsync(IHistoryRepository history, RecoveryStore recovery, string historyDirectory, CancellationToken ct = default)
    {
        if (recovery.AnyInUse) return new ClearAllResult(false, HistoryMessages.ClearBlockedByRecovery);
        var ack = await history.ClearAllAsync(ct).ConfigureAwait(false);
        var ok = ack.Kind != HistoryAckKind.NotSaved;
        var rec = recovery.ClearAll();
        if (rec.BlockedByUse) return new ClearAllResult(false, HistoryMessages.ClearBlockedByRecovery);
        ok &= rec.Complete;
        ok &= DeleteBackups(historyDirectory);
        return new ClearAllResult(ok, ok ? null : HistoryMessages.ClearPartialFailure);
    }

    /// <summary>history-before-v2-&lt;uuid&gt;.sqlite and Legacy Backups\legacy-history-&lt;sha256&gt;.json only; unknown files and links stay.</summary>
    static bool DeleteBackups(string dir)
    {
        var ok = true;
        try
        {
            if (!Directory.Exists(dir)) return true;
            foreach (var f in Directory.EnumerateFiles(dir, "history-before-v2-*.sqlite"))
            {
                var stem = Path.GetFileNameWithoutExtension(f)["history-before-v2-".Length..];
                if (Guid.TryParse(stem, out _)) ok &= Delete(f);
            }
            var legacy = Path.Combine(dir, "Legacy Backups");
            if (Directory.Exists(legacy))
                foreach (var f in Directory.EnumerateFiles(legacy, "legacy-history-*.json")) ok &= Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
        return ok;
    }

    static bool Delete(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
