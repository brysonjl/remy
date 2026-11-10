using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.Session;
using Remy.Core.Settings;

namespace Remy.Core.Transforms;

/// <summary>The two messages of one freeform request.</summary>
public sealed record TransformRequest(string System, string User);

/// <summary>A ready provider: its display name and a single-request completion that returns the raw message content.</summary>
public sealed record TransformRoute(string ProviderName, Func<TransformRequest, CancellationToken, Task<string>> Complete);

/// <summary>Either a ready route or the notice explaining why Transforms cannot run.</summary>
public sealed record TransformRouteResult(TransformRoute? Route, TransformNotice? Notice)
{
    public static TransformRouteResult Ready(TransformRoute route) => new(route, null);
    public static TransformRouteResult Unavailable(TransformNotice notice) => new(null, notice);
}

/// <summary>Resolves the provider for a transform from the current settings when the shortcut fires (v0.2-features.md §8.5).
/// Credentials are read now and kept only in the completion closure.</summary>
public static class TransformProviderRoute
{
    public const string OllamaName = "Ollama";
    public const string OpenRouterName = "OpenRouter";
    public const string OpenAICompatibleName = "OpenAI Compatible";

    /// <summary>Mode-level notice (also drives the Transforms page provider row), or null when the mode can run transforms.</summary>
    public static TransformNotice? ModeNotice(TranscriptPostProcessingMode mode) => mode switch
    {
        TranscriptPostProcessingMode.None or TranscriptPostProcessingMode.FluidAudioVocabulary
            or TranscriptPostProcessingMode.AppleIntelligence => TransformNotice.CleanupOff,
        TranscriptPostProcessingMode.S1Mini => TransformNotice.ProviderUnsupported("S1-mini"),
        _ => null,
    };

    /// <summary>Display name of a mode that can run transforms (page provider row).</summary>
    public static string ModeDisplayName(TranscriptPostProcessingMode mode) => mode switch
    {
        TranscriptPostProcessingMode.Ollama => OllamaName,
        TranscriptPostProcessingMode.OpenRouter => OpenRouterName,
        TranscriptPostProcessingMode.OpenAICompatible => OpenAICompatibleName,
        TranscriptPostProcessingMode.Profile => "Saved Profile",
        _ => mode.ToString(),
    };

    /// <summary>Remote endpoints must be HTTPS; loopback HTTP is allowed. Null when usable.</summary>
    public static TransformNotice? EndpointNotice(string? url)
    {
        var s = (url ?? "").Trim();
        if (s.Length == 0) return TransformNotice.ProviderUnavailable;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;
        if (CleanupEndpointIdentity.TryCreate(s, false, out _)) return null;
        return CleanupEndpointIdentity.TryCreate(s, true, out _) ? TransformNotice.InsecureEndpoint : TransformNotice.ProviderUnavailable;
    }

