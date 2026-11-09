using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Remy.Core.Settings;

namespace Remy.Core.Cleanup;

public enum CleanupProfileError
{
    Conflict, RevisionExhausted, StateUnknown, Storage, CredentialUnavailable, InvalidProfile,
    InvalidCredentialReference, InvalidCredentialValue, MissingProfile, UnsupportedSchema, FileTooLarge,
}

public sealed class CleanupProfileException(CleanupProfileError error, string? detail = null)
    : Exception(detail ?? error.ToString())
{
    public CleanupProfileError Error { get; } = error;
}

public enum CleanupTransport { CompleteJSON, Sse, Ndjson }
public enum CleanupRoutePolicy { Ordinary, StrictLocalOnly }

public sealed record CleanupProfileCapabilities(CleanupTransport Transport, bool Discovery, bool StructuredOutput, bool ThinkingControl)
{
    public static CleanupProfileCapabilities Default { get; } = new(CleanupTransport.CompleteJSON, false, false, false);
}

/// <summary>Reference to a stored key; never the key itself. Endpoint is the canonical URL the key is scoped to.</summary>
public sealed record CleanupCredentialReference(Guid Id, string Endpoint);

/// <summary>Validated, canonical endpoint (cleanup.md §4.2).</summary>
public sealed record CleanupEndpointIdentity(string Canonical, string Host, bool IsLoopback)
{
    public static bool TryCreate(string? baseUrl, bool allowInsecureRemoteHttp, out CleanupEndpointIdentity? identity)
    {
        identity = null;
        if (baseUrl is null || baseUrl != baseUrl.Trim() || baseUrl.Length == 0) return false;
        if (baseUrl.Contains('?') || baseUrl.Contains('#')) return false;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(u.Host) || !string.IsNullOrEmpty(u.UserInfo)) return false;
        var afterScheme = baseUrl[(baseUrl.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = afterScheme.IndexOf('/');
        var rawPath = slash < 0 ? "" : afterScheme[slash..];
        if (rawPath.Split('/').Any(s => s is "." or "..")) return false;
        var authority = slash < 0 ? afterScheme : afterScheme[..slash];
        if (authority.Contains('@')) return false;
        if (!u.IsDefaultPort && (u.Port < 1 || u.Port > 65535)) return false;
        if (authority.EndsWith(":", StringComparison.Ordinal)) return false;

        var host = u.Host.ToLowerInvariant();
        var loopback = host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
        if (u.Scheme == Uri.UriSchemeHttp && !loopback && !allowInsecureRemoteHttp) return false;

        var path = u.AbsolutePath.TrimEnd('/');
        var port = u.IsDefaultPort ? "" : ":" + u.Port;
        identity = new CleanupEndpointIdentity($"{u.Scheme}://{host}{port}{path}", host, loopback);
        return true;
    }

    public static CleanupEndpointIdentity Validate(string? baseUrl, bool allowInsecureRemoteHttp) =>
        TryCreate(baseUrl, allowInsecureRemoteHttp, out var id) ? id! : throw new CleanupProfileException(CleanupProfileError.InvalidProfile, "Invalid endpoint.");
}

public sealed record CleanupProfile(
    Guid Id,
    ulong Revision,
    string Name,
    CleanupProfileAdapter Adapter,
    [property: JsonPropertyName("baseURL")] string BaseUrl,
    string Model,
    string Prompt,
    int PromptVersion,
    double TimeoutSeconds,
    CleanupProfileCapabilities Capabilities,
    CleanupRoutePolicy RoutePolicy,
    bool AllowInsecureRemoteHttp,
    CleanupCredentialReference? Credential)
{
    public const int MaxNameBytes = 256, MaxBaseUrlBytes = 2048, MaxModelBytes = 512, MaxPromptBytes = 65_536;
    public const double MinTimeout = 1, MaxTimeout = 120, DefaultTimeout = 30;

    /// <summary>Defaults of the "New" button in the profile editor.</summary>
    public static CleanupProfile CreateNew() => new(
        Guid.NewGuid(), 1, ProfileStrings.DefaultName, CleanupProfileAdapter.OpenAICompatible, ProfileStrings.DefaultBaseUrl, "",
        CleanupPromptBuilder.DefaultCleanupPrompt, 1, DefaultTimeout, CleanupProfileCapabilities.Default, CleanupRoutePolicy.Ordinary, false, null);

    [JsonIgnore]
    public CleanupEndpointIdentity Endpoint => CleanupEndpointIdentity.Validate(BaseUrl, AllowInsecureRemoteHttp);

    /// <summary>Throws CleanupProfileException(InvalidProfile / InvalidCredentialReference).</summary>
    public CleanupEndpointIdentity Validate()
    {
        void Bad() => throw new CleanupProfileException(CleanupProfileError.InvalidProfile);
        if (Revision == 0 || PromptVersion <= 0) Bad();
        if (string.IsNullOrWhiteSpace(Name) || CleanupChunker.Utf8(Name) > MaxNameBytes) Bad();
        if (string.IsNullOrWhiteSpace(Model) || CleanupChunker.Utf8(Model) > MaxModelBytes) Bad();
        if (CleanupChunker.Utf8(BaseUrl) > MaxBaseUrlBytes || CleanupChunker.Utf8(Prompt ?? "") > MaxPromptBytes) Bad();
        if (double.IsNaN(TimeoutSeconds) || double.IsInfinity(TimeoutSeconds) || TimeoutSeconds < MinTimeout || TimeoutSeconds > MaxTimeout) Bad();
        if (Adapter == CleanupProfileAdapter.Ollama && Capabilities.Transport == CleanupTransport.Sse) Bad();
        if (Adapter == CleanupProfileAdapter.OpenAICompatible && Capabilities.Transport == CleanupTransport.Ndjson) Bad();
        var id = CleanupEndpointIdentity.Validate(BaseUrl, AllowInsecureRemoteHttp);
        if (Credential is not null && Credential.Endpoint != id.Canonical)
            throw new CleanupProfileException(CleanupProfileError.InvalidCredentialReference);
        return id;
    }
}

public sealed record CleanupProfileCollection(
    ulong Revision,
    IReadOnlyList<CleanupProfile> Profiles,
    [property: JsonPropertyName("selectedID")] Guid? SelectedId,
    IReadOnlyList<string> CompletedLegacyMigrations)
{
    public const int MaxProfiles = 64;

    public static CleanupProfileCollection Empty { get; } = new(0, [], null, []);

    [JsonIgnore]
    public CleanupProfile? Selected => SelectedId is { } id ? Profiles.FirstOrDefault(p => p.Id == id) : null;

    public void Validate()
    {
        if (Profiles.Count > MaxProfiles || Profiles.Select(p => p.Id).Distinct().Count() != Profiles.Count)
            throw new CleanupProfileException(CleanupProfileError.InvalidProfile);
        foreach (var p in Profiles) p.Validate();
        if (SelectedId is { } s && Profiles.All(p => p.Id != s)) throw new CleanupProfileException(CleanupProfileError.InvalidProfile);
        if (CompletedLegacyMigrations.Count > 128 || CompletedLegacyMigrations.Any(m => m.Length == 0 || CleanupChunker.Utf8(m) > 128))
            throw new CleanupProfileException(CleanupProfileError.InvalidProfile);
    }
}

internal static class CleanupProfileJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(CleanupProfileCollection c) => JsonSerializer.Serialize(c, Options);
}

/// <summary>Credential Manager naming (cleanup.md §4.5, ARCHITECTURE credentials).</summary>
public static class CleanupProfileCredentials
{
    public const string Service = "io.github.brysonjl.remy.cleanup-profiles";

    public static string Account(CleanupCredentialReference reference) =>
        reference.Id.ToString("D").ToLowerInvariant() + "." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference.Endpoint))).ToLowerInvariant();
}
