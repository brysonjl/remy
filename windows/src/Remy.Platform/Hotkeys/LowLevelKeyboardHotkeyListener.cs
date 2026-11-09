using System.Collections.Concurrent;
using Remy.Core.Contracts;
using Remy.Core.Hotkeys;
using Remy.Platform.Native;

namespace Remy.Platform.Hotkeys;

/// <summary>
/// IHotkeyListener on WH_KEYBOARD_LL. Hook thread: message loop + tiny callback (matcher + enqueue).
/// Consumer thread: raises events (tagged with the matcher generation; stale ones are dropped), drives the 25 ms
/// hold-to-cancel ticks, reconciles lost modifier key-ups and runs the hook watchdog.
/// Events are raised on the consumer thread, never inside the hook callback.
/// </summary>
public sealed class LowLevelKeyboardHotkeyListener : IHotkeyListener
{
    const int VkNoOp = 0xFF;
    static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(15);

    readonly HotkeyMatcher _matcher;
    readonly BlockingCollection<(HotkeyOutput Output, int Generation)> _queue = [];
    readonly object _lifecycle = new();
    readonly NativeMethods.LowLevelKeyboardProc _proc;   // kept alive for the native hook

    Thread? _hookThread, _consumerThread;
    uint _hookThreadId;
    nint _hook;
    volatile bool _monitoring;      // Start() called
    volatile bool _running;         // threads alive
    volatile bool _disposed;
    long _lastHookEventTicks = Environment.TickCount64;
    long _probeSentAt;
    bool _wantMonitoring;

    public LowLevelKeyboardHotkeyListener()
    {
        _matcher = new HotkeyMatcher(o => _queue.Add((o, _matcher!.Generation)));
        _proc = HookProc;
    }

    public event EventHandler<HotkeyBinding>? BindingPressed;
    public event EventHandler<HotkeyBinding>? BindingReleased;
    public event EventHandler<double?>? CancelProgressChanged;
    public event EventHandler? CancelRequested;

    public bool CancellationEnabled
    {
        get => _cancelEnabled;
        set { _cancelEnabled = value; _matcher.SetCancellationEnabled(value); }
    }
    bool _cancelEnabled;