    public static TransformRouteResult Resolve(RemySettings settings, ICredentialStore credentials, CleanupProfileController? profiles,
        TransformCompletion completion)
    {
        var mode = settings.TranscriptPostProcessingMode;
        if (ModeNotice(mode) is { } modeNotice) return TransformRouteResult.Unavailable(modeNotice);

        switch (mode)
        {
            case TranscriptPostProcessingMode.Ollama:
            {
                var model = (settings.OllamaModel ?? "").Trim();
                var baseUrl = settings.OllamaBaseURL;
                if (model.Length == 0) return TransformRouteResult.Unavailable(TransformNotice.ProviderUnavailable);
                if (EndpointNotice(baseUrl) is { } n) return TransformRouteResult.Unavailable(n);
                bool reasoning = settings.OllamaReasoningEnabled;
                return TransformRouteResult.Ready(new TransformRoute(OllamaName, (r, ct) =>
                    completion.OllamaAsync(baseUrl, model, r.System, r.User, null, TransformCompletion.OllamaTimeout, true, true, reasoning, ct)));
            }

            case TranscriptPostProcessingMode.OpenRouter:
            {
                var key = OpenRouterKey(settings, credentials);
                if (key is null) return TransformRouteResult.Unavailable(TransformNotice.MissingCredential);
                var model = (settings.OpenRouterModel ?? "").Trim();
                if (model.Length == 0) return TransformRouteResult.Unavailable(TransformNotice.ProviderUnavailable);
                bool reasoning = settings.OpenRouterReasoningEnabled;
                return TransformRouteResult.Ready(new TransformRoute(OpenRouterName, (r, ct) =>
                    completion.OpenRouterAsync(model, r.System, r.User, key, ct, reasoning)));
            }

            case TranscriptPostProcessingMode.OpenAICompatible:
            {
                var model = (settings.OpenAICompatibleModel ?? "").Trim();
                var baseUrl = settings.OpenAICompatibleBaseURL;
                if (model.Length == 0) return TransformRouteResult.Unavailable(TransformNotice.ProviderUnavailable);
                if (EndpointNotice(baseUrl) is { } n) return TransformRouteResult.Unavailable(n);
                var key = ReadCredential(credentials, BuiltInCredentials.OpenAICompatibleService, BuiltInCredentials.OpenAICompatibleAccount);
                return TransformRouteResult.Ready(new TransformRoute(OpenAICompatibleName, (r, ct) =>
                    completion.OpenAICompatibleAsync(baseUrl, model, r.System, r.User, key, TransformCompletion.RemoteTimeout, ct)));
            }

            case TranscriptPostProcessingMode.Profile:
                return ResolveProfile(profiles, completion);
        }
        return TransformRouteResult.Unavailable(TransformNotice.CleanupOff);
    }

    static TransformRouteResult ResolveProfile(CleanupProfileController? profiles, TransformCompletion completion)
    {
        var profile = profiles?.Collection.Selected;
        if (profile is null) return TransformRouteResult.Unavailable(TransformNotice.InvalidProfile);
        string? key;
        try
        {
            profile.Validate();
            if (profile.Capabilities.Transport != CleanupTransport.CompleteJSON || profile.RoutePolicy == CleanupRoutePolicy.StrictLocalOnly)
                return TransformRouteResult.Unavailable(TransformNotice.InvalidProfile);
            key = profiles!.ResolveApiKey(profile);
        }
        catch (CleanupFailureException ex) when (ex.Failure.Category == RemyCleanupFailure.Authentication)
        {
            return TransformRouteResult.Unavailable(TransformNotice.MissingCredential);
        }
        catch (Exception)
        {
            return TransformRouteResult.Unavailable(TransformNotice.InvalidProfile);
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(profile.TimeoutSeconds, 60));
        var baseUrl = profile.BaseUrl;
        var model = profile.Model;
        var caps = profile.Capabilities;
        if (profile.Adapter == CleanupProfileAdapter.Ollama)
            return TransformRouteResult.Ready(new TransformRoute(profile.Name, (r, ct) =>
                completion.OllamaAsync(baseUrl, model, r.System, r.User, key, timeout, caps.Discovery, caps.ThinkingControl, false, ct)));
        return TransformRouteResult.Ready(new TransformRoute(profile.Name, (r, ct) =>
            completion.OpenAICompatibleAsync(baseUrl, model, r.System, r.User, key, timeout, ct)));
    }

    static string? OpenRouterKey(RemySettings settings, ICredentialStore credentials)
    {
        var stored = ReadCredential(credentials, BuiltInCredentials.OpenRouterService, BuiltInCredentials.OpenRouterAccount);
        if (stored is not null) return stored;
        var name = settings.OpenRouterAPIKeyEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var env = Environment.GetEnvironmentVariable(name.Trim());
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    static string? ReadCredential(ICredentialStore credentials, string service, string account)
    {
        try
        {
            var v = credentials.Read(service, account);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch { return null; }
    }
}
