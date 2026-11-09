using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Remy.Core.Contracts;
using Remy.Platform.Destination;
using Remy.Platform.Native;

namespace Remy.Platform.Delivery;

/// <summary>
/// Clipboard + synthetic Ctrl+V on one dedicated STA worker (pipeline.md 5.5). The clipboard is left holding the dictated text.
/// A paste is never retried by this class.
/// </summary>
public sealed class ClipboardDelivery : IClipboardDelivery, IDisposable
{
    const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x2;
    const ushort VK_CONTROL = 0x11, VK_V = 0x56;
    static readonly TimeSpan ModifierWait = TimeSpan.FromSeconds(1);
    static readonly int[] PhysicalModifierKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C];   // Shift, Ctrl, Alt, LWin, RWin

    readonly BlockingCollection<Action> _work = [];
    readonly Thread _thread;

    public ClipboardDelivery()
    {
        _thread = new Thread(() =>
        {
            foreach (var job in _work.GetConsumingEnumerable()) { try { job(); } catch { } }
        }) { IsBackground = true, Name = "Remy.Clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public uint ClipboardSequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    Task<T> OnWorker<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { tcs.SetResult(func()); } catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    public Task<uint?> CopyAsync(string text) => OnWorker<uint?>(() => CopyCore(text));

    public Task<string?> ReadTextAsync() => OnWorker<string?>(ReadCore);

    static bool OpenWithRetry()
    {
        for (int i = 0; i < 20; i++)
        {
            if (NativeMethods.OpenClipboard(0)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    static uint? CopyCore(string text)
    {
        if (!OpenWithRetry()) return null;
        try
        {
            if (!NativeMethods.EmptyClipboard()) return null;
            var bytes = (text.Length + 1) * 2;
            var mem = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
            if (mem == 0) return null;
            var ptr = NativeMethods.GlobalLock(mem);
            if (ptr == 0) { NativeMethods.GlobalFree(mem); return null; }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0);
            }
            finally { NativeMethods.GlobalUnlock(mem); }
            if (NativeMethods.SetClipboardData(CF_UNICODETEXT, mem) == 0) { NativeMethods.GlobalFree(mem); return null; }   // success: system owns mem
        }
        finally { NativeMethods.CloseClipboard(); }
        return NativeMethods.GetClipboardSequenceNumber();
    }

    static string? ReadCore()
    {
        if (!OpenWithRetry()) return null;
        try
        {
            if (!NativeMethods.IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            var h = NativeMethods.GetClipboardData(CF_UNICODETEXT);
            if (h == 0) return null;
            var p = NativeMethods.GlobalLock(h);
            if (p == 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { NativeMethods.GlobalUnlock(h); }
        }
        finally { NativeMethods.CloseClipboard(); }
    }

    public Task<PasteDispatchResult> SendPasteAsync(CancellationToken ct) => OnWorker(() => PasteCore(ct));

    static PasteDispatchResult PasteCore(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)ModifierWait.TotalMilliseconds;
        while (AnyModifierDown())
        {
            if (ct.IsCancellationRequested) return PasteDispatchResult.Failed;
            if (Environment.TickCount64 >= deadline) return PasteDispatchResult.BlockedModifiersHeld;
            Thread.Sleep(10);
        }
        if (ct.IsCancellationRequested) return PasteDispatchResult.Failed;

        var fg = NativeMethods.GetForegroundWindow();
        if (fg == 0) return PasteDispatchResult.BlockedNotForeground;
        NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        if (pid != 0 && ProcessInfo.IsElevatedRelativeToUs(pid)) return PasteDispatchResult.BlockedElevated;

        var inputs = BuildPasteInputs();
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        return sent == inputs.Length ? PasteDispatchResult.Dispatched : PasteDispatchResult.Failed;
    }

    internal static NativeMethods.INPUT[] BuildPasteInputs() =>
    [
        NativeMethods.Key(VK_CONTROL, up: false),
        NativeMethods.Key(VK_V, up: false),
        NativeMethods.Key(VK_V, up: true),
        NativeMethods.Key(VK_CONTROL, up: true),
    ];

    static bool AnyModifierDown()
    {
        foreach (var vk in PhysicalModifierKeys)
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    public void Dispose() => _work.CompleteAdding();
}
