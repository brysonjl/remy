using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Remy.Core.Context;
using Remy.Core.Contracts;

namespace Remy.Platform.Destination;

/// <summary>Bounded UI Automation walk of the destination window for on-screen names (v0.2-features.md §7.3).
/// Reads Name, ValuePattern.Value and HelpText per node in one cached request; never changes app state; texts are returned
/// to the caller only and never logged.</summary>
public sealed partial class DestinationService
{
    /// <summary>The walk is abandoned slightly after its budget even if a node read is stuck.</summary>
    static readonly TimeSpan WalkAbandonAfter = ScreenNameWalk.Budget + TimeSpan.FromMilliseconds(25);

    // A dedicated automation: its timeouts are changed per read and must not affect focus/context capture.
    static readonly object WalkGate = new();
    static UIA3Automation? _walkAutomation;

    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    const uint GaRoot = 2;

    public async Task<ScreenTextWalkResult?> CaptureScreenTextAsync(DestinationTarget target, CancellationToken ct)
    {
        try
        {
            if (target.IsSecure || target.IsElevated || target.IsRemy || IsSensitiveApplication(target.ProcessName, target.WindowTitle)) return null;
            var work = Task.Run(() => WalkCore(target, ct), CancellationToken.None);
            var finished = await Task.WhenAny(work, Task.Delay(WalkAbandonAfter, ct)).ConfigureAwait(false);
            if (finished != work)
            {
                _ = work.ContinueWith(_ => { }, TaskScheduler.Default);       // observe and drop a late result
                return ct.IsCancellationRequested ? null : new ScreenTextWalkResult([], 0, true, WalkAbandonAfter, "budget");
            }
            return await work.ConfigureAwait(false);
        }
        catch { return null; }
    }

    static ScreenTextWalkResult? WalkCore(DestinationTarget target, CancellationToken ct)
    {
        lock (WalkGate)
        {
            try
            {
                var hwnd = GetAncestor(target.WindowHandle, GaRoot);
                if (hwnd == 0) hwnd = target.WindowHandle;
                if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out var pid) == 0 || (int)pid != target.ProcessId)
                    return new ScreenTextWalkResult([], 0, false, TimeSpan.Zero, "noWindow");

                var automation = _walkAutomation ??= new UIA3Automation();
                var root = automation.FromHandle(hwnd);
                if (root == null) return new ScreenTextWalkResult([], 0, false, TimeSpan.Zero, "noWindow");

                var request = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Children, AutomationElementMode = AutomationElementMode.Full };
                request.TreeFilter = new PropertyCondition(automation.PropertyLibrary.Element.IsControlElement, true);
                request.Add(automation.PropertyLibrary.Element.Name);
                request.Add(automation.PropertyLibrary.Element.HelpText);
                request.Add(automation.PropertyLibrary.Element.IsPassword);
                request.Add(automation.PatternLibrary.ValuePattern);
                request.Add(automation.PropertyLibrary.Value.Value);

                var clock = Stopwatch.StartNew();
                return ScreenNameWalk.Run(new UiaNode(automation, request, root), () => clock.Elapsed, () => ct.IsCancellationRequested);
            }
            catch { return null; }
        }
    }

    sealed class UiaNode(AutomationBase automation, CacheRequest request, AutomationElement element) : IScreenNode
    {
        public ScreenNodeSnapshot? Read(TimeSpan timeout)
        {
            try
            {
                automation.TransactionTimeout = timeout;
                automation.ConnectionTimeout = timeout;
                using (request.Activate())
                {
                    var cached = element.FindFirst(TreeScope.Element, TrueCondition.Default);
                    if (cached == null) return null;

                    if (SafeBool(() => cached.Properties.IsPassword.ValueOrDefault)) return new ScreenNodeSnapshot(true, [], []);

                    var texts = new List<string>(3);
                    if (SafeString(() => cached.Properties.Name.ValueOrDefault) is { } name) texts.Add(name);
                    if (SafeString(() => cached.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault) is { } value) texts.Add(value);
                    if (SafeString(() => cached.Properties.HelpText.ValueOrDefault) is { } help) texts.Add(help);

                    var children = new List<IScreenNode>();
                    try
                    {
                        foreach (var child in cached.CachedChildren) children.Add(new UiaNode(automation, request, child));
                    }
                    catch { /* children unavailable within the timeout */ }
                    return new ScreenNodeSnapshot(false, texts, children);
                }
            }
            catch { return null; }
        }

        static bool SafeBool(Func<bool> read) { try { return read(); } catch { return false; } }
    }
}
