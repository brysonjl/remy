using System.Net;
using System.Security.Cryptography;
using System.Text;
using Remy.Core.Models;
using Xunit;

namespace Remy.Core.Tests.Models;

public sealed class ModelStoreTests : IDisposable
{
    private const string Repo = "owner/test-model";
    private const string Rev = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remy-models-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static readonly byte[] A = Encoding.UTF8.GetBytes("alpha file content");
    private static readonly byte[] B = new byte[300_000].Select((_, i) => (byte)(i * 7)).ToArray();

    private static ModelManifest Manifest(byte[]? shaOfB = null) => new(
        "test", "Test Model", "test-model", Repo, Rev, "CC-BY-4.0",
        new[]
        {
            new ModelFile("a.txt", A.Length, Sha(A), ModelManifest.UrlFor(Repo, Rev, "a.txt")),
            new ModelFile("sub/b.bin", B.Length, Sha(shaOfB ?? B), ModelManifest.UrlFor(Repo, Rev, "sub/b.bin")),
        });

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<string, byte[]?> Body = _ => null;
        public Func<string, Task>? Gate;
        public List<string> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath.Split(Rev + "/")[1];
            Requests.Add(path);
            if (Gate is not null) await Gate(path);
            var body = Body(path);
            if (body is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(body, Gate is not null, ct)) };
        }
    }

    private sealed class SlowStream : MemoryStream
    {
        private readonly bool _slow; private readonly CancellationToken _ct;
        public SlowStream(byte[] b, bool slow, CancellationToken ct) : base(b) { _slow = slow; _ct = ct; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_slow) await Task.Delay(5, ct);
            return await base.ReadAsync(buffer.Length > 65536 ? buffer[..65536] : buffer, ct);
        }
    }

    private static byte[]? Serve(string path) => path switch { "a.txt" => A, "sub/b.bin" => B, _ => null };

    private ModelStore Store(FakeHandler h, Func<string, long?>? free = null) => new(_root, new HttpClient(h), free);

    [Fact]
    public async Task Install_downloads_verifies_activates_and_reports_progress()
    {
        var h = new FakeHandler { Body = Serve };
        var store = Store(h);
        var m = Manifest();
        Assert.Equal("Not downloaded", store.Inspect(m).Title);
        var reports = new List<ModelProgress>();
        await store.InstallAsync(m, new SyncProgress(reports),CancellationToken.None);

        Assert.True(store.IsInstalled(m));
        Assert.Equal("Downloaded", store.Inspect(m).Title);
        Assert.Equal(B, File.ReadAllBytes(Path.Combine(_root, "test-model", "sub", "b.bin")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".staging")));
        Assert.Equal(ModelPhase.Verifying, reports[^1].Phase);
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.Contains(reports, r => r.DownloadedBytes > A.Length && r.Phase == ModelPhase.Downloading);
        await store.VerifyAsync(m, CancellationToken.None);
    }

    private sealed class SyncProgress(List<ModelProgress> l) : IProgress<ModelProgress>
    {
        public void Report(ModelProgress v) => l.Add(v);
    }

    [Fact]
    public async Task Hash_mismatch_activates_nothing_and_cleans_staging()
    {
        var h = new FakeHandler { Body = Serve };
        var store = Store(h);
        var m = Manifest(shaOfB: new byte[] { 1, 2, 3 });   // declared hash does not match served bytes
        var e = await Assert.ThrowsAsync<ModelStoreException>(() => store.InstallAsync(m, null, CancellationToken.None));
        Assert.Equal(ModelStoreError.IntegrityMismatch, e.Kind);
        Assert.False(Directory.Exists(Path.Combine(_root, "test-model")));
        Assert.False(store.IsInstalled(m));
        Assert.False(Directory.Exists(Path.Combine(_root, ".staging")));
    }

    [Fact]
    public async Task Hash_mismatch_during_repair_keeps_previous_install()
    {
        var store0 = Store(new FakeHandler { Body = Serve });
        await store0.InstallAsync(Manifest(), null, CancellationToken.None);
        var bad = Store(new FakeHandler { Body = p => p == "a.txt" ? Encoding.UTF8.GetBytes("ALPHA file content") : Serve(p) });
        await Assert.ThrowsAsync<ModelStoreException>(() => bad.InstallAsync(Manifest(), null, CancellationToken.None));
        Assert.True(store0.IsInstalled(Manifest()));
        await store0.VerifyAsync(Manifest(), CancellationToken.None);
    }

    [Fact]
    public async Task Wrong_size_and_http_error_fail_without_activation()
    {
        var store = Store(new FakeHandler { Body = p => p == "a.txt" ? A : B[..1000] });
        var e = await Assert.ThrowsAsync<ModelStoreException>(() => store.InstallAsync(Manifest(), null, CancellationToken.None));
        Assert.Equal(ModelStoreError.Incomplete, e.Kind);

        var store404 = Store(new FakeHandler { Body = _ => null });
        var e2 = await Assert.ThrowsAsync<ModelStoreException>(() => store404.InstallAsync(Manifest(), null, CancellationToken.None));
        Assert.Equal(ModelStoreError.Network, e2.Kind);
        Assert.False(Directory.Exists(Path.Combine(_root, "test-model")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".staging")));
    }

    [Fact]
    public async Task Cancel_cleans_staging_and_activates_nothing()
    {
        using var cts = new CancellationTokenSource();
        var h = new FakeHandler { Body = Serve, Gate = path => { if (path == "sub/b.bin") cts.Cancel(); return Task.CompletedTask; } };
        var store = Store(h);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InstallAsync(Manifest(), null, cts.Token));
        Assert.False(Directory.Exists(Path.Combine(_root, "test-model")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".staging")));
        Assert.Equal("Not downloaded", store.Inspect(Manifest()).Title);
        // The store is usable again afterwards.
        await Store(new FakeHandler { Body = Serve }).InstallAsync(Manifest(), null, CancellationToken.None);
    }

    [Fact]
    public async Task Second_operation_while_busy_is_rejected()
    {
        var release = new TaskCompletionSource();
        var h = new FakeHandler { Body = Serve, Gate = _ => release.Task };
        var store = Store(h);
        var first = store.InstallAsync(Manifest(), null, CancellationToken.None);
        var e = await Assert.ThrowsAsync<ModelStoreException>(() => store.DeleteAsync(Manifest()));
        Assert.Equal("Another model operation is running.", e.Message);
        Assert.Equal("Downloading…", store.Inspect(Manifest()).Title);
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task Delete_refused_while_leased_and_allowed_after_release()
    {
        var store = Store(new FakeHandler { Body = Serve });
        var m = Manifest();
        await store.InstallAsync(m, null, CancellationToken.None);
        var lease = await store.AcquireLeaseAsync(m, verify: true, CancellationToken.None);
        Assert.True(store.IsLeased(m));
        var e = await Assert.ThrowsAsync<ModelStoreException>(() => store.DeleteAsync(m));
        Assert.Equal("Unload the model before switching or removing its assets.", e.Message);
        Assert.True(store.IsInstalled(m));
        lease.Dispose();
        lease.Dispose();
        await store.DeleteAsync(m);
        Assert.False(store.IsInstalled(m));
        Assert.False(Directory.Exists(Path.Combine(_root, "test-model")));
    }

    [Fact]
    public async Task Lease_detects_corruption_and_missing_install()
    {
        var store = Store(new FakeHandler { Body = Serve });
        var m = Manifest();
        await Assert.ThrowsAsync<ModelStoreException>(() => store.AcquireLeaseAsync(m, true, CancellationToken.None));
        await store.InstallAsync(m, null, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(_root, "test-model", "a.txt"), Encoding.UTF8.GetBytes("ALPHA file content"));
        var e = await Assert.ThrowsAsync<ModelStoreException>(() => store.AcquireLeaseAsync(m, true, CancellationToken.None));
        Assert.Equal(ModelStoreError.IntegrityMismatch, e.Kind);
        Assert.False(store.IsLeased(m));
    }

    [Fact]
    public async Task Insufficient_space_fails_before_any_request()
    {
        var h = new FakeHandler { Body = Serve };
        var store = Store(h, free: _ => 1000);
        var e = await Assert.ThrowsAsync<ModelStoreException>(() => store.InstallAsync(Manifest(), null, CancellationToken.None));
        Assert.Equal(ModelStoreError.InsufficientSpace, e.Kind);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void Catalog_manifests_are_valid_and_pinned()
    {
        foreach (var m in ModelCatalog.All) Assert.Null(m.Validate());
        Assert.Equal(652282309L + 7257753 + 1739080 + 9384, ModelCatalog.ParakeetV2.TotalBytes);
        Assert.Contains(ModelCatalog.S1Mini.Files, f => f.Size == 484219808 &&
            f.Sha256 == "3b41ebe2502cbd03e811d5d16b022f5ab551eda58d62597d152f89535003c634");
        Assert.All(ModelCatalog.ParakeetV2.Files, f => Assert.StartsWith(
            "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/84b478d541cdbf0c296a7ab5f1e20a6f8f998114/", f.Url));
    }

    [Fact]
    public void Manifest_validation_rejects_bad_input()
    {
        var m = Manifest();
        Assert.NotNull((m with { Revision = "main" }).Validate());
        Assert.NotNull((m with { Files = new[] { m.Files[0] with { Path = "../x" } } }).Validate());
        Assert.NotNull((m with { Files = new[] { m.Files[0] with { Url = "https://evil.example/a.txt" } } }).Validate());
        Assert.NotNull((m with { Files = new[] { m.Files[0] with { Sha256 = "ABC" } } }).Validate());
        Assert.NotNull((m with { Files = new[] { m.Files[0], m.Files[0] } }).Validate());
    }

    [Fact]
    public void Readiness_titles_and_progress_caption_are_verbatim()
    {
        Assert.Equal("Needs attention", new ModelReadiness(ModelReadinessState.Failed, "x").Title);
        Assert.Equal("Preparing for first use…", new ModelReadiness(ModelReadinessState.Preparing).Title);
        Assert.Null(ModelReadiness.Downloading(double.NaN).Progress);
        Assert.Equal(1.0, ModelReadiness.Downloading(7).Progress);
        long mib = 1024 * 1024;
        var p = new ModelProgress(100 * mib, 484219808, ModelPhase.Downloading);
        Assert.Equal("21% · 100 of 462 MB", p.Caption);
        Assert.Equal("Verifying model integrity…", new ModelProgress(484219808, 484219808, ModelPhase.Downloading).Caption);
    }
}
