using System.Runtime.InteropServices;
using NAudio.Wave;
using Remy.Platform.Audio;
using Remy.Platform.Delivery;
using Remy.Platform.Native;
using Remy.Platform.Services;
using Xunit;

namespace Remy.Platform.Tests;

public class PlatformPartsTests
{
    [Fact]
    public void InputStruct_Is40BytesOnX64()
    {
        Assert.Equal(40, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    [Fact]
    public void PasteBatch_IsCtrlDown_VDown_VUp_CtrlUp()
    {
        var inputs = ClipboardDelivery.BuildPasteInputs();
        Assert.Equal(4, inputs.Length);
        Assert.Equal([(ushort)0x11, (ushort)0x56, (ushort)0x56, (ushort)0x11], inputs.Select(i => i.ki.wVk).ToArray());
        Assert.Equal([false, false, true, true], inputs.Select(i => (i.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP) != 0).ToArray());
    }

    [Theory]
    [InlineData(48000, 48000)]
    [InlineData(44100, 22050)]
    [InlineData(16000, 8000)]
    [InlineData(96000, 1234)]
    public void Resampler_TotalOutputLength_MatchesRatio_AfterFlush(int rate, int frames)
    {
        var rs = new StreamingResampler(rate);
        int total = 0;
        var rng = new Random(1);
        int pos = 0;
        while (pos < frames)
        {
            int n = Math.Min(rng.Next(100, 900), frames - pos);
            var block = new float[n];
            for (int i = 0; i < n; i++) block[i] = (float)Math.Sin(2 * Math.PI * 440 * (pos + i) / rate);
            total += rs.Process(block).Length;
            pos += n;
        }
        total += rs.Flush().Length;
        long expected = (long)Math.Round(frames * 16000.0 / rate);
        Assert.Equal(expected, total);
    }

    [Fact]
    public void Resampler_PreservesToneAmplitude()
    {
        var rs = new StreamingResampler(48000);
        var input = new float[48000];
        for (int i = 0; i < input.Length; i++) input[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / 48000);
        var all = new List<float>();
        for (int i = 0; i < input.Length; i += 480) all.AddRange(rs.Process(input.AsSpan(i, 480)));
        all.AddRange(rs.Flush());
        var mid = all.Skip(4000).Take(4000).ToArray();
        Assert.InRange(mid.Max(), 0.45f, 0.55f);
    }

    [Fact]
    public void ToMono_AveragesFloatChannels_AndDecodesPcm16()
    {
        var f = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var bytes = new byte[16];
        BitConverter.GetBytes(1.0f).CopyTo(bytes, 0); BitConverter.GetBytes(0.0f).CopyTo(bytes, 4);
        BitConverter.GetBytes(0.5f).CopyTo(bytes, 8); BitConverter.GetBytes(0.5f).CopyTo(bytes, 12);
        var mono = WasapiAudioCapture.ToMono(bytes, f);
        Assert.Equal([0.5f, 0.5f], mono);

        var pcm = new WaveFormat(16000, 16, 1);
        var m2 = WasapiAudioCapture.ToMono(BitConverter.GetBytes((short)16384), pcm);
        Assert.Equal(0.5f, m2[0], 3);
    }

    [Fact]
    public void Credentials_RoundTrip_AgainstRealCredentialManager()
    {
        var store = new CredentialManagerStore();
        var service = "RemyTest-" + Guid.NewGuid().ToString("N");
        const string account = "unit-test";
        try
        {
            Assert.Null(store.Read(service, account));
            store.Write(service, account, "sk-test-\u00e9\u4e2d-123");
            Assert.Equal("sk-test-\u00e9\u4e2d-123", store.Read(service, account));
            store.Write(service, account, "second");
            Assert.Equal("second", store.Read(service, account));
            store.Write(service, account, "   ");           // whitespace deletes
            Assert.Null(store.Read(service, account));
            store.Delete(service, account);                  // deleting a missing item is not an error
        }
        finally { try { store.Delete(service, account); } catch { } }
    }

    [Fact]
    public void Credentials_TargetNameFormat()
        => Assert.Equal("Remy/svc/acct", CredentialManagerStore.TargetName("svc", "acct"));

    [Fact]
    public void Autostart_WritesQuotedPath_ToTestKey()
    {
        var key = @"Software\RemyTests\Run" + Guid.NewGuid().ToString("N");
        try
        {
            var a = new RegistryAutostart(@"C:\Program Files\Remy\Remy.exe", key);
            Assert.False(a.IsEnabled);
            a.SetEnabled(true);
            Assert.True(a.IsEnabled);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal("\"C:\\Program Files\\Remy\\Remy.exe\"", k!.GetValue("Remy"));
            a.SetEnabled(false);
            Assert.False(a.IsEnabled);
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    }

    [Fact]
    public void Sounds_ImportValidation_AndIds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remy-sound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var wav = Path.Combine(dir, "ok.wav");
            using (var w = new WaveFileWriter(wav, new WaveFormat(8000, 16, 1)))
                w.Write(new byte[8000 * 2], 0, 8000 * 2);               // 1 s
            var long_ = Path.Combine(dir, "long.wav");
            using (var w = new WaveFileWriter(long_, new WaveFormat(8000, 16, 1)))
                w.Write(new byte[8000 * 2 * 11], 0, 8000 * 2 * 11);     // 11 s
            var txt = Path.Combine(dir, "x.txt"); File.WriteAllText(txt, "no");

            var player = new WindowsSoundPlayer(Path.Combine(dir, "Sounds"), dir);
            var id = player.ImportCustomSound(wav);
            Assert.StartsWith("custom:", id);
            Assert.EndsWith(".wav", id);
            Assert.Equal("Custom audio", player.DisplayName(id));
            Assert.Equal(WindowsSoundPlayer.ImportFailedMessage, Assert.Throws<InvalidOperationException>(() => player.ImportCustomSound(long_)).Message);
            Assert.Throws<InvalidOperationException>(() => player.ImportCustomSound(txt));
            Assert.Throws<InvalidOperationException>(() => player.ImportCustomSound(Path.Combine(dir, "missing.wav")));
            Assert.Equal("None", player.DisplayName("none"));
            Assert.Equal("Unavailable sound", player.DisplayName("builtin:Nope"));
            player.Play("builtin:Nope", 0.3f);                           // plays nothing, does not throw
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Sounds_BuiltIns_AreWavFilesFromMediaFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remy-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Speech On.wav"), []);
            File.WriteAllBytes(Path.Combine(dir, "readme.txt"), []);
            var p = new WindowsSoundPlayer(Path.Combine(dir, "s"), dir);
            Assert.Equal(["builtin:Speech On"], p.BuiltInSounds.Select(s => s.Id).ToArray());
            Assert.Equal("Speech On", p.DisplayName("builtin:Speech On"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
