using System.Buffers.Binary;
using System.Threading.Channels;

namespace Remy.Core.Recovery;

/// <summary>Verbatim user-facing recovery strings (pipeline.md 3.7, 6.4).</summary>
public static class RecoveryMessages
{
    public const string WriterOverflow = "Audio recovery could not keep up. The saved recording may be incomplete.";
    public const string WriterFailed = "Audio recovery could not be written. The saved recording may be incomplete.";
    public const string UnavailablePrefix = "Audio recovery is unavailable for this recording: ";
    public const string InterruptedPreview = "Interrupted recording";
    public const string OrphanCaptureError = "Recovered after an interruption. Check the transcript for missing audio.";
    public const string MetadataNotSaved = "Recovery details could not be saved. Existing audio and this session's text have been kept.";
    public const string MetadataAndHistoryNotSaved = "Recovery details could not be saved. Existing audio was kept; copy the unsaved transcript before quitting Remy.";
    public const string PartialPrefix = "Only part of the cancelled audio was saved: ";
    public const string PartialWarning = "Partial recovery copy — some audio may be unavailable.";
    public const string AudioSavedForRecovery = "Audio saved for recovery";
    public const string SessionsHint = "Continue restores your words and resumes recording. Stop finishes the combined dictation; if the original app is unavailable, the text is copied. Recover text saves it here without pasting. Sessions expire after 24 hours.";
    public const string StorageHint = "With history on, a temporary audio copy protects interrupted or failed dictations. Successful recordings are removed; recovery audio expires after 24 hours.";
    public const string RecoverNeedsHistory = "Turn on Save new dictations in History before recovering this recording. The saved audio is unchanged.";
    public const string NoSpeechInLatestAudio = "No speech was recognized in the latest saved audio. It has been kept so you can try another speech model.";
    public const string NoSpeechRecovered = "No speech was recovered. The recording is still available; try a different speech model.";
    public const string RecoveredNotSaved = "Recovered text was not saved. Copy it from History before quitting. The saved recording has been kept.";
    public const string NoSpeechRestored = "No speech was restored. Try Recover text with a different speech model.";
    public static string RecoveryFailed(string reason) => $"Recovery failed. The saved session is still available. {reason}";
    public static string ContinueFailed(string reason) => $"Could not continue. Your saved session is still available. {reason}";
    public static string ContinueMicFailed(string message) => $"Could not start the microphone. Your saved session is still available. {message}";
    public const string MicRequiredToContinue = "Microphone access is required to continue. Your saved session is still available.";
}

public sealed record RecoveryWriteResult(long SamplesWritten, string? FailureMessage);

/// <summary>
/// Streams 16 kHz mono float samples to a 16-bit PCM WAV. The capture callback only enqueues (<see cref="TryEnqueue"/>);
/// a dedicated thread does file I/O. Admission is bounded to 30 s of queued-not-yet-written audio; overflow or an I/O error
/// sets a sticky failure, stops admitting audio and raises <see cref="Failed"/> so the coordinator can stop recording.
/// </summary>
public sealed class RecoveryAudioWriter : IDisposable
{
    public const int MaxQueuedSamples = 480_000;
    public const int HeaderBytes = 44;
    public const int SampleRate = 16000;

    readonly Channel<float[]> _queue = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task _worker;
    readonly FileStream _file;
    readonly int _maxQueued;
    int _queued;
    long _written;
    string? _failure;
    int _failureRaised;
    bool _completed;
    readonly object _lock = new();

    public string Path { get; }

    /// <summary>Raised once (possibly on the capture or writer thread) with the verbatim failure message.</summary>
    public event Action<string>? Failed;

    public string? FailureMessage => Volatile.Read(ref _failure);

