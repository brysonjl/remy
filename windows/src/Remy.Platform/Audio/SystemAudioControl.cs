using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Remy.Core.Contracts;

namespace Remy.Platform.Audio;

/// <summary>
/// Output mute and input boost during recording (pipeline.md 3.2). The "muted by Remy" device id is persisted under
/// HKCU\Software\Remy\Runtime so <see cref="RecoverStaleMute"/> can unmute after a crash.
/// </summary>
public sealed class SystemAudioControl : ISystemAudioControl
{
    public const float DefaultBoostTarget = 0.8f;
    const string RuntimeKey = @"Software\Remy\Runtime";
    const string MutedValue = "MutedByRemyDeviceId";
    static readonly string[] HeadphoneTokens = ["airpods", "headphone", "headset", "earbud", "beats", "bluetooth", "hands-free"];

    sealed record MuteToken(string DeviceId);
    sealed record BoostToken(string DeviceId, float Previous);

    /// <summary>Call once at startup: if a previous run crashed while muted, unmute that endpoint.</summary>
    public static void RecoverStaleMute()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RuntimeKey, writable: true);
            if (key?.GetValue(MutedValue) is not string id || id.Length == 0) return;
            try
            {
                using var e = new MMDeviceEnumerator();
                using var d = e.GetDevice(id);
                d.AudioEndpointVolume.Mute = false;
            }
            catch { }
            key.DeleteValue(MutedValue, false);
        }
        catch { }
    }

    public object? MuteOutputForRecording()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            if (IsHeadphoneLike(render)) return null;
            try
            {
                using var capture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                if (IsHeadphoneLike(capture)) return null;
            }
            catch { /* no default input */ }

            var volume = render.AudioEndpointVolume;
            if (volume.Mute) return null;                  // already muted: we never unmute it
            volume.Mute = true;
            if (!volume.Mute) return null;
            Persist(render.ID);
            return new MuteToken(render.ID);
        }
        catch { return null; }
    }

    public object? BoostInput(string? deviceId, float target)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = deviceId == null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : enumerator.GetDevice(deviceId);
            var volume = device.AudioEndpointVolume;
            float current = volume.MasterVolumeLevelScalar;
            if (current >= target) return null;
            volume.MasterVolumeLevelScalar = Math.Clamp(target, 0f, 1f);
            return new BoostToken(device.ID, current);
        }
        catch { return null; }   // many mics expose no controllable volume
    }

    public async Task RestoreAsync(object? muteToken, object? boostToken, TimeSpan settleAfterCaptureStop)
    {
        if (boostToken is BoostToken boost)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(boost.DeviceId);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = boost.Previous;
            }
            catch { }
        }
        if (muteToken is MuteToken mute)
        {
            if (settleAfterCaptureStop > TimeSpan.Zero) await Task.Delay(settleAfterCaptureStop).ConfigureAwait(false);
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(mute.DeviceId);
                device.AudioEndpointVolume.Mute = false;
            }
            catch { }
            ClearPersisted();
        }
    }

    /// <summary>Bluetooth / headset / headphone endpoints: muting would risk a profile switch (name tokens + endpoint form factor).</summary>
    internal static bool IsHeadphoneLike(MMDevice device)
    {
        try
        {
            var name = device.FriendlyName ?? "";
            foreach (var t in HeadphoneTokens)
                if (name.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        try
        {
            // PKEY_AudioEndpoint_FormFactor: 3 = Headphones, 5 = Headset (also 4 = Handset).
            var key = new PropertyKey(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
            if (device.Properties.Contains(key) && device.Properties[key].Value is uint form && form is 3 or 4 or 5) return true;
        }
        catch { }
        try
        {
            // Bluetooth endpoints report a container/instance path mentioning BTH.
            var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);   // PKEY_Device_EnumeratorName
            if (device.Properties.Contains(key) && device.Properties[key].Value is string en
                && en.Contains("BTH", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    static void Persist(string id)
    {
        try { using var k = Registry.CurrentUser.CreateSubKey(RuntimeKey); k.SetValue(MutedValue, id); } catch { }
    }

    static void ClearPersisted()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RuntimeKey, true); k?.DeleteValue(MutedValue, false); } catch { }
    }
}
