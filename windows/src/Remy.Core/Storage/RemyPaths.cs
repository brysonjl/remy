namespace Remy.Core.Storage;

/// <summary>All on-disk locations (ARCHITECTURE.md "Data folders"). Roaming config: %APPDATA%\Remy; local data:
/// %LOCALAPPDATA%\Remy. When env var REMY_DATA_ROOT (or <see cref="OverrideRoot"/>) is set, both trees live under it
/// as <c>Roaming\</c> and <c>Local\</c>. Properties never create folders; use Ensure*/ <see cref="Ensure"/>.</summary>
public static class RemyPaths
{
    public const string AppFolderName = "Remy";
    public const string DataRootEnvVar = "REMY_DATA_ROOT";

    /// <summary>Test/dev override; takes precedence over the environment variable.</summary>
    public static string? OverrideRoot { get; set; }

    private static string? EffectiveRoot
    {
        get
        {
            var r = OverrideRoot ?? Environment.GetEnvironmentVariable(DataRootEnvVar);
            return string.IsNullOrWhiteSpace(r) ? null : r;
        }
    }

    // ---- roaming (config)
    public static string ConfigDirectory => EffectiveRoot is { } r
        ? Path.Combine(r, "Roaming")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");
    public static string CleanupProfilesDirectory => Path.Combine(ConfigDirectory, "CleanupProfiles");
    public static string CleanupProfilesFile => Path.Combine(CleanupProfilesDirectory, "profiles.json");

    // ---- local (data)
    public static string LocalDirectory => EffectiveRoot is { } r
        ? Path.Combine(r, "Local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string ModelsDirectory => Path.Combine(LocalDirectory, "Models");
    public static string ModelsStagingDirectory => Path.Combine(ModelsDirectory, ".staging");
    public static string ParakeetModelDirectory => Path.Combine(ModelsDirectory, "parakeet-tdt-0.6b-v2-int8");
    public static string S1MiniModelDirectory => Path.Combine(ModelsDirectory, "S1-mini");
    public static string SileroVadDirectory => Path.Combine(ModelsDirectory, "silero-vad");
    public static string HistoryDirectory => Path.Combine(LocalDirectory, "History");
    public static string HistoryDatabaseFile => Path.Combine(HistoryDirectory, "history.sqlite");
    public static string RecoveryDirectory => Path.Combine(LocalDirectory, "Recovery");
    public static string SoundsDirectory => Path.Combine(LocalDirectory, "Sounds");
    public static string LogsDirectory => Path.Combine(LocalDirectory, "Logs");

    /// <summary>Create <paramref name="directory"/> (and parents) if missing and return it.</summary>
    public static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static string EnsureConfigDirectory() => Ensure(ConfigDirectory);
    public static string EnsureCleanupProfilesDirectory() => Ensure(CleanupProfilesDirectory);
    public static string EnsureModelsDirectory() => Ensure(ModelsDirectory);
    public static string EnsureHistoryDirectory() => Ensure(HistoryDirectory);
    public static string EnsureRecoveryDirectory() => Ensure(RecoveryDirectory);
    public static string EnsureSoundsDirectory() => Ensure(SoundsDirectory);
    public static string EnsureLogsDirectory() => Ensure(LogsDirectory);

    /// <summary>True if <paramref name="path"/> is inside <paramref name="root"/>, has no "." / ".." segments and
    /// no reparse point among existing ancestors up to the root (never follow junctions for model/history IO).</summary>
    public static bool IsConfinedTo(string root, string path)
    {
        try
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (path.Split('/', '\\').Any(s => s == "..")) return false;
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            for (var cur = full; cur.Length > rootFull.Length; cur = Path.GetDirectoryName(cur) ?? rootFull)
            {
                if ((File.Exists(cur) || Directory.Exists(cur)) &&
                    File.GetAttributes(cur).HasFlag(FileAttributes.ReparsePoint)) return false;
            }
            return true;
        }
        catch { return false; }
    }
}
