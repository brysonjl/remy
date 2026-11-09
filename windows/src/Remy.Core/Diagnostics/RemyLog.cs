using System.Diagnostics;
using System.Globalization;
using Remy.Core.Storage;

namespace Remy.Core.Diagnostics;

public enum RemyLogLevel { Debug, Info, Warning, Error }

/// <summary>Closed vocabulary for error logging (settings-models.md 8): never log exception text.</summary>
public static class RemyDiagnostics
{
    public const string Subsystem = "io.github.brysonjl.remy";

    /// <summary>cancelled | timeout | transport | other.</summary>
    public static string ErrorCategory(Exception? e) => e switch
    {
        null => "other",
        TimeoutException => "timeout",
        TaskCanceledException t when t.InnerException is TimeoutException => "timeout",
        OperationCanceledException => "cancelled",
        System.Net.Http.HttpRequestException => "transport",
        System.Net.Sockets.SocketException => "transport",
        IOException => "transport",
        _ => "other",
    };
}

/// <summary>Tiny thread-safe, metadata-only file logger: %LOCALAPPDATA%\Remy\Logs\remy-YYYYMMDD.log, 7 days kept.
/// Call sites must pass only enums/counts/known labels, never transcript text, prompts, paths or keys.</summary>
public static class RemyLog
{
    public const int RetentionDays = 7;
    private static readonly object Gate = new();
    private static string? _lastPrunedDay;

    public static RemyLogLevel MinimumLevel { get; set; } = RemyLogLevel.Info;

    /// <summary>Replaceable clock for tests.</summary>
    public static Func<DateTime> Now { get; set; } = () => DateTime.Now;

    /// <summary>Optional sink (tests, in-memory capture).</summary>
    public static Action<string>? Sink { get; set; }

    public static void Debug(string area, string message) => Write(RemyLogLevel.Debug, area, message);
    public static void Info(string area, string message) => Write(RemyLogLevel.Info, area, message);
    public static void Warn(string area, string message) => Write(RemyLogLevel.Warning, area, message);

    /// <summary>Logs only the area, a short label and the error category.</summary>
    public static void Error(string area, string message, Exception? ex = null) =>
        Write(RemyLogLevel.Error, area, ex is null ? message : $"{message} category={RemyDiagnostics.ErrorCategory(ex)}");

    public static string LogFileFor(DateTime day) =>
        Path.Combine(RemyPaths.LogsDirectory, "remy-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    private static void Write(RemyLogLevel level, string area, string message)
    {
        if (level < MinimumLevel) return;
        try
        {
            var now = Now();
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} {level.ToString().ToUpperInvariant(),-7} {area}: {Sanitize(message)}";
            Sink?.Invoke(line);
            lock (Gate)
            {
                RemyPaths.EnsureLogsDirectory();
                var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (_lastPrunedDay != day) { _lastPrunedDay = day; Prune(now); }
                File.AppendAllText(LogFileFor(now), line + Environment.NewLine);
            }
        }
        catch { /* logging must never throw */ }
    }

    private static string Sanitize(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Delete log files older than <see cref="RetentionDays"/>.</summary>
    public static void Prune(DateTime now)
    {
        try
        {
            if (!Directory.Exists(RemyPaths.LogsDirectory)) return;
            var cutoff = now.Date.AddDays(-(RetentionDays - 1));
            foreach (var f in Directory.EnumerateFiles(RemyPaths.LogsDirectory, "remy-????????.log"))
            {
                var stem = Path.GetFileNameWithoutExtension(f)[5..];
                if (DateTime.TryParseExact(stem, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < cutoff)
                    try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}

/// <summary>Span timing helper. Disabled unless REMY_PERF=1 (also honors REMY_PERF_TRACE=1). Names are static
/// "&lt;area&gt;.&lt;phase&gt;" strings. Usage: <c>using var _ = PerfTrace.Begin("stt.finalize");</c></summary>
public static class PerfTrace
{
    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("REMY_PERF") == "1" || Environment.GetEnvironmentVariable("REMY_PERF_TRACE") == "1";

    public static IDisposable Begin(string name) => Enabled ? new Span(name) : NullSpan.Instance;

    public static T Measure<T>(string name, Func<T> work)
    {
        using var _ = Begin(name);
        return work();
    }

    public static void Measure(string name, Action work)
    {
        using var _ = Begin(name);
        work();
    }

    private sealed class NullSpan : IDisposable
    {
        public static readonly NullSpan Instance = new();
        public void Dispose() { }
    }

    private sealed class Span(string name) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            RemyLog.Info("perf", $"{name} duration_ms={ms:0.0}");
        }
    }
}
