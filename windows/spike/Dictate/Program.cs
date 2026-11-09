// Throwaway try-it demo: press the hotkey (first free of Ctrl+Alt+Space, Ctrl+Shift+Space,
// Ctrl+Alt+D, F9), talk, press it again. The speech is transcribed
// on the CPU (Parakeet v2 int8 via sherpa-onnx) and pasted into whatever window has focus with
// Ctrl+V, the method that worked everywhere in the paste spike. No cleanup, no settings.
//
//   Dictate.exe [modelDir]   (defaults to ..\..\.cache\models\...-int8 found by walking up)

using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using SherpaOnnx;

// The clipboard needs an STA thread; top-level statements don't get [STAThread], so run the UI on one.
var ui = new Thread(() =>
{
    ApplicationConfiguration.Initialize();
    Application.Run(new DictateForm(args.FirstOrDefault() ?? FindModel()));
});
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
ui.Join();

static string FindModel()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
    {
        string p = Path.Combine(d.FullName, ".cache", "models", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8");
        if (Directory.Exists(p)) return p;
    }
    throw new DirectoryNotFoundException("Model not found; pass the model folder as the first argument.");
}

sealed class DictateForm : Form
{
    const int HotkeyId = 1, MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000, WM_HOTKEY = 0x312;
    static readonly (int mods, Keys key, string name)[] Hotkeys =
    {
        (MOD_CONTROL | MOD_ALT, Keys.Space, "Ctrl+Alt+Space"),
        (MOD_CONTROL | MOD_SHIFT, Keys.Space, "Ctrl+Shift+Space"),
        (MOD_CONTROL | MOD_ALT, Keys.D, "Ctrl+Alt+D"),
        (0, Keys.F9, "F9"),
    };
    string hotkey = "(none)";

