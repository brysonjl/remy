using ThemeMode = Remy.App.ViewModels.ThemeMode;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Remy.App.ViewModels;

namespace Remy.App.Services;

/// <summary>
/// Swaps Themes/Colors.Light|Dark.xaml in Application.Resources. System mode follows
/// HKCU\...\Themes\Personalize\AppsUseLightTheme live (UserPreferenceChanged).
/// </summary>
public static class ThemeManager
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    static ResourceDictionary? _current;
    static bool _hooked;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static bool IsDark { get; private set; }

    /// <summary>Raised after the system (OS) light/dark setting changes. Subscribe for the overlay's System theme.</summary>
    public static event Action? SystemThemeChanged;
    /// <summary>Raised after the app theme actually changed.</summary>
    public static event Action? ThemeApplied;

    public static bool SystemIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return k?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch { return true; }
    }

    public static void Initialize(ThemeMode mode)
    {
        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    SystemThemeChanged?.Invoke();
                    if (Mode == ThemeMode.System) Apply(ThemeMode.System);
                });
            };
        }
        Apply(mode);
    }

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        bool dark = mode switch { ThemeMode.Dark => true, ThemeMode.Light => false, _ => !SystemIsLight() };
        var uri = new Uri($"pack://application:,,,/Themes/Colors.{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute);
        var dict = new ResourceDictionary { Source = uri };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        merged.Insert(0, dict);
        _current = dict;
        IsDark = dark;
        foreach (Window w in Application.Current.Windows) SetCaptionDark(w, dark);
        ThemeApplied?.Invoke();
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Immersive dark mode for native pieces (e.g. system menus / title bar when shown).</summary>
    public static void SetCaptionDark(Window w, bool dark)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(h, 20, ref v, sizeof(int));
    }

    /// <summary>Rounded corners on Windows 11 (DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND).</summary>
    public static void RoundCorners(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        int v = 2;
        DwmSetWindowAttribute(h, 33, ref v, sizeof(int));
    }
}
