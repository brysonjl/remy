using System.Globalization;

namespace Remy.Core.Models;

public enum ModelReadinessState
{
    NotDownloaded, Downloaded, Available, Configured, NeedsSetup,
    Downloading, Verifying, Preparing, Deleting, Ready, Unavailable, Failed,
}

/// <summary>Shared readiness set (settings-models.md 2.5). Titles are verbatim.</summary>
public sealed record ModelReadiness(ModelReadinessState State, string? Detail = null, double? Progress = null)
{
    public string Title => State switch
    {
        ModelReadinessState.NotDownloaded => "Not downloaded",
        ModelReadinessState.Downloaded => "Downloaded",
        ModelReadinessState.Available => "Available",
        ModelReadinessState.Configured => "Configured",
        ModelReadinessState.NeedsSetup => "Not set up",
        ModelReadinessState.Downloading => "Downloading…",
        ModelReadinessState.Verifying => "Verifying…",
        ModelReadinessState.Preparing => "Preparing for first use…",
        ModelReadinessState.Deleting => "Deleting…",
        ModelReadinessState.Ready => "Ready",
        ModelReadinessState.Unavailable => "Unavailable",
        ModelReadinessState.Failed => "Needs attention",
        _ => "",
    };

    public bool IsWorking => State is ModelReadinessState.Downloading or ModelReadinessState.Verifying
        or ModelReadinessState.Preparing or ModelReadinessState.Deleting;

    public bool IsReady => State == ModelReadinessState.Ready;

    public static ModelReadiness Downloading(double fraction) =>
        new(ModelReadinessState.Downloading, null, double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : null);
}

public enum ModelPhase { Downloading, Verifying }

/// <summary>Byte progress of one install. Fraction maps transfer to 0..0.98 and the verification tail to 0.98..1.</summary>
public sealed record ModelProgress(long DownloadedBytes, long TotalBytes, ModelPhase Phase)
{
    public const double InstallationFraction = 0.98;

    public double Fraction => Phase == ModelPhase.Verifying ? 1.0
        : TotalBytes <= 0 ? 0 : Math.Min(1.0, (double)DownloadedBytes / TotalBytes) * InstallationFraction;

    /// <summary>The page caption: "NN% · X of Y MB" while transferring, "Verifying model integrity…" at the end.</summary>
    public string Caption
    {
        get
        {
            if (Phase == ModelPhase.Verifying || Fraction >= InstallationFraction) return "Verifying model integrity…";
            long mib = 1024 * 1024;
            double pct = Math.Min(1.0, (double)DownloadedBytes / Math.Max(1, TotalBytes)) * 100;
            return string.Create(CultureInfo.InvariantCulture,
                $"{(int)pct}% · {DownloadedBytes / mib} of {(TotalBytes + mib - 1) / mib} MB");
        }
    }
}
