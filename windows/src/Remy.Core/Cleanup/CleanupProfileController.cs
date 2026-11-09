using Remy.Core.Contracts;
using Remy.Core.Settings;

namespace Remy.Core.Cleanup;

/// <summary>Load / select / save / remove for cleanup profiles, with endpoint-scoped credentials (cleanup.md §4.4).
/// Not thread-safe: call from one context (the settings UI).</summary>
public sealed class CleanupProfileController(CleanupProfileFileRepository repository, ICredentialStore credentials)
{
    public const int MaxCredentialBytes = 65_536;

    private CleanupProfileCollection _collection = CleanupProfileCollection.Empty;

    /// <summary>Last collection loaded or saved.</summary>
    public CleanupProfileCollection Collection => _collection;

    public CleanupProfileCollection Load()
    {
        _collection = repository.Load();
        return _collection;
    }

    public CleanupProfileCollection Select(Guid? id)
    {
        if (id is { } g && _collection.Profiles.All(p => p.Id != g)) throw new CleanupProfileException(CleanupProfileError.MissingProfile);
        return Commit(_collection with { Revision = _collection.Revision + 1, SelectedId = id });
    }

    /// <summary>
    /// Saves a draft. Revisions are managed here: a new profile gets revision 1, an existing one revision+1 (and its
    /// promptVersion increments when the prompt text changed). The saved profile becomes selected.
    /// </summary>
    /// <param name="newKey">A new API key to store under a fresh credential reference (endpoint-scoped), or null.</param>
    /// <param name="removeKey">Drop the profile's credential reference (and delete the stored secret).</param>
    public CleanupProfile Save(CleanupProfile draft, string? newKey = null, bool removeKey = false)
    {
        var existing = _collection.Profiles.FirstOrDefault(p => p.Id == draft.Id);
        var profile = draft with
        {
            Revision = existing is null ? 1UL : existing.Revision + 1,
            PromptVersion = existing is null ? Math.Max(1, draft.PromptVersion)
                : existing.Prompt == draft.Prompt ? existing.PromptVersion : existing.PromptVersion + 1,
        };
        if (removeKey || !string.IsNullOrWhiteSpace(newKey)) profile = profile with { Credential = null };

        var endpoint = profile.Validate();   // also checks the credential reference is scoped to this endpoint

        CleanupCredentialReference? staged = null;
        if (!string.IsNullOrWhiteSpace(newKey))
        {
            if (CleanupChunker.Utf8(newKey) > MaxCredentialBytes) throw new CleanupProfileException(CleanupProfileError.InvalidCredentialValue);
            staged = new CleanupCredentialReference(Guid.NewGuid(), endpoint.Canonical);
            try { credentials.Write(CleanupProfileCredentials.Service, CleanupProfileCredentials.Account(staged), newKey.Trim()); }
            catch (Exception) { throw new CleanupProfileException(CleanupProfileError.CredentialUnavailable); }
            profile = profile with { Credential = staged };
        }

        var profiles = _collection.Profiles.Where(p => p.Id != profile.Id).ToList();
        var index = existing is null ? profiles.Count : _collection.Profiles.ToList().FindIndex(p => p.Id == profile.Id);
        profiles.Insert(Math.Min(index, profiles.Count), profile);
        try
        {
            Commit(_collection with { Revision = _collection.Revision + 1, Profiles = profiles, SelectedId = profile.Id });
        }
        catch
        {
            if (staged is not null) TryDelete(staged);
            throw;
        }
        if (removeKey && existing?.Credential is { } old) TryDelete(old);
        return profile;
    }

    /// <summary>Removes the profile. Its stored key is not deleted (orphans are possible, as on the Mac).</summary>
    public CleanupProfileCollection Remove(Guid id)
    {
        if (_collection.Profiles.All(p => p.Id != id)) throw new CleanupProfileException(CleanupProfileError.MissingProfile);
        var rest = _collection.Profiles.Where(p => p.Id != id).ToList();
        var selected = _collection.SelectedId == id ? rest.FirstOrDefault()?.Id : _collection.SelectedId;
        return Commit(_collection with { Revision = _collection.Revision + 1, Profiles = rest, SelectedId = selected });
    }

    private CleanupProfileCollection Commit(CleanupProfileCollection next)
    {
        _collection = repository.Save(_collection, next);
        return _collection;
    }

    private void TryDelete(CleanupCredentialReference r)
    {
        try { credentials.Delete(CleanupProfileCredentials.Service, CleanupProfileCredentials.Account(r)); } catch { /* best effort */ }
    }

    /// <summary>Key for the profile, or null when it has no credential reference. Throws CleanupFailureException(authentication)
    /// when a reference exists but the key is unavailable.</summary>
    public string? ResolveApiKey(CleanupProfile profile)
    {
        if (profile.Credential is not { } r) return null;
        var endpoint = profile.Validate();
        if (r.Endpoint != endpoint.Canonical) throw new CleanupFailureException(RemyCleanupFailure.Authentication);
        string? key;
        try { key = credentials.Read(CleanupProfileCredentials.Service, CleanupProfileCredentials.Account(r)); }
        catch (Exception) { throw new CleanupFailureException(RemyCleanupFailure.Authentication); }
        if (string.IsNullOrWhiteSpace(key)) throw new CleanupFailureException(RemyCleanupFailure.Authentication);
        return key;
    }

    public bool HasStoredKey(CleanupProfile profile)
    {
        try { return ResolveApiKey(profile) is not null; } catch (CleanupFailureException) { return false; }
    }

    /// <summary>Frozen runtime for the saved profile (dictation or Test connection).</summary>
    public CleanupRuntime BuildRuntime(CleanupProfile profile, ContextPolicy policy)
    {
        try { profile.Validate(); }
        catch (CleanupProfileException) { throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration); }
        if (profile.Capabilities.Transport != CleanupTransport.CompleteJSON || profile.RoutePolicy == CleanupRoutePolicy.StrictLocalOnly)
            throw new CleanupFailureException(RemyCleanupFailure.InvalidConfiguration);
        return new CleanupRuntime(
            TranscriptPostProcessingMode.Profile, profile.Prompt, profile.BaseUrl, profile.Model, false, ResolveApiKey(profile),
            TimeSpan.FromSeconds(profile.TimeoutSeconds), profile.Adapter, profile.Id, profile.Name,
            profile.Capabilities.StructuredOutput, profile.Capabilities.Discovery, profile.Capabilities.ThinkingControl,
            S1MiniStructure.Prose, S1MiniContextSetting.Automatic, policy);
    }
}