    readonly Label status = new() { Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 11), Padding = new Padding(6, 4, 0, 0) };
    readonly TextBox history = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 9.5f) };
    readonly System.Windows.Forms.Timer tick = new() { Interval = 100 };
    readonly ComboBox micPicker = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    float peak;
    readonly string modelDir;
    readonly string logPath = Path.Combine(AppContext.BaseDirectory, "dictate.log");
    OfflineRecognizer? recognizer;
    WaveInEvent? mic;
    List<float> samples = new();
    readonly Stopwatch recording = new();
    bool busy;

    public DictateForm(string modelDir)
    {
        this.modelDir = modelDir;
        Text = "Remy spike dictation";
        Size = new Size(520, 280);
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        // -1 is WAVE_MAPPER, which follows the Windows default recording device.
        micPicker.Items.Add(new MicItem(-1, "Windows default microphone"));
        for (int i = 0; i < WaveInEvent.DeviceCount; i++) micPicker.Items.Add(new MicItem(i, WaveInEvent.GetCapabilities(i).ProductName));
        micPicker.SelectedIndex = 0;
        Controls.Add(history);
        Controls.Add(status);
        Controls.Add(micPicker);
        tick.Tick += (_, _) => { if (recording.IsRunning) SetStatus($"● Recording {recording.Elapsed.TotalSeconds:F1}s  level {LevelBar(peak)}  {hotkey} to stop", Color.Firebrick); };
        SetStatus("Loading model...", Color.DimGray);
    }

    protected override bool ShowWithoutActivation => true;

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        foreach (var h in Hotkeys)
            if (RegisterHotKey(Handle, HotkeyId, h.mods | MOD_NOREPEAT, (uint)h.key)) { hotkey = h.name; break; }
        Text = $"Remy spike dictation - {hotkey}";
        var sw = Stopwatch.StartNew();
        recognizer = await Task.Run(() => LoadRecognizer(modelDir));
        await Task.Run(() => Decode(recognizer, new float[16000])); // warm-up
        SetStatus($"Ready (model loaded in {sw.Elapsed.TotalSeconds:F1}s). Click into any app, press {hotkey}.", hotkey == "(none)" ? Color.Firebrick : Color.DarkGreen);
        Log($"loaded model={modelDir} in {sw.ElapsedMilliseconds} ms");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == HotkeyId) Toggle();
        base.WndProc(ref m);
    }

    void Toggle()
    {
        if (recognizer == null || busy) return;
        if (mic == null) StartRecording(); else _ = StopAndPaste();
    }

    void StartRecording()
    {
        samples = new List<float>(16000 * 60);
        peak = 0;
        var dev = (MicItem)micPicker.SelectedItem!;
        mic = new WaveInEvent { DeviceNumber = dev.Number, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
        mic.DataAvailable += (_, a) =>
        {
            lock (samples)
                for (int i = 0; i + 1 < a.BytesRecorded; i += 2)
                {
                    float v = BitConverter.ToInt16(a.Buffer, i) / 32768f;
                    samples.Add(v);
                    peak = Math.Max(peak * 0.9995f, Math.Abs(v));
                }
        };
        try { mic.StartRecording(); }
        catch (Exception ex) { mic.Dispose(); mic = null; SetStatus("Mic error: " + ex.Message, Color.Firebrick); return; }
        System.Media.SystemSounds.Asterisk.Play();
        recording.Restart();
        tick.Start();
    }

    async Task StopAndPaste()
    {
        busy = true;
        try { await StopAndPasteCore(); }
        catch (Exception ex)
        {
            // An unhandled error here used to vanish and leave the window stuck on "Transcribing".
            SetStatus("Error: " + ex.Message, Color.Firebrick);
            history.AppendText($"[error] {ex}\r\n");
            Log("error " + ex);
        }
        finally { busy = false; }
    }

    async Task StopAndPasteCore()
    {
        mic!.StopRecording();
        mic.Dispose();
        mic = null;
        recording.Stop();
        tick.Stop();
        float[] audio;
        lock (samples) audio = samples.ToArray();
        // Test hook: REMY_FAKE_WAV replaces the mic recording with a 16 kHz mono WAV file.
        if (Environment.GetEnvironmentVariable("REMY_FAKE_WAV") is { Length: > 0 } fake) audio = ReadWav(fake);
        double audioSec = audio.Length / 16000.0;
        float maxLevel = audio.Length == 0 ? 0 : audio.Max(Math.Abs);
        string micName = micPicker.Text;
        SetStatus($"Transcribing {audioSec:F1}s...", Color.DarkOrange);
        Log($"stop mic=\"{micName}\" peak={maxLevel:F4} audio_s={audioSec:F2}");

        var sw = Stopwatch.StartNew();
        string text = (await Task.Run(() => DecodeChunked(recognizer!, audio, 20))).Trim();
        sw.Stop();

        string pasted = maxLevel < 0.003f ? $"not pasted (the mic was silent - pick another mic above)" : "not pasted (nothing heard)";
        if (text.Length > 0)
        {
            IntPtr target = GetForegroundWindow();
            if (target == Handle) pasted = "not pasted (this window had focus; copy it from below)";
            else { await Paste(text); pasted = "pasted"; }
        }
        SetStatus($"{audioSec:F1}s of audio → {sw.Elapsed.TotalSeconds:F2}s to transcribe, {pasted}", Color.DarkGreen);
        history.AppendText($"[{audioSec:F1}s → {sw.Elapsed.TotalSeconds:F2}s] {text}\r\n");
        Log($"mic=\"{micName}\" peak={maxLevel:F4} audio_s={audioSec:F2} decode_ms={sw.ElapsedMilliseconds} {pasted} text={text}");
    }

    static async Task Paste(string text)
    {
        // Wait for the hotkey's Ctrl/Alt to be released, or the target would see Ctrl+Alt+V.
        for (int i = 0; i < 40 && new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0); i++)
            await Task.Delay(50);
        string? saved = Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        Retry(() => Clipboard.SetText(text));
        SendKeys(0x11, 'V');
        await Task.Delay(400); // let the target read the clipboard before restoring it
        Retry(() => { if (saved != null) Clipboard.SetText(saved); else Clipboard.Clear(); });
    }

    static float[] ReadWav(string path)
    {
        // 16-bit PCM mono 16 kHz only.
        using var br = new BinaryReader(File.OpenRead(path));
        br.ReadBytes(12);
        while (true)
        {
            string id = new(br.ReadChars(4)); int size = br.ReadInt32();
            if (id != "data") { br.BaseStream.Seek(size + (size & 1), SeekOrigin.Current); continue; }
            var data = new float[size / 2];
            for (int i = 0; i < data.Length; i++) data[i] = br.ReadInt16() / 32768f;
            return data;
        }
    }

    static string LevelBar(float p) => new string('|', (int)Math.Clamp(Math.Sqrt(p) * 20, 0, 20)).PadRight(20, '.');
    sealed record MicItem(int Number, string Name) { public override string ToString() => Name; }

    void SetStatus(string s, Color c) { status.Text = s; status.ForeColor = c; }
    void Log(string s) { try { File.AppendAllText(logPath, $"{DateTime.Now:s} {s}\r\n"); } catch (IOException) { } }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyId);
        mic?.Dispose();
        recognizer?.Dispose();
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------------ speech (same setup as Speech/)

    static OfflineRecognizer LoadRecognizer(string modelDir)
    {
        string Find(string prefix) => Directory.GetFiles(modelDir, prefix + "*.onnx").OrderBy(f => f.Length).First();
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Find("encoder");
        config.ModelConfig.Transducer.Decoder = Find("decoder");
        config.ModelConfig.Transducer.Joiner = Find("joiner");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(config);
    }

    static string Decode(OfflineRecognizer recognizer, float[] samples)
    {
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(16000, samples);
        recognizer.Decode(stream);
        return stream.Result.Text;
    }

    static string DecodeChunked(OfflineRecognizer recognizer, float[] samples, double chunkSeconds)
    {
        // Cut near every chunkSeconds at the quietest 100 ms window within +/-25% of the target.
        int target = (int)(chunkSeconds * 16000), win = 1600, start = 0;
        var parts = new List<string>();
        while (start < samples.Length)
        {
            int end = samples.Length;
            if (samples.Length - start > target * 1.25)
            {
                double best = double.MaxValue;
                for (int c = start + (int)(target * 0.75); c + win < start + (int)(target * 1.25); c += 160)
                {
                    double e = 0;
                    for (int i = c; i < c + win; i++) e += samples[i] * samples[i];
                    if (e < best) { best = e; end = c + win / 2; }
                }
            }
            parts.Add(Decode(recognizer, samples[start..end]).Trim());
            start = end;
        }
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    // ------------------------------------------------------------ win32

    static T? Retry<T>(Func<T?> f) { for (int i = 0; ; i++) { try { return f(); } catch (ExternalException) when (i < 20) { Thread.Sleep(50); } } }
    static void Retry(Action f) => Retry<object>(() => { f(); return null; });

    static void SendKeys(ushort modifier, char key)
    {
        const uint KEYUP = 2;
        var inputs = new[] { (modifier, 0u), ((ushort)key, 0u), ((ushort)key, KEYUP), (modifier, KEYUP) }
            .Select(k => new INPUT { type = 1, ki = new KEYBDINPUT { wVk = k.Item1, dwFlags = k.Item2 } }).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, int mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
}
