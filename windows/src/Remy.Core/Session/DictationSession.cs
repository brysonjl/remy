using System.Threading.Channels;
using Remy.Core.Contracts;
using Remy.Core.History;
using Remy.Core.Recovery;
using Remy.Core.Speech;

namespace Remy.Core.Session;

enum SessionStage { Preparing, Recording, Stopping, Cleaning, Delivering }

/// <summary>Append-only growing 16 kHz buffer written from the capture thread and read by the preview loop.</summary>
sealed class SampleBuffer
{
    readonly object _lock = new();
    float[] _data = new float[16000 * 30];
    int _count;

    public int Count { get { lock (_lock) return _count; } }

    public void Append(ReadOnlySpan<float> s)
    {
        lock (_lock)
        {
            if (_count + s.Length > _data.Length) Array.Resize(ref _data, Math.Max(_data.Length * 2, _count + s.Length));
            s.CopyTo(_data.AsSpan(_count));
            _count += s.Length;
        }
    }

    public float[] CopyTail(int n)
    {
        lock (_lock)
        {
            n = Math.Min(n, _count);
            var r = new float[n];
            Array.Copy(_data, _count - n, r, 0, n);
            return r;
        }
    }

    public float[] CopyRange(long start, int length)
    {
        lock (_lock)
        {
            length = (int)Math.Min(length, Math.Max(0, _count - start));
            var r = new float[length];
            Array.Copy(_data, start, r, 0, length);
            return r;
        }
    }

    public float[] Snapshot()
    {
        lock (_lock)
        {
            var r = new float[_count];
            Array.Copy(_data, r, _count);
            return r;
        }
    }
}

/// <summary>One dictation. The object identity is the session token: a result is current only while it is the coordinator's session.</summary>
sealed class DictationSession(Guid id, DictationMode mode, long generation)
{
    public Guid Id { get; } = id;
    public long Generation { get; } = generation;
    public DictationMode Mode { get; } = mode;
    public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cts { get; } = new();
    public DeliveryLease Lease { get; } = new();

    public volatile SessionStage Stage = SessionStage.Preparing;
    public volatile bool Cancelling;

    public SessionConfig? Config;
    public DestinationTarget? Origin;
    public IHistoryRepository History = null!;
    public SessionContinuation? Continuation;
    public RecoveryAudioWriter? Recovery;
    public string? RecordingSafetyFailure;
    public Task<CapturedContext?>? ContextTask;

    // screen names (v0.2-features.md 7): in memory only, never stored or logged
    public volatile IReadOnlyList<string>? ScreenNames;
    public CancellationTokenSource? WalkCts;
    public Task? WalkTask;

    /// <summary>Reads whatever the walk has produced so far, then invalidates it. Never waits.</summary>
    public IReadOnlyList<string> TakeScreenNames()
    {
        var names = ScreenNames ?? [];
        CancelWalk();
        return names;
    }

    public void CancelWalk()
    {
        var cts = WalkCts;
        if (cts != null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        ScreenNames = null;
    }

    // audio
    public SampleBuffer Samples { get; } = new();
    public volatile bool Closed;
    public long LastSignalBlock;
    public Channel<bool> PreviewSignal { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    public LivePreviewScheduler Scheduler { get; } = new();
    public PreviewTimeline Timeline { get; } = new();
    public volatile string Transcript = "";
    public CancellationTokenSource LoopCts { get; } = new();
    public Task? PreviewTask, LevelTask;
    public Task? StopCaptureTask;
    public DateTime CaptureStoppedAt = DateTime.MinValue;
    public int AudioRestored;        // Interlocked
    public object? MuteToken, BoostToken;

    // pipeline
    public TaskCompletionSource StartDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? FinishDone;
    public CancellationTokenSource? CleanupCts;
    public volatile bool UseRawRequested;
    public volatile bool CleanupDecided;
    public volatile bool RawSaved;
    public volatile bool OutputSelected;
    public string? CompletedRaw;
    public Guid? DeliveryId;
    public volatile bool OutcomeRecorded;

    public string Prefix => Continuation?.Prefix ?? "";
}
