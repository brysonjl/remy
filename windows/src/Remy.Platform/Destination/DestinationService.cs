using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Remy.Core.Contracts;
using Remy.Platform.Native;

namespace Remy.Platform.Destination;

/// <summary>
/// Foreground window + UIA focused element capture, focus restoration and bounded context capture
/// (pipeline.md 5.3/5.4, cleanup.md 5.3/5.4). Never throws; UIA work is time-boxed.
/// </summary>
public sealed class DestinationService : IDestinationService
{
    public static readonly TimeSpan UiaBudget = TimeSpan.FromMilliseconds(45);
    public static readonly TimeSpan ContextBudget = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan RestoreBudget = TimeSpan.FromMilliseconds(800);

    static readonly string[] SensitiveFragments =
        ["1password", "bitwarden", "dashlane", "enpass", "keepass", "keeper", "lastpass", "nordpass", "password", "proton.pass", "protonpass", "credentialuibroker"];
    static readonly string[] BrowserProcesses = ["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "vivaldi.exe", "opera.exe", "arc.exe"];

    static readonly Lazy<UIA3Automation> Uia = new(() => new UIA3Automation());

    readonly object _cacheGate = new();
    readonly Dictionary<string, AutomationElement> _elements = [];
    readonly Queue<string> _elementOrder = new();

    // ------------------------------------------------------------------ capture

    public DestinationTarget? CaptureForeground()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == 0) return null;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            var path = ProcessInfo.ImagePath(pid);
            var name = path != null ? Path.GetFileName(path).ToLowerInvariant() : "";
            bool isRemy = pid == (uint)Environment.ProcessId;
            bool elevated = ProcessInfo.IsElevatedRelativeToUs(pid);

            string? elementId = null; bool? editable = null; bool secure = false;
            if (!elevated && !isRemy)
            {
                var probe = RunBounded(() => ProbeFocus((int)pid), UiaBudget);
                if (probe != null)
                {
                    elementId = probe.Id; editable = probe.Editable; secure = probe.Secure;
                    if (probe.Element != null && probe.Id != null) Remember(KeyFor(hwnd, probe.Id), probe.Element);
                }
            }

