using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remy.Core.Recovery;

/// <summary>A saved cancelled/failed session (metadata JSON beside the WAV). Matches the Mac CancelledDictation fields.</summary>
public sealed record RecoverySession(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    double DurationSeconds,
    string? Preview,
    string? CompletedTranscript,
    bool HasAudio,
    string? CaptureError,
    string? TranscriptPrefix,
    string? TargetExecutable)
{
    /// <summary>True when the metadata could not be written; the entry lives in memory only and the audio file was kept.</summary>
    [JsonIgnore] public bool IsVolatile { get; init; }
    /// <summary>Verbatim warning to show on the row, if any.</summary>
    [JsonIgnore] public string? Warning { get; init; }
}

public sealed record RecoverySaveRequest(
    Guid Id,
    DateTimeOffset CreatedAt,
    long SampleCount,
    string? Preview,
    string? CompletedTranscript,
    string? CaptureError,
    string? TranscriptPrefix,
    string? TargetExecutable,
    double PreviousDurationSeconds = 0);

/// <summary>Entry is null when the session was empty and discarded.</summary>
public sealed record RecoverySaveResult(RecoverySession? Entry, string? Message);

public sealed record RecoveryClearResult(bool BlockedByUse, int Removed, int Failed)
{
    public bool Complete => !BlockedByUse && Failed == 0;
}

