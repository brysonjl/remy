using System.Text.RegularExpressions;
using NAudio.Wave;
using Remy.Core.Contracts;

namespace Remy.Platform.Services;

/// <summary>
/// Built-ins are the *.wav files in %WINDIR%\Media (id "builtin:&lt;name&gt;"); custom sounds live in <c>soundsFolder</c> as
/// "custom:&lt;uuid&gt;.wav|mp3". Playback is fire-and-forget through NAudio and stops any sound still playing.
/// </summary>
public sealed partial class WindowsSoundPlayer : ISoundPlayer, IDisposable
{
    public const string ImportFailedMessage = "Audio could not be imported. Choose a supported local clip up to 10 seconds and 10 MiB.";
    public const long MaxBytes = 10L * 1024 * 1024;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(10);

    [GeneratedRegex(@"^custom:[0-9a-fA-F\-]{36}\.(wav|mp3)$")]
    private static partial Regex CustomId();

    readonly string _mediaFolder;
    readonly string _soundsFolder;
    readonly object _playGate = new();
    WaveOutEvent? _active;
    AudioFileReader? _activeReader;
    IReadOnlyList<SoundOption>? _builtIns;

    public WindowsSoundPlayer(string soundsFolder, string? mediaFolder = null)
    {
        _soundsFolder = soundsFolder;
        _mediaFolder = mediaFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
    }

    public IReadOnlyList<SoundOption> BuiltInSounds => _builtIns ??= LoadBuiltIns();

    IReadOnlyList<SoundOption> LoadBuiltIns()
    {
        try
        {
            if (!Directory.Exists(_mediaFolder)) return [];
            return Directory.EnumerateFiles(_mediaFolder, "*.wav")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new SoundOption("builtin:" + n, n!))
                .ToList();
        }
        catch { return []; }
    }

    string? ResolvePath(string soundId)
    {
        if (string.IsNullOrEmpty(soundId) || soundId == "none") return null;
        if (soundId.StartsWith("builtin:", StringComparison.Ordinal))
        {
            var name = soundId["builtin:".Length..];
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var path = Path.Combine(_mediaFolder, name + ".wav");
            return File.Exists(path) ? path : null;
        }
        if (CustomId().IsMatch(soundId))
        {
            var path = Path.Combine(_soundsFolder, soundId["custom:".Length..]);
            return File.Exists(path) ? path : null;
        }
        return null;
    }

    public void Play(string soundId, float volume)
    {
        var path = ResolvePath(soundId);
        if (path == null) return;
        float v = float.IsFinite(volume) ? Math.Clamp(volume, 0f, 1f) : 0.3f;
        Task.Run(() =>
        {
            try
            {
                lock (_playGate)
                {
                    StopActive();
                    var reader = new AudioFileReader(path) { Volume = v };
                    var output = new WaveOutEvent();
                    output.Init(reader);
                    output.PlaybackStopped += (_, _) =>
                    {
                        lock (_playGate)
                        {
                            if (ReferenceEquals(_active, output)) { _active = null; _activeReader = null; }
                        }
                        try { output.Dispose(); reader.Dispose(); } catch { }
                    };
                    _active = output;
                    _activeReader = reader;
                    output.Play();
                }
            }
            catch { /* missing/corrupt/no device: play nothing */ }
        });
    }

    void StopActive()
    {
        var a = _active;
        _active = null; _activeReader = null;
        try { a?.Stop(); } catch { }
    }

    public string ImportCustomSound(string path)
    {
        string? staged = null;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".wav" or ".mp3")) throw new InvalidOperationException(ImportFailedMessage);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxBytes || info.LinkTarget != null
                || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException(ImportFailedMessage);

            using (var reader = new AudioFileReader(path))
            {
                var f = reader.WaveFormat;
                if (f.SampleRate <= 0 || f.SampleRate > 192_000 || f.Channels is < 1 or > 8
                    || reader.TotalTime <= TimeSpan.Zero || reader.TotalTime > MaxDuration)
                    throw new InvalidOperationException(ImportFailedMessage);
            }

            Directory.CreateDirectory(_soundsFolder);
            var name = Guid.NewGuid().ToString() + ext;
            staged = Path.Combine(_soundsFolder, name + ".tmp");
            File.Copy(path, staged, overwrite: true);
            File.Move(staged, Path.Combine(_soundsFolder, name));
            staged = null;
            return "custom:" + name;
        }
        catch (InvalidOperationException) { throw; }
        catch { throw new InvalidOperationException(ImportFailedMessage); }
        finally { if (staged != null) try { File.Delete(staged); } catch { } }
    }

    public string DisplayName(string soundId)
    {
        if (string.IsNullOrEmpty(soundId) || soundId == "none") return "None";
        if (soundId.StartsWith("builtin:", StringComparison.Ordinal))
            return ResolvePath(soundId) != null ? soundId["builtin:".Length..] : "Unavailable sound";
        if (CustomId().IsMatch(soundId)) return ResolvePath(soundId) != null ? "Custom audio" : "Unavailable sound";
        return "Unavailable sound";
    }

    public void Dispose() { lock (_playGate) StopActive(); }
}
