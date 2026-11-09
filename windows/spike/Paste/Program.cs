// Throwaway spike: can a background process put text into the focused field of common Windows apps?
//
//   Paste <target> [<target> ...]     targets: notepad chrome-textarea chrome-editable vscode terminal slack notepad-admin
//
// For each target the harness opens a scratch window, focuses its text field (standing in for the
// user clicking there), then for each method:
//   uia     - UI Automation ValuePattern.SetValue on the focused element (replaces the field's value)
//   ctrlv   - put text on the clipboard, SendInput Ctrl+V
//   unicode - SendInput KEYEVENTF_UNICODE, one character at a time (extra; common Windows fallback)
// and reads the field back with UIA (ValuePattern / TextPattern). If UIA can't see the text, it falls back
// to Ctrl+A, Ctrl+C after putting a sentinel on the clipboard. Never sends Enter. Before every keystroke
// it checks that the target window is foreground and aborts the trial if not.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

static class Program
{
    static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
    static readonly string Scratch = Path.Combine(Root, ".cache", "paste");
    static readonly UIA3Automation Uia = new();
    static readonly int UnicodeDelayMs = int.TryParse(Environment.GetEnvironmentVariable("REMY_UNICODE_DELAY_MS"), out var d) ? d : 5;
    const string Sentinel = "<<remy-readback-sentinel>>";

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        SetProcessDPIAware();
        Directory.CreateDirectory(Scratch);
        string? savedClipboard = Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        try
        {
            foreach (var target in args) RunTarget(target);
        }
        finally
        {
            Retry(() => { if (savedClipboard != null) Clipboard.SetText(savedClipboard); else Clipboard.Clear(); });
        }
        return 0;
    }

    record Target(IntPtr Hwnd, int Pid, Func<AutomationElement?> Field, bool TerminalLike);

    static void RunTarget(string name)
    {
        Target? t;
        try { t = Setup(name); }
        catch (Exception e) { Emit(new { target = name, stage = "setup", error = e.Message }); return; }
        if (t == null) { Emit(new { target = name, stage = "setup", error = "window not found" }); return; }

        int n = 0;
        foreach (var method in new[] { "uia", "ctrlv", "unicode" })
        {
            string text = $"Remy {method} check {++n} café naïve — done";
            var r = new Dictionary<string, object?> { ["target"] = name, ["method"] = method, ["expected"] = text };
            if (method == "unicode") r["unicode_delay_ms"] = UnicodeDelayMs;
            try { Trial(t, method, text, r); }
            catch (Exception e) { r["error"] = $"{e.GetType().Name}: {e.Message}"; }
            Screenshot(t.Hwnd, Path.Combine(Scratch, $"{name}-{method}.png"));
            Emit(r);
        }
        // Leave the scratch field empty (matters for Slack, where a draft would persist).
        if (Focus(t)) Clear(t);
    }

    static void Trial(Target t, string method, string text, Dictionary<string, object?> r)
    {
        if (!Focus(t)) { r["result"] = "blocked: could not make target foreground/focused"; return; }
        Clear(t);
        Thread.Sleep(300);

        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint fgPid);
        r["harness_is_foreground"] = fgPid == Environment.ProcessId;
        if (fg != t.Hwnd) { r["result"] = "aborted: target not foreground before insert"; return; }

        var focused = SafeFocused();
        r["focused"] = Describe(focused);

        var sw = Stopwatch.StartNew();
        switch (method)
        {
            case "uia":
                if (focused == null) { r["dispatch"] = "no focused element visible to UIA"; break; }
                if (!focused.Patterns.Value.TryGetPattern(out var vp)) { r["dispatch"] = "focused element has no ValuePattern"; break; }
                if (vp.IsReadOnly.ValueOrDefault) { r["dispatch"] = "ValuePattern is read-only"; break; }
                vp.SetValue(vp.Value.ValueOrDefault + text);
                r["dispatch"] = "SetValue returned";
                break;
            case "ctrlv":
                Retry(() => Clipboard.SetText(text));
                r["dispatch"] = $"SendInput sent {Keys((VK_CONTROL, false), ((ushort)'V', false), ((ushort)'V', true), (VK_CONTROL, true))}/4, err={Marshal.GetLastWin32Error()}";
                break;
            case "unicode":
                int sent = 0;
                foreach (char c in text) sent += Unicode(c);
                r["dispatch"] = $"SendInput sent {sent}/{text.Length * 2}, err={Marshal.GetLastWin32Error()}";
                break;
        }
        r["dispatch_ms"] = sw.Elapsed.TotalMilliseconds;
        // Wait until the field stops changing (some apps process injected input slowly), max 5 s.
        Thread.Sleep(500);
        var settleUntil = sw.ElapsedMilliseconds + 5000;
        string? last = ReadUia(SafeFocused());
        while (sw.ElapsedMilliseconds < settleUntil)
        {
            Thread.Sleep(400);
            string? now = ReadUia(SafeFocused());
            if (now == last) break;
            last = now;
        }
        r["settled_ms"] = sw.Elapsed.TotalMilliseconds;

        // Readback 1: UIA on whatever is focused now.
        string? uiaText = ReadUia(SafeFocused());
        r["uia_readback"] = uiaText == null ? null : Trunc(uiaText);
        bool viaUia = uiaText != null && uiaText.Contains(text);

        // Readback 2: select all + copy, only for editors (Ctrl+A/Ctrl+C mean other things in a shell).
        bool viaClip = false;
        if (!viaUia && !t.TerminalLike && GetForegroundWindow() == t.Hwnd)
        {
            Retry(() => Clipboard.SetText(Sentinel));
            Keys((VK_CONTROL, false), ((ushort)'A', false), ((ushort)'A', true), ((ushort)'C', false), ((ushort)'C', true), (VK_CONTROL, true));
            Thread.Sleep(400);
            Keys((VK_RIGHT, false), (VK_RIGHT, true));
            string clip = Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : "") ?? "";
            r["clipboard_readback"] = clip == Sentinel ? "(copy did nothing)" : Trunc(clip);
            viaClip = clip.Contains(text);
        }

        string? observed = viaUia ? uiaText : viaClip ? (string?)r["clipboard_readback"] : null;
        int count = observed == null ? 0 : CountOf(observed, text);
        r["result"] = viaUia || viaClip
            ? (count == 1 ? "inserted" : $"inserted {count}x") + (viaUia ? " (verified by UIA)" : " (verified by Ctrl+A/Ctrl+C)")
            : uiaText == null && t.TerminalLike ? "unverified: no readback available" : "not inserted";
        if (uiaText != null && !viaUia && uiaText.Contains(text.Replace("—", "-"))) r["note"] = "text arrived with em dash altered";
    }

    // ---------------------------------------------------------------- targets

    static Target? Setup(string name)
    {
        switch (name)
        {
            case "notepad":
            case "notepad-admin":
            {
                string file = Path.Combine(Scratch, $"spike-{name}.txt");
                File.WriteAllText(file, "");
                var psi = new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true };
                if (name == "notepad-admin") psi.Verb = "runas"; // UAC prompt: the user must click Yes.
                Process.Start(psi);
                var h = WaitWindow($"spike-{name}", name == "notepad-admin" ? 180 : 20);
                return h == IntPtr.Zero ? null : Make(h, w =>
                    w.FindFirstDescendant(cf => cf.ByControlType(ControlType.Document)) ??
                    w.FindFirstDescendant(cf => cf.ByClassName("RichEditD2DPT")), false);
            }
            case "chrome-textarea":
            case "chrome-editable":
            {
                string page = Path.GetFullPath(Path.Combine(Root, "..", "..", "validation", "destination-baseline", "index.html"));
                var h = FindWindow("Remy insertion fixture");
                if (h == IntPtr.Zero)
                {
                    string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
                    string profile = Path.Combine(Scratch, "chrome-profile");
                    Process.Start(chrome, $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --new-window \"file:///{page.Replace('\\', '/')}\"");
                    h = WaitWindow("Remy insertion fixture", 30);
                }
                string id = name == "chrome-textarea" ? "primary" : "editable";
                return h == IntPtr.Zero ? null : Make(h, w => Poll(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), 10), false);
            }
            case "vscode":
            {
                string file = Path.Combine(Scratch, "spike-vscode.txt");
                File.WriteAllText(file, "");
                string code = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe");
                Process.Start(code, $"-n \"{file}\"");
                var h = WaitWindow("spike-vscode.txt", 40);
                Thread.Sleep(3000); // let the editor finish loading
                return h == IntPtr.Zero ? null : Make(h, w => Poll(() => w.FindFirstDescendant(cf => cf.ByClassName("inputarea monaco-mouse-cursor-text"))
                                                                      ?? w.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)), 10), false);
            }
            case "terminal":
            {
                // A new, separately titled window so nothing can land in an existing terminal session.
                Process.Start(new ProcessStartInfo("wt.exe", "-w new --title RemySpikeTerminal --suppressApplicationTitle powershell.exe -NoLogo -NoProfile") { UseShellExecute = true });
                var h = WaitWindow("RemySpikeTerminal", 30);
                Thread.Sleep(2500); // shell prompt
                return h == IntPtr.Zero ? null : Make(h, w => w.FindFirstDescendant(cf => cf.ByClassName("TermControl")), true);
            }
            case "slack":
            {
                var p = Process.GetProcessesByName("slack").FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);
                if (p == null)
                {
                    Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"slack\slack.exe"));
                    p = Poll(() => Process.GetProcessesByName("slack").FirstOrDefault(x => { x.Refresh(); return x.MainWindowHandle != IntPtr.Zero && x.MainWindowTitle.Length > 0; }), 60);
                    Thread.Sleep(8000); // workspace load
                }
                if (p == null) return null;
                p.Refresh();
                // The message composer is a contenteditable whose accessible name starts with "Message".
                return Make(p.MainWindowHandle, w => Poll(() => w.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                    .FirstOrDefault(e => (e.Properties.Name.ValueOrDefault ?? "").StartsWith("Message", StringComparison.OrdinalIgnoreCase)), 15), false);
            }
        }
        throw new ArgumentException($"unknown target {name}");
    }

    static Target Make(IntPtr h, Func<AutomationElement, AutomationElement?> field, bool terminalLike)
    {
        GetWindowThreadProcessId(h, out uint pid);
        return new Target(h, (int)pid, () => { try { return field(Uia.FromHandle(h)); } catch { return null; } }, terminalLike);
    }

    static bool Focus(Target t)
    {
        if (IsIconic(t.Hwnd)) ShowWindow(t.Hwnd, 9);
        // A background process may not steal foreground; attaching to the current foreground thread allows it.
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _), me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        BringWindowToTop(t.Hwnd);
        SetForegroundWindow(t.Hwnd);
        AttachThreadInput(me, fgThread, false);
        Thread.Sleep(400);
        try { t.Field()?.Focus(); } catch { /* elevated targets refuse UIA; fall through to the foreground check */ }
        Thread.Sleep(300);
        return GetForegroundWindow() == t.Hwnd;
    }

    static void Clear(Target t)
    {
        if (GetForegroundWindow() != t.Hwnd) return;
        if (t.TerminalLike) Keys((VK_ESCAPE, false), (VK_ESCAPE, true)); // PSReadLine: clear the line
        else Keys((VK_CONTROL, false), ((ushort)'A', false), ((ushort)'A', true), (VK_CONTROL, true), (VK_DELETE, false), (VK_DELETE, true));
    }

    // ---------------------------------------------------------------- UIA helpers

    static AutomationElement? SafeFocused() { try { return Uia.FocusedElement(); } catch { return null; } }

    static string? ReadUia(AutomationElement? e)
    {
        if (e == null) return null;
        try
        {
            if (e.Patterns.Value.TryGetPattern(out var vp) && vp.Value.ValueOrDefault is { Length: > 0 } v) return v;
            if (e.Patterns.Text.TryGetPattern(out var tp)) return tp.DocumentRange.GetText(-1);
            return e.Patterns.Value.IsSupported ? "" : null;
        }
        catch { return null; }
    }

    static object? Describe(AutomationElement? e)
    {
        if (e == null) return null;
        try
        {
            return new
            {
                type = e.Properties.ControlType.ValueOrDefault.ToString(), cls = e.Properties.ClassName.ValueOrDefault,
                name = Trunc(e.Properties.Name.ValueOrDefault ?? "", 60), value = e.Patterns.Value.IsSupported, text = e.Patterns.Text.IsSupported,
            };
        }
        catch (Exception x) { return x.GetType().Name; }
    }

    // ---------------------------------------------------------------- misc helpers

    static T? Poll<T>(Func<T?> f, int seconds) where T : class
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { try { if (f() is { } v) return v; } catch { } Thread.Sleep(250); }
        return null;
    }

    static IntPtr WaitWindow(string titlePart, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { var h = FindWindow(titlePart); if (h != IntPtr.Zero) return h; Thread.Sleep(250); }
        return IntPtr.Zero;
    }

    static IntPtr FindWindow(string titlePart)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().Contains(titlePart, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static void Screenshot(IntPtr h, string path)
    {
        try
        {
            GetWindowRect(h, out var rc);
            using var bmp = new Bitmap(rc.Right - rc.Left, rc.Bottom - rc.Top);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(rc.Left, rc.Top, 0, 0, bmp.Size);
            bmp.Save(path, ImageFormat.Png);
        }
        catch { }
    }

    // Other apps (clipboard history, password managers) briefly hold the clipboard open.
    static T? Retry<T>(Func<T?> f) { for (int i = 0; ; i++) { try { return f(); } catch (ExternalException) when (i < 20) { Thread.Sleep(50); } } }
    static void Retry(Action f) => Retry<object>(() => { f(); return null; });

    static int CountOf(string hay, string needle) { int c = 0, i = 0; while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { c++; i += needle.Length; } return c; }
    static string Trunc(string s, int n = 200) => s.Length <= n ? s : "…" + s[^n..];
    static void Emit(object o) => Console.WriteLine(JsonSerializer.Serialize(o, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    // ---------------------------------------------------------------- Win32

    const ushort VK_CONTROL = 0x11, VK_ESCAPE = 0x1B, VK_DELETE = 0x2E, VK_RIGHT = 0x27;
    const uint KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;

    static uint Keys(params (ushort vk, bool up)[] keys)
    {
        var inputs = keys.Select(k => new INPUT { type = 1, ki = new KEYBDINPUT { wVk = k.vk, dwFlags = k.up ? KEYEVENTF_KEYUP : 0 } }).ToArray();
        uint n = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Thread.Sleep(30);
        return n;
    }

    static int Unicode(char c)
    {
        var inputs = new[]
        {
            new INPUT { type = 1, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } },
            new INPUT { type = 1, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } },
        };
        int n = (int)SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        Thread.Sleep(UnicodeDelayMs);
        return n;
    }

    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    // INPUT is a union; pad to the size of MOUSEINPUT so Marshal.SizeOf matches the native struct (40 bytes on x64).
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}
