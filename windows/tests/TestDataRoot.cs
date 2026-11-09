using System.Runtime.CompilerServices;

namespace Remy.Tests;

internal static class TestDataRoot
{
    /// <summary>Runs before any test: sends RemyPaths (logs, history, models) to a per-run temp folder.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REMY_DATA_ROOT"))) return;
        var root = Path.Combine(Path.GetTempPath(), "remy-tests-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("REMY_DATA_ROOT", root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        };
    }
}
