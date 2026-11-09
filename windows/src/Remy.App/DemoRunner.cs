using ThemeMode = Remy.App.ViewModels.ThemeMode;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Remy.App.Overlay;
using Remy.App.Services;
using Remy.App.ViewModels;

namespace Remy.App;

/// <summary>`Remy.exe --demo`: cycles the overlay through every state and size every ~1.5 s with a fake level signal.</summary>
public sealed class DemoRunner(CompositionRoot root)
{
    public const string SampleText = "A little more room for your train of thought, so a quick glance shows exactly what was heard. And then a little more so the text has to scroll up past the top line.";
    readonly Stopwatch _clock = Stopwatch.StartNew();
    int _step;
    DispatcherTimer? _level, _step_t;

    public static IEnumerable<(OverlayState State, bool Text)> States()
    {
        yield return (OverlayState.Listening, true);
        yield return (OverlayState.Listening, false);
        yield return (OverlayState.Processing, true);
        yield return (OverlayState.Cleaning, true);
        yield return (OverlayState.Preparing, true);
        yield return (OverlayState.Success, true);
        yield return (OverlayState.Busy, true);
        yield return (OverlayState.CopyOnly, true);
        yield return (OverlayState.Error, true);
        yield return (OverlayState.HoldToCancel, true);
    }

    public void Start()
    {
        var vm = root.OverlayVm;
        root.Shell.AttentionIssues.Add(AttentionIssue.SpeechSetup(false, () => root.Shell.Navigate(AppPage.SpeechModel)));
        root.Shell.AttentionIssues.Add(AttentionIssue.Cleanup(CleanupProblem.OllamaModelMissing, () => root.Shell.Navigate(AppPage.TranscriptCleanup)));
        root.Shell.ServiceStatus = ServiceStatus.ReadyToDictate;
        vm.PreparingText = "Loading Parakeet v2…";
        vm.ErrorMessage = "Cleanup timed out"; vm.ErrorDetail = "Raw transcript · Press Ctrl+V";

        // fake level signal, ~33 ms
        _level = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _level.Tick += (_, _) =>
        {
            double t = _clock.Elapsed.TotalSeconds;
            double raw = Math.Clamp(0.45 + 0.4 * Math.Sin(t * 3.1) * Math.Sin(t * 0.9) + 0.1 * Math.Sin(t * 11), 0, 1);
            vm.Level = vm.Level * (raw > vm.Level ? 0.08 : 0.65) + raw * (raw > vm.Level ? 0.92 : 0.35);
            if (vm.State == OverlayState.Listening && vm.ShowTextPreview)
            {
                int n = Math.Min(SampleText.Length, (int)(t % 9 * 22)); // restarts every 9 s
                vm.TranscriptText = SampleText[..n];
            }
            if (vm.State == OverlayState.HoldToCancel) vm.HoldProgress = (t % 1.5) / 1.5;
        };
        _level.Start();

        _step_t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _step_t.Tick += (_, _) => Advance();
        _step_t.Start();
        Advance();
    }

    void Advance()
    {
        var states = States().ToList();
        var sizes = Enum.GetValues<OverlaySize>();
        int i = _step++ % (states.Count * sizes.Length);
        var size = sizes[i / states.Count];
        var (state, text) = states[i % states.Count];
        var vm = root.OverlayVm;
        if (i % states.Count == 0) root.Overlay.BeginSession();
        vm.Size = size; vm.ShowTextPreview = text;
        root.Shell.ServiceStatus = state switch
        {
            OverlayState.Listening => ServiceStatus.Listening,
            OverlayState.Processing or OverlayState.Cleaning => ServiceStatus.Transcribing,
            OverlayState.Error => ServiceStatus.Error, _ => ServiceStatus.ReadyToDictate,
        };
        if (state == OverlayState.Listening) vm.TranscriptText = "";
        vm.State = state;
        root.Main.Title = $"Remy - demo: {size} / {state}{(state == OverlayState.Listening ? (text ? " (text)" : " (waveform only)") : "")}";
    }
}

