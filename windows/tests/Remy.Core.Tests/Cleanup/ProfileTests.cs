using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.Settings;
using Xunit;

namespace Remy.Core.Tests.Cleanup;

public sealed class MemoryCredentialStore : ICredentialStore
{
    public Dictionary<(string, string), string> Items { get; } = new();
    public string? Read(string service, string account) => Items.GetValueOrDefault((service, account));
    public void Write(string service, string account, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) Items.Remove((service, account)); else Items[(service, account)] = secret;
    }
    public void Delete(string service, string account) => Items.Remove((service, account));
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remy-prof-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { /* ignore */ } }
}

public class EndpointTests
{
    [Theory]
    [InlineData("https://Ollama.com/v1", "https://ollama.com/v1")]
    [InlineData("https://example.com:443/v1/", "https://example.com/v1")]
    [InlineData("http://localhost:11434", "http://localhost:11434")]
    [InlineData("http://127.0.0.1:80/", "http://127.0.0.1")]
    [InlineData("https://example.com:8443", "https://example.com:8443")]
    public void Canonicalizes(string input, string canonical)
    {
        Assert.True(CleanupEndpointIdentity.TryCreate(input, false, out var id));
        Assert.Equal(canonical, id!.Canonical);
    }

    [Theory]
    [InlineData(" https://example.com")]
    [InlineData("https://example.com ")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:pw@example.com")]
    [InlineData("https://example.com/v1?x=1")]
    [InlineData("https://example.com/v1#frag")]
    [InlineData("https://example.com/a/../b")]
    [InlineData("https://example.com/./b")]
    [InlineData("http://example.com")]
    [InlineData("http://192.168.1.2:1234/v1")]
    [InlineData("https://example.com:0")]
    [InlineData("")]
    [InlineData("example.com")]
    public void Rejects(string input) => Assert.False(CleanupEndpointIdentity.TryCreate(input, false, out _));

    [Fact]
    public void InsecureRemoteHttpOnlyWhenAllowed()
    {
        Assert.True(CleanupEndpointIdentity.TryCreate("http://192.168.1.2:1234/v1", true, out var id));
        Assert.False(id!.IsLoopback);
        Assert.True(CleanupEndpointIdentity.TryCreate("http://[::1]:11434", false, out var v6));
        Assert.True(v6!.IsLoopback);
    }

    [Fact]
    public void CredentialAccountNameIsEndpointScoped()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var a = CleanupProfileCredentials.Account(new CleanupCredentialReference(id, "https://a.com/v1"));
        var b = CleanupProfileCredentials.Account(new CleanupCredentialReference(id, "https://b.com/v1"));
        Assert.StartsWith("11111111-2222-3333-4444-555555555555.", a);
        Assert.Equal(36 + 1 + 64, a.Length);
        Assert.NotEqual(a, b);
        Assert.Equal("io.github.brysonjl.remy.cleanup-profiles", CleanupProfileCredentials.Service);
    }
}

public class ProfileRepositoryTests
{
    private static CleanupProfile NewProfile(string model = "gemma4:31b") =>
        CleanupProfile.CreateNew() with { Model = model };

    [Fact]
    public void ControllerRoundTrip()
    {
        using var dir = new TempDir();
        var store = new MemoryCredentialStore();
        var c = new CleanupProfileController(new CleanupProfileFileRepository(dir.Path), store);
        Assert.Empty(c.Load().Profiles);

        var saved = c.Save(NewProfile(), "sk-secret");
        Assert.Equal(1UL, saved.Revision);
        Assert.NotNull(saved.Credential);
        Assert.Equal("https://ollama.com/v1", saved.Credential!.Endpoint);
        Assert.Equal("sk-secret", store.Read(CleanupProfileCredentials.Service, CleanupProfileCredentials.Account(saved.Credential)));
        Assert.Equal(saved.Id, c.Collection.SelectedId);

        // reload from disk with a fresh controller
        var c2 = new CleanupProfileController(new CleanupProfileFileRepository(dir.Path), store);
        var loaded = c2.Load();
        var p = Assert.Single(loaded.Profiles);
        Assert.Equal(saved, p);
        Assert.Equal("sk-secret", c2.ResolveApiKey(p));
        Assert.Contains("\"schemaVersion\":1", File.ReadAllText(Path.Combine(dir.Path, "profiles.json")));
        Assert.Contains("\"adapter\":\"openAICompatible\"", File.ReadAllText(Path.Combine(dir.Path, "profiles.json")));

        // edit bumps revisions; prompt change bumps promptVersion
        var edited = c2.Save(p with { Name = "Renamed", Prompt = "New prompt" });
        Assert.Equal(2UL, edited.Revision);
        Assert.Equal(2, edited.PromptVersion);
        Assert.Equal(p.Credential, edited.Credential);
        Assert.Equal(2UL, c2.Collection.Revision);
        Assert.Single(c2.Collection.Profiles);
    }

