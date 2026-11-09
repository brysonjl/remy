using System.Security.Cryptography;

namespace Remy.Core.Models;

/// <summary>Released to allow delete/replace again. Hold one while a recognizer or engine has the files loaded.</summary>
public sealed class ModelLease : IDisposable
{
    private readonly Action _release;
    private int _released;
    public string Directory { get; }
    internal ModelLease(string directory, Action release) { Directory = directory; _release = release; }
    public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) _release(); }
}

/// <summary>
/// On-demand model store under a Models directory (settings-models.md 2.4, Windows adaptation).
/// Layout: &lt;root&gt;\&lt;DirectoryName&gt;\ (verified bundle), &lt;root&gt;\.staging\&lt;guid&gt;\ (in flight, always cleaned).
/// Install streams each file while hashing, verifies size + SHA-256 and the exact inventory, then activates by
/// renaming the staging folder. Nothing is ever fetched except by an explicit <see cref="InstallAsync"/>.
/// </summary>
public sealed class ModelStore
{
    private const long FreeSpaceMargin = 64L * 1024 * 1024;
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly Func<string, long?> _freeSpace;
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _leases = new();
    private string? _busyModel;
    private ModelReadiness? _busyReadiness;

    public event Action<string>? Changed;   // model id

    public ModelStore(string modelsRoot, HttpClient http, Func<string, long?>? freeSpace = null)
    {
        _root = modelsRoot;
        _http = http;
        _freeSpace = freeSpace ?? DefaultFreeSpace;
    }

    public string DirectoryFor(ModelManifest m) => Path.Combine(_root, m.DirectoryName);
    private string StagingRoot => Path.Combine(_root, ".staging");

