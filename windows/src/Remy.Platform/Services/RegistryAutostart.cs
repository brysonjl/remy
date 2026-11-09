using Microsoft.Win32;
using Remy.Core.Contracts;

namespace Remy.Platform.Services;

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "Remy" = quoted exe path.</summary>
public sealed class RegistryAutostart : IAutostart
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Remy";

    readonly string _exePath;
    readonly string _runKey;
    readonly string _valueName;

    public RegistryAutostart(string? exePath = null, string runKeyPath = RunKeyPath, string valueName = ValueName)
    {
        _exePath = exePath ?? Environment.ProcessPath ?? "";
        _runKey = runKeyPath;
        _valueName = valueName;
    }

    public bool IsEnabled
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(_runKey); return k?.GetValue(_valueName) is string s && s.Length > 0; }
            catch { return false; }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(_runKey);
            if (enabled)
            {
                if (string.IsNullOrEmpty(_exePath)) throw new InvalidOperationException("Could not determine the Remy executable path.");
                k.SetValue(_valueName, "\"" + _exePath + "\"", RegistryValueKind.String);
            }
            else k.DeleteValue(_valueName, throwOnMissingValue: false);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception e)
        {
            throw new InvalidOperationException(enabled
                ? "Could not turn on launch at login. Try again."
                : "Could not turn off launch at login. Try again.", e);
        }
    }
}
