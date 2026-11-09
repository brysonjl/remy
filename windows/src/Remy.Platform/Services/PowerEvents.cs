using System.Runtime.InteropServices;
using Remy.Core.Contracts;
using Remy.Platform.Native;

namespace Remy.Platform.Services;

/// <summary>
/// Hidden top-level window on its own thread: WM_POWERBROADCAST suspend/resume and WTS session lock/unlock.
/// <see cref="Suspending"/> fires when the machine first becomes unavailable (sleep or lock); <see cref="Resumed"/> fires when
/// neither is in effect any more. Events are raised on the window thread.
/// </summary>
public sealed class PowerEvents : IPowerEvents, IDisposable
{
    const uint WM_POWERBROADCAST = 0x218, WM_WTSSESSION_CHANGE = 0x2B1, WM_CLOSE = 0x10;
    const int PBT_APMSUSPEND = 4, PBT_APMRESUMESUSPEND = 7, PBT_APMRESUMEAUTOMATIC = 0x12;
    const int WTS_SESSION_LOCK = 7, WTS_SESSION_UNLOCK = 8;

    readonly object _gate = new();
    bool _sleeping, _locked;
    nint _hwnd;
    uint _threadId;
    readonly Thread _thread;
    readonly NativeMethods.WndProc _wndProc;       // keep alive
    readonly ManualResetEventSlim _ready = new();

    public event EventHandler? Suspending;
    public event EventHandler? Resumed;

    public PowerEvents()
    {
        _wndProc = WndProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Remy.PowerEvents" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(2000);
    }

    void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        var inst = NativeMethods.GetModuleHandleW(null);
        var cls = "RemyPowerEvents_" + Guid.NewGuid().ToString("N");
        var wc = new NativeMethods.WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = inst,
            lpszClassName = cls,
        };
        NativeMethods.RegisterClassW(ref wc);
        // Not HWND_MESSAGE: message-only windows do not receive broadcast messages.
        _hwnd = NativeMethods.CreateWindowExW(0x80 /* WS_EX_TOOLWINDOW */, cls, "Remy power events", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, inst, 0);
        if (_hwnd != 0) NativeMethods.WTSRegisterSessionNotification(_hwnd, 0 /* NOTIFY_FOR_THIS_SESSION */);
        _ready.Set();
        while (NativeMethods.GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
    }

    nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_POWERBROADCAST)
            {
                switch ((int)wParam)
                {
                    case PBT_APMSUSPEND: Update(() => _sleeping = true); break;
                    case PBT_APMRESUMEAUTOMATIC:
                    case PBT_APMRESUMESUSPEND: Update(() => _sleeping = false); break;
                }
            }
            else if (msg == WM_WTSSESSION_CHANGE)
            {
                switch ((int)wParam)
                {
                    case WTS_SESSION_LOCK: Update(() => _locked = true); break;
                    case WTS_SESSION_UNLOCK: Update(() => _locked = false); break;
                }
            }
            else if (msg == WM_CLOSE) { NativeMethods.DestroyWindow(hwnd); return 0; }
            else if (msg == 0x2 /* WM_DESTROY */) { NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, 0, 0); }
        }
        catch { }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    void Update(Action change)
    {
        bool raiseSuspend = false, raiseResume = false;
        lock (_gate)
        {
            bool wasAvailable = !_sleeping && !_locked;
            change();
            bool isAvailable = !_sleeping && !_locked;
            raiseSuspend = wasAvailable && !isAvailable;
            raiseResume = !wasAvailable && isAvailable;
        }
        if (raiseSuspend) Suspending?.Invoke(this, EventArgs.Empty);
        if (raiseResume) Resumed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            NativeMethods.WTSUnRegisterSessionNotification(_hwnd);
            NativeMethods.PostMessageW(_hwnd, WM_CLOSE, 0, 0);
        }
    }
}