    private static long? DefaultFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch { return null; }
    }

    // ------------------------------------------------------------ state

    /// <summary>Cheap offline check: every file exists with the declared size. Hashes are checked at install and lease time.</summary>
    public bool IsInstalled(ModelManifest m)
    {
        string dir = DirectoryFor(m);
        if (!Directory.Exists(dir)) return false;
        foreach (var f in m.Files)
        {
            var info = new FileInfo(Path.Combine(dir, f.Path));
            if (!info.Exists || info.Length != f.Size) return false;
        }
        return true;
    }

    /// <summary>NotDownloaded / Downloaded, or Downloading(progress) / Verifying / Deleting while an operation runs.</summary>
    public ModelReadiness Inspect(ModelManifest m)
    {
        lock (_sync)
            if (_busyModel == m.Id && _busyReadiness is not null) return _busyReadiness;
        return new ModelReadiness(IsInstalled(m) ? ModelReadinessState.Downloaded : ModelReadinessState.NotDownloaded);
    }

    public bool IsLeased(ModelManifest m)
    {
        lock (_sync) return _leases.TryGetValue(m.Id, out int n) && n > 0;
    }

    // ------------------------------------------------------------ install

    public async Task InstallAsync(ModelManifest m, IProgress<ModelProgress>? progress, CancellationToken ct)
    {
        if (m.Validate() is not null) throw ModelStoreException.InvalidManifest();
        BeginOperation(m, new ModelReadiness(ModelReadinessState.Downloading, null, 0));
        string? staging = null;
        try
        {
            if (IsLeased(m)) throw ModelStoreException.Leased();
            Directory.CreateDirectory(_root);
            long needed = m.TotalBytes + FreeSpaceMargin;
            if (_freeSpace(_root) is long free && free < needed) throw ModelStoreException.NoSpace();

            staging = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            long total = m.TotalBytes, done = 0;
            void Report(ModelPhase phase, long bytes)
            {
                var p = new ModelProgress(bytes, total, phase);
                SetBusy(m, p.Phase == ModelPhase.Verifying
                    ? new ModelReadiness(ModelReadinessState.Verifying, null, 1)
                    : ModelReadiness.Downloading(p.Fraction));
                progress?.Report(p);
            }
            Report(ModelPhase.Downloading, 0);

            foreach (var f in m.Files)
            {
                ct.ThrowIfCancellationRequested();
                string dest = Path.Combine(staging, f.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                long before = done;
                await DownloadFileAsync(f, dest, ct, n => Report(ModelPhase.Downloading, before + n)).ConfigureAwait(false);
                done += f.Size;
            }

            Report(ModelPhase.Verifying, total);
            await VerifyDirectoryAsync(m, staging, ct).ConfigureAwait(false);

            Activate(m, staging);
            staging = null;
        }
        catch (HttpRequestException e)
        {
            throw new ModelStoreException(ModelStoreError.Network, $"The {m.DisplayName} download failed: {e.Message}", e);
        }
        finally
        {
            if (staging is not null) TryDelete(staging);
            TryRemoveEmpty(StagingRoot);
            EndOperation(m);
        }
    }

    private async Task DownloadFileAsync(ModelFile f, string dest, CancellationToken ct, Action<long> onBytes)
    {
        var uri = new Uri(f.Url);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new ModelStoreException(ModelStoreError.Network, "Model downloads must use HTTPS.");
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ModelStoreException(ModelStoreError.Network, $"The model download server returned an invalid response ({(int)resp.StatusCode}).");
        if (resp.Content.Headers.ContentLength is long cl && cl > f.Size)
            throw new ModelStoreException(ModelStoreError.Incomplete, $"The download had an unexpected size ({cl} of {f.Size} bytes).");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buf = new byte[1 << 16];
            int n;
            long lastReport = 0;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                written += n;
                if (written > f.Size)
                    throw new ModelStoreException(ModelStoreError.Incomplete, $"The download had an unexpected size (more than {f.Size} bytes).");
                hash.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                if (written - lastReport >= 256 * 1024) { lastReport = written; onBytes(written); }
            }
        }
        onBytes(written);
        if (written != f.Size)
            throw new ModelStoreException(ModelStoreError.Incomplete, $"The download had an unexpected size ({written} of {f.Size} bytes).");
        if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(f.Sha256, StringComparison.Ordinal))
            throw ModelStoreException.Integrity();
    }

    private void Activate(ModelManifest m, string staging)
    {
        string target = DirectoryFor(m);
        string? aside = null;
        if (Directory.Exists(target))
        {
            if (IsLeased(m)) throw ModelStoreException.Leased();
            aside = staging + ".old";
            Directory.Move(target, aside);
        }
        try { Directory.Move(staging, target); }
        catch
        {
            if (aside is not null && !Directory.Exists(target)) Directory.Move(aside, target);
            throw;
        }
        if (aside is not null) TryDelete(aside);
    }

    // ------------------------------------------------------------ verify

    /// <summary>Full offline re-hash of an installed bundle. Throws <see cref="ModelStoreException"/> when not installed or corrupt.</summary>
    public async Task VerifyAsync(ModelManifest m, CancellationToken ct)
    {
        string dir = DirectoryFor(m);
        if (!Directory.Exists(dir)) throw ModelStoreException.NotInstalled();
        await VerifyDirectoryAsync(m, dir, ct).ConfigureAwait(false);
    }

    private static async Task VerifyDirectoryAsync(ModelManifest m, string dir, CancellationToken ct)
    {
        // Inventory must equal the manifest exactly: no extras, no reparse points.
        var expected = m.Files.Select(f => Path.GetFullPath(Path.Combine(dir, f.Path.Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw ModelStoreException.Integrity();
            if (entry is FileInfo && !expected.Contains(entry.FullName)) throw ModelStoreException.Integrity();
        }
        foreach (var f in m.Files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(Path.Combine(dir, f.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists || info.Length != f.Size) throw ModelStoreException.Integrity();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var s = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
            var buf = new byte[1 << 20];
            int n;
            while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0) hash.AppendData(buf, 0, n);
            if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(f.Sha256, StringComparison.Ordinal))
                throw ModelStoreException.Integrity();
        }
    }

    // ------------------------------------------------------------ lease

    /// <summary>
    /// Acquire a lease before loading the files (blocks delete/replace until disposed). With
    /// <paramref name="verify"/> the whole bundle is re-hashed first (the Mac's "Prepare Model" behavior).
    /// </summary>
    public async Task<ModelLease> AcquireLeaseAsync(ModelManifest m, bool verify, CancellationToken ct)
    {
        if (!IsInstalled(m)) throw ModelStoreException.NotInstalled();
        if (verify) await VerifyAsync(m, ct).ConfigureAwait(false);
        lock (_sync) _leases[m.Id] = _leases.GetValueOrDefault(m.Id) + 1;
        return new ModelLease(DirectoryFor(m), () =>
        {
            lock (_sync) _leases[m.Id] = Math.Max(0, _leases.GetValueOrDefault(m.Id) - 1);
            Changed?.Invoke(m.Id);
        });
    }

    // ------------------------------------------------------------ delete

    /// <summary>Remove the installed bundle. Refused while a lease is held or another operation runs.</summary>
    public Task DeleteAsync(ModelManifest m, CancellationToken ct = default)
    {
        BeginOperation(m, new ModelReadiness(ModelReadinessState.Deleting));
        try
        {
            if (IsLeased(m)) throw ModelStoreException.Leased();
            string dir = DirectoryFor(m);
            if (Directory.Exists(dir))
            {
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw ModelStoreException.Integrity();
                Directory.Delete(dir, true);
            }
            return Task.CompletedTask;
        }
        finally { EndOperation(m); }
    }

    // ------------------------------------------------------------ plumbing

    private void BeginOperation(ModelManifest m, ModelReadiness r)
    {
        lock (_sync)
        {
            if (_busyModel is not null) throw ModelStoreException.Busy();
            _busyModel = m.Id;
            _busyReadiness = r;
        }
        Changed?.Invoke(m.Id);
    }

    private void SetBusy(ModelManifest m, ModelReadiness r)
    {
        lock (_sync) if (_busyModel == m.Id) _busyReadiness = r;
        Changed?.Invoke(m.Id);
    }

    private void EndOperation(ModelManifest m)
    {
        lock (_sync) { _busyModel = null; _busyReadiness = null; }
        Changed?.Invoke(m.Id);
    }

    private static void TryDelete(string dir) { try { Directory.Delete(dir, true); } catch { } }
    private static void TryRemoveEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }
}