    [Fact]
    public void SelectAndRemove()
    {
        using var dir = new TempDir();
        var c = new CleanupProfileController(new CleanupProfileFileRepository(dir.Path), new MemoryCredentialStore());
        c.Load();
        var a = c.Save(NewProfile("a"));
        var b = c.Save(CleanupProfile.CreateNew() with { Model = "b", Name = "B" });
        Assert.Equal(b.Id, c.Collection.SelectedId);
        c.Select(a.Id);
        Assert.Equal(a.Id, c.Collection.SelectedId);
        c.Remove(a.Id);
        Assert.Equal(b.Id, c.Collection.SelectedId);
        c.Remove(b.Id);
        Assert.Null(c.Collection.SelectedId);
        Assert.Empty(c.Collection.Profiles);
        Assert.Equal(CleanupProfileError.MissingProfile, Assert.Throws<CleanupProfileException>(() => c.Remove(b.Id)).Error);
    }

    [Fact]
    public void StaleWriterGetsConflict()
    {
        using var dir = new TempDir();
        var store = new MemoryCredentialStore();
        var c1 = new CleanupProfileController(new CleanupProfileFileRepository(dir.Path), store);
        var c2 = new CleanupProfileController(new CleanupProfileFileRepository(dir.Path), store);
        c1.Load(); c2.Load();
        c1.Save(NewProfile());
        var ex = Assert.Throws<CleanupProfileException>(() => c2.Save(NewProfile("other")));
        Assert.Equal(CleanupProfileError.Conflict, ex.Error);
        c2.Load();
        c2.Save(NewProfile("other"));
        Assert.Equal(2, c2.Collection.Profiles.Count);
    }

    [Fact]
    public void RepositoryRejectsBadTransitions()
    {
        using var dir = new TempDir();
        var repo = new CleanupProfileFileRepository(dir.Path);
        var empty = repo.Load();
        var p = NewProfile();
        // wrong collection revision
        Assert.Equal(CleanupProfileError.Conflict, Assert.Throws<CleanupProfileException>(() =>
            repo.Save(empty, new CleanupProfileCollection(5, [p], p.Id, []))).Error);
        // new profile must have revision 1
        Assert.Equal(CleanupProfileError.InvalidProfile, Assert.Throws<CleanupProfileException>(() =>
            repo.Save(empty, new CleanupProfileCollection(1, [p with { Revision = 3 }], p.Id, []))).Error);
        // selected must exist
        Assert.Equal(CleanupProfileError.InvalidProfile, Assert.Throws<CleanupProfileException>(() =>
            repo.Save(empty, new CleanupProfileCollection(1, [p], Guid.NewGuid(), []))).Error);
        var one = repo.Save(empty, new CleanupProfileCollection(1, [p], p.Id, ["m1"]));
        // migrations must stay a superset
        Assert.Equal(CleanupProfileError.InvalidProfile, Assert.Throws<CleanupProfileException>(() =>
            repo.Save(one, new CleanupProfileCollection(2, [p], p.Id, []))).Error);
        // changed profile must bump its revision by exactly one
        Assert.Equal(CleanupProfileError.InvalidProfile, Assert.Throws<CleanupProfileException>(() =>
            repo.Save(one, new CleanupProfileCollection(2, [p with { Name = "x" }], p.Id, ["m1"]))).Error);
    }

