using System.Text;
using System.Text.Json;

namespace Remy.Core.Cleanup;

/// <summary>profiles.json with envelope, 1 MiB cap, optimistic revisions, lock file and atomic replace (cleanup.md §4.3).</summary>
public sealed class CleanupProfileFileRepository(string directory)
{
    public const int SchemaVersion = 1;
    public const long MaxFileBytes = 1_048_576;
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(1);

    public string Directory { get; } = directory;
    public string FilePath => Path.Combine(Directory, "profiles.json");
    private string LockPath => Path.Combine(Directory, ".profiles.lock");

    private sealed record Envelope(int SchemaVersion, CleanupProfileCollection Collection);

    private static bool IsReparsePoint(string path) =>
        (File.Exists(path) || System.IO.Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Missing file -> empty collection (revision 0).</summary>
    public CleanupProfileCollection Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return CleanupProfileCollection.Empty;
            if (IsReparsePoint(FilePath) || IsReparsePoint(Directory)) throw new CleanupProfileException(CleanupProfileError.Storage);
            var len = new FileInfo(FilePath).Length;
            if (len > MaxFileBytes) throw new CleanupProfileException(CleanupProfileError.FileTooLarge);
            var bytes = File.ReadAllBytes(FilePath);
            return Decode(bytes);
        }
        catch (CleanupProfileException) { throw; }
        catch (JsonException) { throw new CleanupProfileException(CleanupProfileError.Storage, "Corrupt profiles file."); }
        catch (IOException) { throw new CleanupProfileException(CleanupProfileError.Storage); }
        catch (UnauthorizedAccessException) { throw new CleanupProfileException(CleanupProfileError.Storage); }
    }

    private static CleanupProfileCollection Decode(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("schemaVersion", out var v) || v.ValueKind != JsonValueKind.Number)
            throw new CleanupProfileException(CleanupProfileError.Storage);
        if (v.GetInt32() != SchemaVersion) throw new CleanupProfileException(CleanupProfileError.UnsupportedSchema);
        var env = doc.RootElement.Deserialize<Envelope>(CleanupProfileJson.Options)
            ?? throw new CleanupProfileException(CleanupProfileError.Storage);
        env.Collection.Validate();
        return env.Collection;
    }

    /// <summary>Compare-and-swap. <paramref name="expected"/> must equal what is on disk; returns <paramref name="next"/>.</summary>
    public CleanupProfileCollection Save(CleanupProfileCollection expected, CleanupProfileCollection next)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (IsReparsePoint(Directory)) throw new CleanupProfileException(CleanupProfileError.Storage);
            using var lockStream = AcquireLock();

            var current = Load();
            if (CleanupProfileJson.Serialize(current) != CleanupProfileJson.Serialize(expected))
                throw new CleanupProfileException(CleanupProfileError.Conflict);
            ValidateTransition(current, next);

            var json = JsonSerializer.SerializeToUtf8Bytes(new Envelope(SchemaVersion, next), CleanupProfileJson.Options);
            if (json.Length > MaxFileBytes) throw new CleanupProfileException(CleanupProfileError.FileTooLarge);

            var tmp = Path.Combine(Directory, $".profiles-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(json);
                    fs.Flush(true);
                }
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, ignoreMetadataErrors: true);
                else File.Move(tmp, FilePath);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
                throw;
            }
            return next;
        }
        catch (CleanupProfileException) { throw; }
        catch (IOException) { throw new CleanupProfileException(CleanupProfileError.Storage); }
        catch (UnauthorizedAccessException) { throw new CleanupProfileException(CleanupProfileError.Storage); }
    }

    private FileStream AcquireLock()
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try { return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(20); }
            catch (IOException) { throw new CleanupProfileException(CleanupProfileError.Storage, "Profiles are locked."); }
        }
    }

    internal static void ValidateTransition(CleanupProfileCollection old, CleanupProfileCollection next)
    {
        if (old.Revision == ulong.MaxValue) throw new CleanupProfileException(CleanupProfileError.RevisionExhausted);
        next.Validate();
        if (next.Revision != old.Revision + 1) throw new CleanupProfileException(CleanupProfileError.Conflict);
        foreach (var p in next.Profiles)
        {
            var prev = old.Profiles.FirstOrDefault(o => o.Id == p.Id);
            if (prev is null) { if (p.Revision != 1) throw new CleanupProfileException(CleanupProfileError.InvalidProfile); }
            else if (p != prev)
            {
                if (prev.Revision == ulong.MaxValue) throw new CleanupProfileException(CleanupProfileError.RevisionExhausted);
                if (p.Revision != prev.Revision + 1) throw new CleanupProfileException(CleanupProfileError.InvalidProfile);
            }
        }
        if (!old.CompletedLegacyMigrations.All(next.CompletedLegacyMigrations.Contains))
            throw new CleanupProfileException(CleanupProfileError.InvalidProfile);
    }
}
