using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Remy.Platform.Audio;

/// <summary>
/// One persistent mono resampler to 16 kHz (never reset between blocks). <see cref="Flush"/> drains the filter tail once at the end
/// and trims the output so the total length equals round(totalInput * 16000 / sourceRate).
/// </summary>
public sealed class StreamingResampler
{
    public const int TargetRate = 16000;
    const int FlushPaddingFrames = 512;

    readonly int _sourceRate;
    readonly Queue? _queue;
    readonly WdlResamplingSampleProvider? _resampler;
    long _totalIn, _totalOut;

    public StreamingResampler(int sourceRate)
    {
        _sourceRate = sourceRate;
        if (sourceRate == TargetRate) return;
        _queue = new Queue(WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 1));
        _resampler = new WdlResamplingSampleProvider(_queue, TargetRate);
    }

    public float[] Process(ReadOnlySpan<float> mono)
    {
        if (mono.Length == 0) return [];
        _totalIn += mono.Length;
        if (_resampler == null) { _totalOut += mono.Length; return mono.ToArray(); }
        _queue!.Enqueue(mono);
        return Drain(maxOut: (int)((long)_queue.Available * TargetRate / _sourceRate) + 64, limit: long.MaxValue);
    }

    public float[] Flush()
    {
        if (_resampler == null) return [];
        _queue!.Enqueue(new float[FlushPaddingFrames]);
        long expectedTotal = (long)Math.Round(_totalIn * (double)TargetRate / _sourceRate);
        var tail = Drain(maxOut: FlushPaddingFrames * TargetRate / _sourceRate + 64, limit: expectedTotal - _totalOut);
        return tail;
    }

    float[] Drain(int maxOut, long limit)
    {
        if (limit <= 0) { return []; }
        var buf = new float[maxOut];
        var list = new List<float>();
        while (true)
        {
            int n = _resampler!.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            list.AddRange(buf.AsSpan(0, n).ToArray());
            if (n < buf.Length) break;
        }
        if (list.Count > limit) list.RemoveRange((int)limit, list.Count - (int)limit);
        _totalOut += list.Count;
        return list.ToArray();
    }

    /// <summary>Mono float source that returns only what has been enqueued (and 0 when empty).</summary>
    sealed class Queue(WaveFormat format) : ISampleProvider
    {
        readonly Queue<float> _q = new();
        public WaveFormat WaveFormat { get; } = format;
        public int Available => _q.Count;
        public void Enqueue(ReadOnlySpan<float> s) { foreach (var f in s) _q.Enqueue(f); }
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _q.Count);
            for (int i = 0; i < n; i++) buffer[offset + i] = _q.Dequeue();
            return n;
        }
    }
}
