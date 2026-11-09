using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Remy.Core.Diagnostics;
using Remy.Core.Storage;

namespace Remy.Core.Settings;

public enum ConfigurationVersionKind { Legacy, Current, Unsupported, Malformed }

/// <summary>Result of the schema-version preflight (settings-models.md 1.2).</summary>
public sealed record ConfigurationGate(ConfigurationVersionKind Kind, long? Version, string? BlockingMessage)
{
    public bool IsBlocking => BlockingMessage is not null;
}

public static class ConfigurationVersion
{
    public const string BlockingDialogTitle = "Remy cannot open these settings";
    public const string BlockingDialogButton = "Quit";

    public static string UnsupportedMessage(long version) =>
        $"These settings use configuration version {version}. This build supports version 1. Open a compatible Remy build or restore a Remy settings backup. No Remy settings were changed.";

    public const string MalformedMessage =
        "The Remy configuration version could not be read. Restore a Remy settings backup before reopening the app. No Remy settings were changed.";

    /// <summary>Absent or 0 = legacy; 1 = current; greater than 1 = unsupported; anything else (string, bool,
    /// negative, fractional, null) = malformed. Never mutates anything.</summary>
    public static ConfigurationGate Preflight(JsonObject? root)
    {
        if (root is null || !root.TryGetPropertyValue(RemySettings.SchemaVersionKey, out var node))
            return new(ConfigurationVersionKind.Legacy, 0, null);
        if (node is JsonValue v)
        {
            try
            {
                var el = v.GetValue<JsonElement>();
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
                {
                    if (n == 0) return new(ConfigurationVersionKind.Legacy, 0, null);
                    if (n == RemySettings.CurrentSchemaVersion) return new(ConfigurationVersionKind.Current, n, null);
                    if (n > RemySettings.CurrentSchemaVersion) return new(ConfigurationVersionKind.Unsupported, n, UnsupportedMessage(n));
                }
            }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
        }
        return new(ConfigurationVersionKind.Malformed, null, MalformedMessage);
    }
}

public sealed record SettingsLoadResult(RemySettings Settings, ConfigurationGate Gate, bool MigratedFromLegacy, bool FileWasUnreadable);

/// <summary>Loads/saves %APPDATA%\Remy\settings.json. Tolerant load, atomic write, debounced save on property change.</summary>
public sealed class SettingsStore : IDisposable
{
    public static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeSpan _debounce;
    private Timer? _timer;
    private RemySettings? _attached;
    private JsonObject _unknownKeys = new();
    private bool _blocked;
    private bool _disposed;

    public SettingsStore(string? path = null, TimeSpan? debounce = null)
    {
        _path = path ?? RemyPaths.SettingsFile;
        _debounce = debounce ?? DebounceInterval;
    }

    public string Path => _path;

    /// <summary>Raised after a successful write (background thread).</summary>
    public event EventHandler? Saved;

    /// <summary>True when the version gate blocked the file: nothing will ever be written.</summary>
    public bool IsBlocked => _blocked;

    /// <summary>Read the file, run the version gate and return a populated settings object. When the gate blocks,
    /// returns default settings and never writes. Otherwise attaches change tracking to the returned settings.</summary>
    public SettingsLoadResult Load()
    {
        JsonObject? root = null;
        bool unreadable = false;
        if (File.Exists(_path))
        {
            try
            {
                var text = File.ReadAllText(_path, Encoding.UTF8);
                root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
                if (root is null) unreadable = true;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                unreadable = true;
                RemyLog.Warn("settings", $"settings file unreadable category={RemyDiagnostics.ErrorCategory(ex)}");
            }
        }

        var settings = new RemySettings();
        if (unreadable)
        {
            BackUpUnreadable();
            root = null;
        }

        var gate = ConfigurationVersion.Preflight(root);
        if (gate.IsBlocking)
        {
            _blocked = true;
            return new(settings, gate, false, false);
        }

        bool rewrite = false;
        _unknownKeys = new JsonObject();
        if (root is not null)
        {
            rewrite = settings.LoadFrom(root);
            var known = new HashSet<string>(RemySettings.PersistedKeys) { RemySettings.SchemaVersionKey };
            foreach (var kv in root)
                if (!known.Contains(kv.Key) && kv.Value is not null) _unknownKeys[kv.Key] = kv.Value.DeepClone();
        }

        bool migrated = root is not null && gate.Kind == ConfigurationVersionKind.Legacy;
        bool missing = root is null;
        Attach(settings);
        if (migrated || rewrite || (missing && !unreadable && !File.Exists(_path)))
            Save(settings);
        return new(settings, gate with { Kind = migrated ? ConfigurationVersionKind.Current : gate.Kind }, migrated, unreadable);
    }

    private void BackUpUnreadable()
    {
        try
        {
            var bak = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(_path, bak, overwrite: true);
        }
        catch { }
    }

    private void Attach(RemySettings settings)
    {
        lock (_gate)
        {
            if (_attached is not null) _attached.PropertyChanged -= OnChanged;
            _attached = settings;
            settings.PropertyChanged += OnChanged;
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || _blocked) return;
            _timer ??= new Timer(_ => { try { Flush(); } catch { } }, null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Write any pending changes immediately (call on shutdown).</summary>
    public void Flush()
    {
        RemySettings? s;
        lock (_gate)
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            s = _attached;
        }
        if (s is not null) Save(s);
    }

    /// <summary>Atomic write (temp file, then replace). Re-checks the stored schema marker so a stale load can never
    /// overwrite a newer-version file. Returns false if blocked or the write failed.</summary>
    public bool Save(RemySettings settings)
    {
        lock (_gate)
        {
            if (_blocked) return false;
            try
            {
                if (File.Exists(_path))
                {
                    try
                    {
                        var existing = JsonNode.Parse(File.ReadAllText(_path, Encoding.UTF8)) as JsonObject;
                        if (existing is not null && ConfigurationVersion.Preflight(existing).IsBlocking) { _blocked = true; return false; }
                    }
                    catch (JsonException) { /* unreadable existing file: overwrite (backed up on load) */ }
                }

                var root = new JsonObject { [RemySettings.SchemaVersionKey] = RemySettings.CurrentSchemaVersion };
                foreach (var kv in settings.ToJson()) root[kv.Key] = kv.Value?.DeepClone();
                foreach (var kv in _unknownKeys) root[kv.Key] = kv.Value?.DeepClone();

                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                RemyLog.Warn("settings", $"save failed category={RemyDiagnostics.ErrorCategory(ex)}");
                return false;
            }
        }
        Saved?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Dispose()
    {
        try { Flush(); } catch { }
        lock (_gate)
        {
            _disposed = true;
            if (_attached is not null) _attached.PropertyChanged -= OnChanged;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