    public void Configure(IReadOnlyList<HotkeyBinding> recordingBindings, HotkeyBinding? cancelShortcut, bool holdToCancel)
    {
        _matcher.Configure(recordingBindings, cancelShortcut, holdToCancel);
        _matcher.SetCancellationEnabled(_cancelEnabled);
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            _wantMonitoring = true;
            _monitoring = true;
            EnsureThreads();
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            _wantMonitoring = false;
            _monitoring = false;
            _matcher.Reset();
            if (!_matcher.IsRecording) TearDownThreads();
        }
    }

    public void RequireNeutralModifiers()
    {
        _matcher.SyncModifiers(PhysicalModifiers());
        _matcher.RequireNeutralModifiers();
    }

    public void ResetCancellationGesture() => _matcher.ResetCancellationGesture();

    public Task<HotkeyBinding?> RecordShortcutAsync(bool allowEscape, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<HotkeyBinding?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycle)
        {
            if (_matcher.IsRecording) { tcs.SetResult(null); return tcs.Task; }
            EnsureThreads();
            _matcher.SyncModifiers(PhysicalModifiers());
            _matcher.BeginRecording(allowEscape, r => tcs.TrySetResult(r));
        }
        var reg = ct.Register(() => _matcher.EndRecording(abort: true));
        tcs.Task.ContinueWith(_ =>
        {
            reg.Dispose();
            lock (_lifecycle)
            {
                // Monitoring resumes with the (possibly changed) configuration; the caller re-Configures and Starts as needed.
                if (!_wantMonitoring && !_matcher.IsRecording) TearDownThreads();
            }
        }, TaskScheduler.Default);
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lifecycle) { _matcher.EndRecording(abort: true); TearDownThreads(); }
        _queue.CompleteAdding();
    }

    // ------------------------------------------------------------------ threads

    void EnsureThreads()
    {
        if (_running || _disposed) return;
        _running = true;
        var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() => HookThreadMain(ready)) { IsBackground = true, Name = "Remy.KeyboardHook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        ready.Wait(2000);
        _matcher.SyncModifiers(PhysicalModifiers());
        _consumerThread = new Thread(ConsumerMain) { IsBackground = true, Name = "Remy.HotkeyConsumer" };
        _consumerThread.Start();
    }

    void TearDownThreads()
    {
        if (!_running) return;
        _running = false;
        if (_hookThreadId != 0) NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, 0, 0);
        _hookThread?.Join(1000);
        _hookThread = null;
        _hookThreadId = 0;
        // The consumer polls _running and exits by itself.
    }

    void HookThreadMain(ManualResetEventSlim ready)
    {
        _hookThreadId = NativeMethods.GetCurrentThreadId();
        NativeMethods.PeekMessageW(out _, 0, 0, 0, 0);   // create the message queue before anyone posts to it
        Install();
        ready.Set();
        while (NativeMethods.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == NativeMethods.WM_APP) { Uninstall(); Install(); continue; }
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
        Uninstall();
    }

    void Install()
    {
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandleW(null), 0);
        Interlocked.Exchange(ref _lastHookEventTicks, Environment.TickCount64);
        Interlocked.Exchange(ref _probeSentAt, 0);
    }

    void Uninstall()
    {
        if (_hook != 0) { NativeMethods.UnhookWindowsHookEx(_hook); _hook = 0; }
    }

    // ------------------------------------------------------------------ hook callback (keep tiny)

    nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
            var kb = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            Interlocked.Exchange(ref _lastHookEventTicks, Environment.TickCount64);
            if ((kb.flags & NativeMethods.LLKHF_INJECTED) == 0 && (_monitoring || _matcher.IsRecording))
            {
                bool down = wParam is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                var result = _matcher.Process((int)kb.vkCode, down);
                if (result.SuppressStartMenu) InjectNoOpKey();
                if (result.Swallow) return 1;
            }
        }
        catch
        {
            // Never let an exception escape the hook; fall through to the next hook.
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Win key-up after a Win-containing modifier-only shortcut would open the Start menu; a no-op key between cancels that.</summary>
    static void InjectNoOpKey()
    {
        var inputs = new[]
        {
            new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new NativeMethods.KEYBDINPUT { wVk = VkNoOp } },
            new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, ki = new NativeMethods.KEYBDINPUT { wVk = VkNoOp, dwFlags = NativeMethods.KEYEVENTF_KEYUP } },
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }

    // ------------------------------------------------------------------ consumer

    void ConsumerMain()
    {
        long nextWatchdog = Environment.TickCount64 + (long)WatchdogInterval.TotalMilliseconds;
        while (_running && !_disposed)
        {
            try
            {
                bool tick = _matcher.NeedsTick;
                if (_queue.TryTake(out var item, tick ? 25 : 200))
                {
                    Raise(item);
                    while (_queue.TryTake(out item)) Raise(item);
                }
                if (_matcher.NeedsTick)
                {
                    _matcher.ReconcileModifiers(PhysicalModifiers());
                    _matcher.Tick();
                }
                if (Environment.TickCount64 >= nextWatchdog)
                {
                    nextWatchdog = Environment.TickCount64 + (long)WatchdogInterval.TotalMilliseconds;
                    Watchdog();
                }
            }
            catch (InvalidOperationException) { return; }   // queue completed
            catch { /* handlers must not kill the consumer */ }
        }
    }

    void Raise((HotkeyOutput Output, int Generation) item)
    {
        if (item.Generation != _matcher.Generation) return;   // stale gesture
        var o = item.Output;
        try
        {
            switch (o.Kind)
            {
                case HotkeyOutputKind.Pressed: BindingPressed?.Invoke(this, o.Binding!); break;
                case HotkeyOutputKind.Released: BindingReleased?.Invoke(this, o.Binding!); break;
                case HotkeyOutputKind.CancelProgress: CancelProgressChanged?.Invoke(this, o.Progress); break;
                case HotkeyOutputKind.CancelRequested: CancelRequested?.Invoke(this, EventArgs.Empty); break;
            }
        }
        catch { /* subscriber errors are theirs */ }
    }

    /// <summary>Windows silently unhooks a slow LL hook (LowLevelHooksTimeout). Probe with an injected no-op key; reinstall when it is not seen.</summary>
    void Watchdog()
    {
        if (_hookThreadId == 0) return;
        long sentAt = Interlocked.Read(ref _probeSentAt);
        long lastSeen = Interlocked.Read(ref _lastHookEventTicks);
        if (sentAt != 0 && lastSeen < sentAt)
        {
            NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_APP, 0, 0);   // reinstall
            _matcher.SyncModifiers(PhysicalModifiers());
            Interlocked.Exchange(ref _probeSentAt, 0);
            return;
        }
        Interlocked.Exchange(ref _probeSentAt, Environment.TickCount64);
        InjectNoOpKey();
    }

    // ------------------------------------------------------------------ physical modifier state

    internal static HotkeyModifiers PhysicalModifiers()
    {
        var m = HotkeyModifiers.None;
        foreach (var (vk, bit) in HotkeyMatcher.AllModifierKeys)
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0) m |= bit;
        return m;
    }
}
