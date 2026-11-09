using Remy.Core.Cleanup;
using Remy.Core.Contracts;
using Remy.Core.Settings;

namespace Remy.Core.Session;

/// <summary>Freezes the cleanup configuration at recording start, including the credential value (pipeline.md 2.3).</summary>
public sealed class CleanupRuntimeFactory(RemySettings settings, ICredentialStore credentials, CleanupProfileController? profiles)
{
    public ContextPolicy BuildPolicy() => new(
        settings.DictationContextAwarenessEnabled,
        settings.ShareDictationContextWithRemoteProviders,
        settings.WritingStylesSnapshot(),
        settings.S1MiniAppStyling,
        settings.DictationAppRules);

    /// <summary>Never throws. A failure to build (for example a missing profile credential) is returned so the cleanup
    /// stage can fall back to the raw transcript with that category.</summary>
    public (CleanupRuntime Runtime, RemyCleanupFailure? Failure) Build()
    {
        var policy = BuildPolicy();
        var mode = settings.TranscriptPostProcessingMode;
        try
        {
            switch (mode)
            {
                case TranscriptPostProcessingMode.Ollama:
                    return (new CleanupRuntime(mode, settings.OllamaPostProcessingPrompt, settings.OllamaBaseURL, settings.OllamaModel,
                        settings.OllamaReasoningEnabled, null, TimeSpan.FromSeconds(120), null, null, null,
                        true, true, true, S1MiniStructure.Prose, S1MiniContextSetting.Automatic, policy), null);

                case TranscriptPostProcessingMode.OpenAICompatible:
                    return (new CleanupRuntime(mode, settings.OpenAICompatiblePostProcessingPrompt, settings.OpenAICompatibleBaseURL,
                        settings.OpenAICompatibleModel, false,
                        Read(BuiltInCredentials.OpenAICompatibleService, BuiltInCredentials.OpenAICompatibleAccount),
                        TimeSpan.FromSeconds(60), null, null, null,
                        true, false, false, S1MiniStructure.Prose, S1MiniContextSetting.Automatic, policy), null);

                case TranscriptPostProcessingMode.OpenRouter:
                    return (new CleanupRuntime(mode, settings.OpenRouterPostProcessingPrompt, CleanupEndpoints.OpenRouterBaseUrl,
                        settings.OpenRouterModel, settings.OpenRouterReasoningEnabled, OpenRouterKey(),
                        TimeSpan.FromSeconds(60), null, null, null,
                        false, false, false, S1MiniStructure.Prose, S1MiniContextSetting.Automatic, policy), null);

                case TranscriptPostProcessingMode.S1Mini:
                    return (new CleanupRuntime(mode, "", null, null, false, null, TimeSpan.FromSeconds(30), null, null, null,
                        false, false, false, settings.S1MiniStructure, settings.S1MiniContextSetting, policy), null);

                case TranscriptPostProcessingMode.Profile:
                {
                    var selected = profiles?.Collection.SelectedId;
                    var profile = profiles?.Collection.Profiles.FirstOrDefault(p => p.Id == selected);
                    if (profile is null) throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
                    return (profiles!.BuildRuntime(profile, policy), null);
                }

                default:
                    return (CleanupRuntime.Off(policy), null);
            }
        }
        catch (CleanupFailureException ex)
        {
            return (CleanupRuntime.Off(policy), ex.Failure);
        }
        catch (Exception)
        {
            return (CleanupRuntime.Off(policy), RemyCleanupFailure.Of(RemyCleanupFailure.InvalidConfiguration));
        }
    }

    string? Read(string service, string account)
    {
        try
        {
            var v = credentials.Read(service, account);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch { return null; }
    }

    string? OpenRouterKey()
    {
        var stored = Read(BuiltInCredentials.OpenRouterService, BuiltInCredentials.OpenRouterAccount);
        if (stored is not null) return stored;
        var name = settings.OpenRouterAPIKeyEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var env = Environment.GetEnvironmentVariable(name.Trim());
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    /// <summary>Cleanup problems for the current settings (pipeline.md 8.3 / ui.md 2.4).</summary>
    public IReadOnlyList<string> Problems(bool s1MiniInstalled)
    {
        var list = new List<string>();
        switch (settings.TranscriptPostProcessingMode)
        {
            case TranscriptPostProcessingMode.S1Mini:
                if (!string.Equals(settings.SelectedLanguage, "en", StringComparison.OrdinalIgnoreCase)) list.Add(CleanupProblems.S1MiniLanguageUnsupported);
                if (!s1MiniInstalled) list.Add(CleanupProblems.S1MiniNotDownloaded);
                break;
            case TranscriptPostProcessingMode.Ollama:
                if (string.IsNullOrWhiteSpace(settings.OllamaModel)) list.Add(CleanupProblems.OllamaModelMissing);
                break;
            case TranscriptPostProcessingMode.OpenRouter:
                if (OpenRouterKey() is null) list.Add(CleanupProblems.OpenRouterKeyMissing);
                if (string.IsNullOrWhiteSpace(settings.OpenRouterModel)) list.Add(CleanupProblems.OpenRouterModelMissing);
                break;
            case TranscriptPostProcessingMode.OpenAICompatible:
                if (string.IsNullOrWhiteSpace(settings.OpenAICompatibleModel)) list.Add(CleanupProblems.OpenAICompatibleModelMissing);
                break;
            case TranscriptPostProcessingMode.Profile:
                var c = profiles?.Collection;
                if (c?.SelectedId is null || c.Profiles.All(p => p.Id != c.SelectedId)) list.Add(CleanupProblems.SavedProfileMissing);
                break;
        }
        return list;
    }
}