    /// <summary>Creates the file. Throws <see cref="IOException"/> (caller shows UnavailablePrefix + reason and does not start recording).</summary>
    public RecoveryAudioWriter(string path, int maxQueuedSamples = MaxQueuedSamples)
    {
        Path = path;
        _maxQueued = maxQueuedSamples;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
        WriteHeader(_file, 0);
        _file.Flush();
        _worker = Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning);
    }

    /// <summary>Called from the capture thread. Copies the samples. Returns false (and fails the writer) on overflow.</summary>
    public bool TryEnqueue(ReadOnlyMemory<float> samples)
    {
        if (FailureMessage != null) return false;
        if (samples.IsEmpty) return true;
        lock (_lock)
        {
            if (_completed) return false;
            if (Volatile.Read(ref _queued) + samples.Length > _maxQueued) { Fail(RecoveryMessages.WriterOverflow); return false; }
            Interlocked.Add(ref _queued, samples.Length);
            _queue.Writer.TryWrite(samples.ToArray());
        }
        return true;
    }

    void Run()
    {
        var bytes = new byte[0];
        try
        {
            var reader = _queue.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var block))
                {
                    try
                    {
                        if (FailureMessage == null)
                        {
                            if (bytes.Length < block.Length * 2) bytes = new byte[block.Length * 2];
                            for (var i = 0; i < block.Length; i++)
                            {
                                var v = Math.Clamp(block[i], -1f, 1f);
                                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Round(v * 32767f));
                            }
                            _file.Write(bytes, 0, block.Length * 2);
                            Interlocked.Add(ref _written, block.Length);
                        }
                    }
                    catch (Exception) { Fail(RecoveryMessages.WriterFailed); }
                    finally { Interlocked.Add(ref _queued, -block.Length); }
                }
            }
        }
        catch (Exception) { Fail(RecoveryMessages.WriterFailed); }
    }

    void Fail(string message)
    {
        Interlocked.CompareExchange(ref _failure, message, null);
        if (Interlocked.Exchange(ref _failureRaised, 1) == 0) Failed?.Invoke(message);
    }

    /// <summary>Drain the queue, patch the header and close. A failure at this point must be treated as a session failure.</summary>
    public async Task<RecoveryWriteResult> CompleteAsync()
    {
        lock (_lock) { _completed = true; _queue.Writer.TryComplete(); }
        await _worker.ConfigureAwait(false);
        try
        {
            if (_file.CanWrite)
            {
                _file.Flush();
                WriteHeader(_file, Interlocked.Read(ref _written) * 2, seekToStart: true);
                _file.Flush(true);
            }
        }
        catch (Exception) { Fail(RecoveryMessages.WriterFailed); }
        finally { _file.Dispose(); }
        return new RecoveryWriteResult(Interlocked.Read(ref _written), FailureMessage);
    }

    /// <summary>Close without keeping anything (the file stays; callers delete it).</summary>
    public void Dispose()
    {
        if (_completed && !_file.CanWrite) return;
        try { CompleteAsync().GetAwaiter().GetResult(); } catch { }
    }

    internal static void WriteHeader(FileStream f, long dataBytes, bool seekToStart = false)
    {
        if (seekToStart) f.Seek(0, SeekOrigin.Begin);
        Span<byte> h = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)Math.Min(36 + dataBytes, uint.MaxValue));
        "WAVEfmt "u8.CopyTo(h[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);          // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], 1);          // mono
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 16);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], (uint)Math.Min(dataBytes, uint.MaxValue));
        f.Write(h);
    }
}

/// <summary>Reads the recovery WAV (16-bit PCM or 32-bit float, mono, 16 kHz). Tolerates a header left unpatched by a crash.</summary>
public static class RecoveryWavReader
{
    /// <summary>Number of samples, or -1 if the file is not a valid 16 kHz mono recovery WAV.</summary>
    public static long CountSamples(string path)
    {
        try { using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return Probe(f, out _, out _) ? Samples(f, Probe2(f)) : -1; }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
    }

    static (long Start, int BytesPer, long Length) Probe2(FileStream f) { Probe(f, out var start, out var bps); return (start, bps, f.Length - start); }
    static long Samples(FileStream f, (long Start, int BytesPer, long Length) p) => p.Length / p.BytesPer;

    static bool Probe(FileStream f, out long dataStart, out int bytesPerSample)
    {
        dataStart = HeaderBytes; bytesPerSample = 2;
        if (f.Length < HeaderBytes) return false;
        var h = new byte[HeaderBytes];
        f.Seek(0, SeekOrigin.Begin);
        f.ReadExactly(h);
        if (!h.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !h.AsSpan(8, 4).SequenceEqual("WAVE"u8) || !h.AsSpan(12, 4).SequenceEqual("fmt "u8)) return false;
        var fmt = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(20));
        var ch = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(22));
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(24));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(34));
        if (ch != 1 || rate != 16000 || !h.AsSpan(36, 4).SequenceEqual("data"u8)) return false;
        if (fmt == 1 && bits == 16) bytesPerSample = 2;
        else if (fmt == 3 && bits == 32) bytesPerSample = 4;
        else return false;
        return true;
    }

    /// <summary>Stream the audio in blocks (default 10 s) so the final decode can chunk it. Throws InvalidDataException for bad files.</summary>
    public static IEnumerable<float[]> ReadBlocks(string path, int blockSamples = 160_000)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (!Probe(f, out var start, out var bps)) throw new InvalidDataException("Recovery audio is not a 16 kHz mono WAV.");
        f.Seek(start, SeekOrigin.Begin);
        var total = (f.Length - start) / bps;
        var buf = new byte[blockSamples * bps];
        while (total > 0)
        {
            var want = (int)Math.Min(total, blockSamples);
            f.ReadExactly(buf, 0, want * bps);
            var block = new float[want];
            for (var i = 0; i < want; i++)
                block[i] = bps == 2 ? BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(i * 2)) / 32768f : BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(i * 4));
            total -= want;
            yield return block;
        }
    }

    public static float[] ReadAll(string path)
    {
        var n = CountSamples(path);
        if (n < 0) throw new InvalidDataException("Recovery audio is not a 16 kHz mono WAV.");
        var all = new float[n];
        var pos = 0;
        foreach (var b in ReadBlocks(path)) { b.CopyTo(all, pos); pos += b.Length; }
        return all;
    }

    const int HeaderBytes = RecoveryAudioWriter.HeaderBytes;
}
