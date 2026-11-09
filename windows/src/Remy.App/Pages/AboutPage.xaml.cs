using System.Diagnostics;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Remy.App.Services;
using Remy.Core.Models;

namespace Remy.App.Pages;

/// <summary>One Open Source Acknowledgements row (ui.md 3.7, Windows dependency set).</summary>
public sealed record LibraryInfo(string Name, string Url, string License, string Description, bool IsFirst = false)
{
    public string UrlText => Url.StartsWith("https://", StringComparison.Ordinal) ? Url[8..] : Url;
    public string LinkName => $"{Name} project website";
}

public partial class AboutPage : UserControl, IServicePage
{
    public static ICommand OpenLink { get; } = new RelayCommand<string>(url =>
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.Ordinal)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    });

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"Version {VersionString()}";
        var parakeet = ModelCatalog.ParakeetV2;
        LibraryInfo[] libs =
        [
            new("Dictate Anywhere", "https://github.com/hoomanaskari/mac-dictate-anywhere", "MIT License",
                "Original application by Hooman Askari. Copyright (c) 2026 Hooman Askari. Remy retains the upstream MIT license and notices.", IsFirst: true),
            new("sherpa-onnx", "https://github.com/k2-fsa/sherpa-onnx", "Apache License 2.0",
                "On-device speech recognition runtime used to run the Parakeet model."),
            new("Parakeet v2 by NVIDIA", $"https://huggingface.co/{parakeet.Repo}/tree/{parakeet.Revision}", "CC-BY-4.0",
                "NVIDIA Parakeet TDT 0.6B v2, exported to ONNX (int8) for sherpa-onnx. Remy downloads the pinned bundle separately; the complete notices include its model card and license link."),
            new("S1-mini by Superwhisper", "https://huggingface.co/superwhisper/s1-mini", "Apache 2.0 + naming term",
                "Optional on-device model for English transcript normalization. Model files and license are downloaded separately at the user's request."),
            new("LLamaSharp", "https://github.com/SciSharp/LLamaSharp", "MIT License", "Runs S1-mini by Superwhisper locally on your CPU."),
            new("llama.cpp", "https://github.com/ggml-org/llama.cpp", "MIT License", "Local inference runtime underneath LLamaSharp."),
            new("Ollama", "https://github.com/ollama/ollama", "MIT License", "Local LLM runtime used for optional transcript post-processing."),
            new("NAudio", "https://github.com/naudio/NAudio", "MIT License", "Microphone capture and sound-effect playback."),
            new("FlaUI", "https://github.com/FlaUI/FlaUI", "MIT License", "UI Automation helpers used to find the focused text field."),
            new("H.NotifyIcon", "https://github.com/HavenDV/H.NotifyIcon", "MIT License", "System tray icon and menu."),
            new("CommunityToolkit.Mvvm", "https://github.com/CommunityToolkit/dotnet", "MIT License", "MVVM helpers for the Remy settings interface."),
        ];
        Libraries.ItemsSource = libs;
    }

    static string VersionString()
    {
        var asm = typeof(AboutPage).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
        return asm.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "–";
    }

    public void Attach(AppServices services) { }
}