/// <summary>
/// Recovery sessions on disk: <c>&lt;uuid&gt;.wav</c> + <c>&lt;uuid&gt;.json</c> in the Recovery folder. 24 hour retention, orphan
/// discovery, in-use exemption. Only UUID-named regular files are ever touched (unknown files and reparse points are preserved).
/// Operations are synchronous and cheap; call from a worker thread if needed. The directory is passed in (RemyPaths-independent).
/// </summary>
public sealed class RecoveryStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    readonly string _dir;
    readonly Func<DateTimeOffset> _clock;
    readonly object _lock = new();
    readonly HashSet<Guid> _inUse = new();
    readonly Dictionary<Guid, RecoverySession> _volatile = new();

    public RecoveryStore(string directory, Func<DateTimeOffset>? clock = null)
    {
        _dir = directory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Directory => _dir;
    public string AudioPath(Guid id) => Path.Combine(_dir, id.ToString("D") + ".wav");
    string MetaPath(Guid id) => Path.Combine(_dir, id.ToString("D") + ".json");

    /// <summary>Create the temp WAV writer for a new session and mark it in use (so it is not mistaken for an orphan).
    /// Throws IOException when the file cannot be created: show <see cref="RecoveryMessages.UnavailablePrefix"/> + message and do not record.</summary>
    public RecoveryAudioWriter CreateWriter(Guid sessionId)
    {
        System.IO.Directory.CreateDirectory(_dir);
        lock (_lock) _inUse.Add(sessionId);
        try { return new RecoveryAudioWriter(AudioPath(sessionId)); }
        catch { lock (_lock) _inUse.Remove(sessionId); throw; }
    }

    /// <summary>Exempt an entry from expiry and Clear All while it is being recovered or continued.</summary>
    public void MarkInUse(Guid id) { lock (_lock) _inUse.Add(id); }
    public void Release(Guid id) { lock (_lock) _inUse.Remove(id); }
    public bool AnyInUse { get { lock (_lock) return _inUse.Count > 0; } }
    bool IsInUse(Guid id) { lock (_lock) return _inUse.Contains(id); }

    /// <summary>Successful session: delete the safety-spool audio and release it.</summary>
    public void DiscardCompleted(Guid id)
    {
        Release(id);
        TryDelete(AudioPath(id)); TryDelete(MetaPath(id));
        lock (_lock) _volatile.Remove(id);
    }

    /// <summary>Convert a closed (or abandoned) spool into a recovery session. Zero samples and no text means nothing to recover: deleted.</summary>
    public RecoverySaveResult Save(RecoverySaveRequest r)
    {
        var audio = AudioPath(r.Id);
        var hasAudio = r.SampleCount > 0 && File.Exists(audio);
        var hasText = !string.IsNullOrWhiteSpace(r.Preview) || !string.IsNullOrWhiteSpace(r.TranscriptPrefix) || !string.IsNullOrWhiteSpace(r.CompletedTranscript);
        if (!hasAudio && !hasText)
        {
            DiscardCompleted(r.Id);
            return new RecoverySaveResult(null, null);
        }
        var entry = new RecoverySession(r.Id, r.CreatedAt, r.CreatedAt + Retention, r.PreviousDurationSeconds + r.SampleCount / 16000.0,
            r.Preview, r.CompletedTranscript, hasAudio, r.CaptureError, r.TranscriptPrefix, r.TargetExecutable);
        if (!string.IsNullOrEmpty(r.CaptureError)) entry = entry with { Warning = RecoveryMessages.PartialPrefix + r.CaptureError };
        Release(r.Id);
        try
        {
            WriteMetadata(entry);
            return new RecoverySaveResult(entry, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var v = entry with { IsVolatile = true, Warning = RecoveryMessages.MetadataNotSaved };
            lock (_lock) _volatile[r.Id] = v;
            return new RecoverySaveResult(v, RecoveryMessages.MetadataNotSaved);
        }
    }

    void WriteMetadata(RecoverySession e)
    {
        System.IO.Directory.CreateDirectory(_dir);
        var tmp = MetaPath(e.Id) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(e, Json));
        File.Move(tmp, MetaPath(e.Id), true);
    }

    /// <summary>Sweep expired entries, discover orphans, and return live entries newest first. Call at launch and when the expiry timer fires.</summary>
    public IReadOnlyList<RecoverySession> List()
    {
        var now = _clock();
        var result = new Dictionary<Guid, RecoverySession>();
        if (System.IO.Directory.Exists(_dir))
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
            {
                if (!TryGuidName(path, out var id) || IsLink(path)) continue;
                RecoverySession? e = null;
                try { e = JsonSerializer.Deserialize<RecoverySession>(File.ReadAllText(path), Json); } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
                if (e == null || e.Id != id)
                {
                    if (!IsInUse(id) && now - File.GetLastWriteTimeUtc(path) > Retention) TryDelete(path);
                    continue;
                }
                if (e.ExpiresAt <= now && !IsInUse(id)) { TryDelete(path); TryDelete(AudioPath(id)); continue; }
                result[id] = e with { HasAudio = IsRegularFile(AudioPath(id)) && new FileInfo(AudioPath(id)).Length > RecoveryAudioWriter.HeaderBytes };
            }
            foreach (var path in System.IO.Directory.EnumerateFiles(_dir, "*.wav"))
            {
                if (!TryGuidName(path, out var id) || IsLink(path) || result.ContainsKey(id) || IsInUse(id) || File.Exists(MetaPath(id))) continue;
                var info = new FileInfo(path);
                var age = now - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (age > Retention) { TryDelete(path); continue; }
                var samples = RecoveryWavReader.CountSamples(path);
                if (samples <= 0) continue; // unreadable or empty, young: leave it for a later sweep
                var created = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                var orphan = new RecoverySession(id, created, created + Retention, samples / 16000.0, RecoveryMessages.InterruptedPreview, null, true,
                    RecoveryMessages.OrphanCaptureError, null, null) { Warning = RecoveryMessages.PartialPrefix + RecoveryMessages.OrphanCaptureError };
                try { WriteMetadata(orphan); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { orphan = orphan with { IsVolatile = true }; }
                result[id] = orphan;
            }
        }
        lock (_lock)
        {
            foreach (var (id, v) in _volatile)
                if (v.ExpiresAt > now || _inUse.Contains(id)) result.TryAdd(id, v);
        }
        return result.Values.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).ToList();
    }

    /// <summary>When the next entry will expire, for the expiry timer. Null when nothing is stored.</summary>
    public DateTimeOffset? NextExpiry() => List().Select(e => (DateTimeOffset?)e.ExpiresAt).Min();

    public bool Delete(Guid id)
    {
        if (IsInUse(id)) return false;
        lock (_lock) _volatile.Remove(id);
        var ok = TryDelete(AudioPath(id));
        return TryDelete(MetaPath(id)) && ok;
    }

    /// <summary>Whole recording as 16 kHz mono floats for "Recover text" / "Continue". Throws FileNotFoundException/InvalidDataException.</summary>
    public float[] LoadSamples(Guid id) => RecoveryWavReader.ReadAll(AudioPath(id));

    /// <summary>Streaming variant (10 s blocks) for the chunked final decoder.</summary>
    public IEnumerable<float[]> ReadBlocks(Guid id, int blockSamples = 160_000) => RecoveryWavReader.ReadBlocks(AudioPath(id), blockSamples);

    /// <summary>Clear All: delete recognized (UUID-named) .wav/.json/.tmp files. Refuses while any entry is in use.</summary>
    public RecoveryClearResult ClearAll()
    {
        if (AnyInUse) return new RecoveryClearResult(true, 0, 0);
        int removed = 0, failed = 0;
        lock (_lock) _volatile.Clear();
        if (System.IO.Directory.Exists(_dir))
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(_dir))
            {
                var name = Path.GetFileName(path);
                var ext = Path.GetExtension(name).ToLowerInvariant();
                var stem = ext == ".tmp" ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(name)) : Path.GetFileNameWithoutExtension(name);
                var okExt = ext is ".wav" or ".json" or ".tmp";
                if (!okExt || !Guid.TryParseExact(stem, "D", out _) || IsLink(path)) continue;
                if (TryDelete(path)) removed++; else failed++;
            }
        }
        return new RecoveryClearResult(false, removed, failed);
    }

    static bool TryGuidName(string path, out Guid id) => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out id);
    static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    static bool IsRegularFile(string path) => File.Exists(path) && !IsLink(path);

    static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
