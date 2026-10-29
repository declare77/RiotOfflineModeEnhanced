using System;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Deceive;

/// <summary>
/// The agent select alert: flashes the window's taskbar button (or shows a tray notice) and
/// plays a short chime when agent select starts. It notices this from your own chat presence,
/// so it costs no requests and doesn't need the window open. It never takes focus from the game.
/// </summary>
internal static class Alerts
{
    private static MainController? _controller;
    private static DateTime _lastAlert = DateTime.MinValue;

    internal static void Attach(MainController controller)
    {
        _controller = controller;
        ValorantPresence.OwnStateChanged += (_, presence) =>
        {
            if (presence.InAgentSelect)
                controller.RunOnUi(AgentSelectStarted);
        };
    }

    private static void AgentSelectStarted()
    {
        var settings = AppSettings.Current;
        if (!settings.AgentSelectAlert && !settings.AgentSelectSound) return;
        // Dodges and re-queues can bounce the state; one alert per half minute is plenty.
        if (DateTime.UtcNow - _lastAlert < TimeSpan.FromSeconds(30)) return;
        _lastAlert = DateTime.UtcNow;

        if (settings.AgentSelectAlert)
        {
            var window = _controller?.Window;
            if (window is { IsDisposed: false, Visible: true } && window.IsHandleCreated)
                Native.FlashTaskbar(window.Handle);
            else
                _controller?.ShowTrayNotice("Agent select started", "Open the window to see your team.");
        }

        if (settings.AgentSelectSound)
            PlayChime();
    }

    // ------------------------------------------------------------------ chime

    private static byte[]? _chime;

    internal static void PlayChime()
    {
        try
        {
            _chime ??= MakeChime();
            var player = new SoundPlayer(new MemoryStream(_chime));
            player.Play(); // plays on a background thread
        }
        catch (Exception e)
        {
            Trace.WriteLine("Chime failed: " + e.Message);
        }
    }

    /// <summary>A soft two-note chime as a 16-bit mono WAV.</summary>
    private static byte[] MakeChime()
    {
        const int rate = 44100;
        var notes = new (double freq, double seconds)[] { (880, 0.13), (1318.5, 0.22) };
        var total = 0;
        foreach (var n in notes) total += (int)(rate * n.seconds);

        var samples = new short[total];
        var i = 0;
        foreach (var (freq, seconds) in notes)
        {
            var count = (int)(rate * seconds);
            for (var k = 0; k < count; k++, i++)
            {
                var t = (double)k / rate;
                var attack = Math.Min(1.0, k / (rate * 0.008));
                var release = Math.Exp(-4.5 * t / seconds);
                var value = Math.Sin(2 * Math.PI * freq * t) * 0.28 * attack * release;
                samples[i] = (short)(value * short.MaxValue);
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        var dataBytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);      // PCM
        w.Write((short)1);      // mono
        w.Write(rate);
        w.Write(rate * 2);      // bytes per second
        w.Write((short)2);      // block align
        w.Write((short)16);     // bits per sample
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}

internal static class NativeFlash
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