    [Fact]
    public void ValidationRules()
    {
        var p = NewProfile();
        Assert.Throws<CleanupProfileException>(() => (p with { Name = " " }).Validate());
        Assert.Throws<CleanupProfileException>(() => (p with { Model = "" }).Validate());
        Assert.Throws<CleanupProfileException>(() => (p with { TimeoutSeconds = 0.5 }).Validate());
        Assert.Throws<CleanupProfileException>(() => (p with { TimeoutSeconds = 121 }).Validate());
        Assert.Throws<CleanupProfileException>(() => (p with { Adapter = CleanupProfileAdapter.Ollama,
            Capabilities = CleanupProfileCapabilities.Default with { Transport = CleanupTransport.Sse } }).Validate());
        Assert.Throws<CleanupProfileException>(() => (p with { Name = new string('n', 257) }).Validate());
        var wrongRef = new CleanupCredentialReference(Guid.NewGuid(), "https://other.com/v1");
        Assert.Equal(CleanupProfileError.InvalidCredentialReference,
            Assert.Throws<CleanupProfileException>(() => (p with { Credential = wrongRef }).Validate()).Error);
    }

    [Fact]
    public void EndpointChangeWithKeptReferenceIsRejected()
    {
        using var dir = new TempDir();
        var c = new CleanupProfileController(new CleanupProfileFileRepository(dir.Path), new MemoryCredentialStore());
        c.Load();
        var saved = c.Save(NewProfile(), "k");
        Assert.Throws<CleanupProfileException>(() => c.Save(saved with { BaseUrl = "https://elsewhere.com/v1" }));
        var moved = c.Save(saved with { BaseUrl = "https://elsewhere.com/v1", Credential = null });
        Assert.Null(moved.Credential);
    }

    [Fact]
    public void FileSizeCapAndSchemaGate()
    {
        using var dir = new TempDir();
        var repo = new CleanupProfileFileRepository(dir.Path);
        File.WriteAllText(repo.FilePath, new string(' ', (int)CleanupProfileFileRepository.MaxFileBytes + 1));
        Assert.Equal(CleanupProfileError.FileTooLarge, Assert.Throws<CleanupProfileException>(() => repo.Load()).Error);
        File.WriteAllText(repo.FilePath, "{\"schemaVersion\":2,\"collection\":{}}");
        Assert.Equal(CleanupProfileError.UnsupportedSchema, Assert.Throws<CleanupProfileException>(() => repo.Load()).Error);
        File.WriteAllText(repo.FilePath, "garbage");
        Assert.Equal(CleanupProfileError.Storage, Assert.Throws<CleanupProfileException>(() => repo.Load()).Error);
    }

    [Fact]
    public void RemoveKeyAndRuntimeBuilding()
    {
        using var dir = new TempDir();
        var store = new MemoryCredentialStore();
        var c = new CleanupProfileController(new CleanupProfileFileRepository(dir.Path), store);
        c.Load();
        var saved = c.Save(NewProfile() with { Capabilities = new(CleanupTransport.CompleteJSON, true, true, false) }, "k");
        var policy = new ContextPolicy(true, false, new Dictionary<DictationContextCategory, DictationWritingStyle>(), S1MiniAppStyling.Recommended, []);
        var rt = c.BuildRuntime(saved, policy);
        Assert.Equal(TranscriptPostProcessingMode.Profile, rt.Mode);
        Assert.Equal("k", rt.ApiKey);
        Assert.True(rt.StructuredOutput);
        Assert.True(rt.Discovery);
        Assert.False(rt.ThinkingControl);
        Assert.Equal(TimeSpan.FromSeconds(30), rt.RequestTimeout);

        var cleared = c.Save(saved, removeKey: true);
        Assert.Null(cleared.Credential);
        Assert.Empty(store.Items);
        Assert.Null(c.BuildRuntime(cleared, policy).ApiKey);

        // a reference whose secret vanished is an authentication failure
        var again = c.Save(cleared, "k2");
        store.Items.Clear();
        Assert.Equal("authentication", Assert.Throws<CleanupFailureException>(() => c.BuildRuntime(again, policy)).Failure.Category);
        Assert.Equal("invalidConfiguration", Assert.Throws<CleanupFailureException>(() =>
            c.BuildRuntime(again with { RoutePolicy = CleanupRoutePolicy.StrictLocalOnly }, policy)).Failure.Category);
    }
}