            return new DestinationTarget(hwnd, (int)pid, name, path, ProcessInfo.WindowTitle(hwnd), elementId, editable, secure, elevated, isRemy);
        }
        catch { return null; }
    }

    sealed record FocusProbe(string? Id, bool? Editable, bool Secure, AutomationElement? Element);

    FocusProbe? ProbeFocus(int expectedPid)
    {
        try
        {
            var el = Uia.Value.FocusedElement();
            if (el == null) return null;
            int pid = el.Properties.ProcessId.ValueOrDefault;
            if (pid != expectedPid) return null;
            return new FocusProbe(RuntimeIdOf(el), IsEditable(el), el.Properties.IsPassword.ValueOrDefault, el);
        }
        catch { return null; }
    }

    static string? RuntimeIdOf(AutomationElement el)
    {
        try { var id = el.Properties.RuntimeId.ValueOrDefault; return id is { Length: > 0 } ? string.Join('.', id) : null; }
        catch { return null; }
    }

    /// <summary>Relaxed rule (pipeline.md 5.3): true when UIA positively says editable, false when it says read-only/disabled, otherwise unknown.</summary>
    static bool? IsEditable(AutomationElement el)
    {
        try
        {
            if (el.Properties.IsEnabled.ValueOrDefault == false) return false;
            var value = el.Patterns.Value.PatternOrDefault;
            if (value != null)
            {
                bool ro = value.IsReadOnly.ValueOrDefault;
                return !ro;
            }
            var type = el.Properties.ControlType.ValueOrDefault;
            if (type is ControlType.Edit or ControlType.ComboBox) return true;
            if (type == ControlType.Document && el.Patterns.Text.IsSupported) return null;   // read-only documents exist; cannot tell
            return null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ focus checks

    public bool IsStillFocused(DestinationTarget target)
    {
        try
        {
            if (NativeMethods.GetForegroundWindow() != target.WindowHandle) return false;
            if (target.FocusedElementId == null) return true;
            var now = RunBounded(() => ProbeFocus(target.ProcessId), UiaBudget);
            if (now?.Id == null) return true;             // UIA cannot tell: window match is enough
            return now.Id == target.FocusedElementId;
        }
        catch { return false; }
    }

    public async Task<bool> RestoreFocusAsync(DestinationTarget target, CancellationToken ct)
    {
        try
        {
            var started = Environment.TickCount64;
            bool Expired() => Environment.TickCount64 - started > RestoreBudget.TotalMilliseconds;

            if (!NativeMethods.IsWindow(target.WindowHandle)) return false;
            NativeMethods.GetWindowThreadProcessId(target.WindowHandle, out var pid);
            if ((int)pid != target.ProcessId) return false;            // handle reused by another process
            if (IsStillFocused(target)) return true;

            if (NativeMethods.IsIconic(target.WindowHandle)) NativeMethods.ShowWindow(target.WindowHandle, 9 /* SW_RESTORE */);

            for (int attempt = 0; attempt < 3 && !Expired(); attempt++)
            {
                ct.ThrowIfCancellationRequested();
                ForceForeground(target.WindowHandle, useAltTrick: attempt > 0);
                for (int i = 0; i < 15 && !Expired(); i++)             // up to ~120 ms, 8 ms steps
                {
                    if (NativeMethods.GetForegroundWindow() == target.WindowHandle) break;
                    await Task.Delay(8, ct).ConfigureAwait(false);
                }
                if (NativeMethods.GetForegroundWindow() == target.WindowHandle) break;
            }
            if (NativeMethods.GetForegroundWindow() != target.WindowHandle) return false;

            if (target.FocusedElementId != null)
            {
                AutomationElement? el;
                lock (_cacheGate) _elements.TryGetValue(KeyFor(target.WindowHandle, target.FocusedElementId), out el);
                var current = RunBounded(() => ProbeFocus(target.ProcessId), UiaBudget);
                if (el != null && current?.Id != target.FocusedElementId)
                    RunBounded<object>(() => { try { el.Focus(); } catch { } return new object(); }, TimeSpan.FromMilliseconds(150));
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
            return IsStillFocused(target);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    static void ForceForeground(nint hwnd, bool useAltTrick)
    {
        var fg = NativeMethods.GetForegroundWindow();
        uint fgThread = fg != 0 ? NativeMethods.GetWindowThreadProcessId(fg, out _) : 0;
        uint me = NativeMethods.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && NativeMethods.AttachThreadInput(me, fgThread, true);
        try
        {
            if (useAltTrick) { NativeMethods.keybd_event(0x12, 0, 0, 0); NativeMethods.keybd_event(0x12, 0, NativeMethods.KEYEVENTF_KEYUP, 0); }
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally { if (attached) NativeMethods.AttachThreadInput(me, fgThread, false); }
    }

    // ------------------------------------------------------------------ context capture

    public async Task<CapturedContext?> CaptureContextAsync(DestinationTarget target, ContextCaptureLimits limits, CancellationToken ct)
    {
        try
        {
            if (target.IsSecure || target.IsElevated || target.IsRemy || IsSensitiveApplication(target.ProcessName, target.WindowTitle)) return null;
            var work = Task.Run(() => CaptureContextCore(target, limits, ct), CancellationToken.None);
            var finished = await Task.WhenAny(work, Task.Delay(ContextBudget, ct)).ConfigureAwait(false);
            if (finished != work) { _ = work.ContinueWith(_ => { }, TaskScheduler.Default); return null; }
            return await work.ConfigureAwait(false);
        }
        catch { return null; }
    }

    CapturedContext? CaptureContextCore(DestinationTarget target, ContextCaptureLimits limits, CancellationToken ct)
    {
        string? before = null, after = null, selected = null, label = null, placeholder = null, url = null, host = null;
        try
        {
            var el = Uia.Value.FocusedElement();
            if (el != null && el.Properties.ProcessId.ValueOrDefault == target.ProcessId)
            {
                if (el.Properties.IsPassword.ValueOrDefault) return null;      // secure field: nothing at all
                label = Clip(SafeString(() => el.Properties.Name.ValueOrDefault), limits.MetadataChars);
                placeholder = Clip(SafeString(() => el.Properties.HelpText.ValueOrDefault), limits.MetadataChars);
                ReadCursorText(el, limits.CursorTextChars, out before, out after, out selected);
            }
            ct.ThrowIfCancellationRequested();

            if (Array.IndexOf(BrowserProcesses, target.ProcessName) >= 0)
            {
                var rawUrl = ReadBrowserUrl(target, ct);
                if (rawUrl != null)
                {
                    url = Clip(rawUrl, limits.MetadataChars);
                    host = HostOf(rawUrl);
                }
            }
        }
        catch (OperationCanceledException) { return null; }
        catch { /* partial context is fine */ }

        return new CapturedContext(target.ProcessName, Clip(target.WindowTitle, limits.MetadataChars) ?? "", url, host, label, placeholder, before, after, selected);
    }

    static void ReadCursorText(AutomationElement el, int max, out string? before, out string? after, out string? selected)
    {
        before = after = selected = null;
        try
        {
            var text = el.Patterns.Text.PatternOrDefault;
            if (text == null) return;
            var sels = text.GetSelection();
            if (sels is not { Length: > 0 }) return;
            var sel = sels[0];
            selected = ClipRaw(sel.GetText(max), max);
            if (string.IsNullOrEmpty(selected)) selected = null;

            var b = sel.Clone();
            b.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -max);
            b.MoveEndpointByRange(TextPatternRangeEndpoint.End, sel, TextPatternRangeEndpoint.Start);
            before = b.GetText(max);

            var a = sel.Clone();
            a.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, max);
            a.MoveEndpointByRange(TextPatternRangeEndpoint.Start, sel, TextPatternRangeEndpoint.End);
            after = a.GetText(max);
        }
        catch { /* unreadable stays null, never empty */ }
    }

    static string? ReadBrowserUrl(DestinationTarget target, CancellationToken ct)
    {
        try
        {
            var window = Uia.Value.FromHandle(target.WindowHandle);
            if (window == null) return null;
            foreach (var edit in window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
            {
                ct.ThrowIfCancellationRequested();
                var name = SafeString(() => edit.Properties.Name.ValueOrDefault) ?? "";
                var id = SafeString(() => edit.Properties.AutomationId.ValueOrDefault) ?? "";
                bool isAddressBar = name.Contains("address", StringComparison.OrdinalIgnoreCase)
                    || id is "urlbar-input" or "view_1020" or "view_1022";
                if (!isAddressBar) continue;
                var value = edit.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return null;
    }

    internal static string? HostOf(string url)
    {
        var candidate = url.Contains("://") ? url : "https://" + url;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) ? u.Host : null;
    }

    internal static bool IsSensitiveApplication(string processName, string windowTitle)
    {
        var n = (processName ?? "").ToLowerInvariant();
        foreach (var f in SensitiveFragments) if (n.Contains(f)) return true;
        var t = (windowTitle ?? "").ToLowerInvariant();
        return t is "windows security" or "credential manager";
    }

    // ------------------------------------------------------------------ helpers

    static string KeyFor(nint hwnd, string id) => $"{hwnd}:{id}";

    void Remember(string key, AutomationElement el)
    {
        lock (_cacheGate)
        {
            if (!_elements.ContainsKey(key)) _elementOrder.Enqueue(key);
            _elements[key] = el;
            while (_elementOrder.Count > 8) _elements.Remove(_elementOrder.Dequeue());
        }
    }

    static T? RunBounded<T>(Func<T?> work, TimeSpan budget) where T : class
    {
        try
        {
            var task = Task.Run(work);
            return task.Wait(budget) ? task.Result : null;
        }
        catch { return null; }
    }

    static string? SafeString(Func<string?> read) { try { return read(); } catch { return null; } }

    static string? Clip(string? s, int max) => s == null ? null : (s.Length <= max ? s : s[..max]);
    static string? ClipRaw(string? s, int max) => Clip(s, max);
}