/// <summary>`Remy.exe --snap DIR`: renders the main window and every overlay state to PNG files, then exits.</summary>
public sealed class SnapshotRunner(CompositionRoot root, string dir)
{
    static async Task Idle(int ms) => await Task.Delay(ms);

    public async Task RunAsync()
    {
        try
        {
            Directory.CreateDirectory(dir);
            var shell = root.Shell;
            await Idle(600);
            foreach (var (mode, name) in new[] { (ThemeMode.Dark, "dark"), (ThemeMode.Light, "light") })
            {
                ThemeManager.Apply(mode);
                shell.ServiceStatus = ServiceStatus.ReadyToDictate;
                shell.AttentionIssues.Clear();
                shell.Navigate(AppPage.SpeechModel);
                await Idle(500);
                Save(Render(root.Main.Content as FrameworkElement, root.Main), Path.Combine(dir, $"main-{name}.png"));
                shell.AttentionIssues.Add(AttentionIssue.SpeechSetup(false, () => { }));
                shell.AttentionIssues.Add(AttentionIssue.Cleanup(CleanupProblem.OllamaModelMissing, () => { }));
                shell.ServiceStatus = ServiceStatus.SetupNeeded;
                shell.Navigate(AppPage.TranscriptCleanup);
                await Idle(500);
                Save(Render(root.Main.Content as FrameworkElement, root.Main), Path.Combine(dir, $"main-{name}-banner.png"));
                shell.AttentionIssues.Clear();

                // overlay sheets
                var vm = root.OverlayVm;
                vm.ThemeMode = mode;
                vm.PreparingText = "Loading Parakeet v2…"; vm.ErrorMessage = "Cleanup timed out"; vm.ErrorDetail = "Raw transcript · Press Ctrl+V";
                foreach (var size in Enum.GetValues<OverlaySize>())
                {
                    var cells = new List<RenderTargetBitmap>();
                    vm.Size = size;
                    foreach (var (state, text) in DemoRunner.States())
                    {
                        vm.Hide(); await Idle(450);
                        root.Overlay.BeginSession();
                        vm.ShowTextPreview = text; vm.Level = 0.6; vm.HoldProgress = 0.55;
                        vm.TranscriptText = state == OverlayState.Listening && text ? (size == OverlaySize.Compact ? "Let's get ready to publish this repo." : DemoRunner.SampleText) : "";
                        vm.State = state;
                        await Idle(900);
                        cells.Add(Render(((OverlayPill)root.Overlay.Content), root.Overlay));
                    }
                    Save(Sheet(cells, mode == ThemeMode.Dark), Path.Combine(dir, $"overlay-{size.ToString().ToLowerInvariant()}-{name}.png"));
                    vm.Hide(); await Idle(450);
                }
            }
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "snap-error.txt"), ex.ToString()); }
        (Application.Current as App)?.QuitApp();
    }

    static RenderTargetBitmap Render(FrameworkElement? el, Window w)
    {
        var dpi = VisualTreeHelper.GetDpi(w);
        el!.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(el.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(el);
        return rtb;
    }

    static RenderTargetBitmap Sheet(List<RenderTargetBitmap> cells, bool dark)
    {
        int cols = 4, cw = 560, ch = 260, rows = (cells.Count + cols - 1) / cols;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2F, 0x36) : Color.FromRgb(0xC9, 0xCE, 0xD6)), null, new Rect(0, 0, cols * cw, rows * ch));
            for (int i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                double x = (i % cols) * cw + (cw - c.PixelWidth) / 2.0, y = (i / cols) * ch + ch - c.PixelHeight - 8;
                dc.DrawImage(c, new Rect(x, y, c.PixelWidth, c.PixelHeight));
            }
        }
        var rtb = new RenderTargetBitmap(cols * cw, rows * ch, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path); enc.Save(f);
    }
}
