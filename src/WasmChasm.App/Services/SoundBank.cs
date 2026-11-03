using System.IO;
using System.Media;

namespace WasmChasm.App.Services;

/// <summary>
/// Optional sounds (off by default), synthesised at runtime so no audio assets ship:
/// relay click on select, low hum during consign, single beep on completion.
/// </summary>
public sealed class SoundBank : IDisposable
{
    private const int Rate = 22050;
    private readonly SoundPlayer _click;
    private readonly SoundPlayer _hum;
    private readonly SoundPlayer _beep;

    public bool Enabled { get; set; }

    public SoundBank()
    {
        _click = Make(Click());
        _hum = Make(Hum());
        _beep = Make(Beep());
    }

    public void PlayClick() { if (Enabled) Safe(() => _click.Play()); }
    public void StartHum() { if (Enabled) Safe(() => _hum.PlayLooping()); }
    public void StopHum() => Safe(() => _hum.Stop());
    public void PlayBeep() { if (Enabled) Safe(() => _beep.Play()); }

    private static void Safe(Action a)
    {
        try { a(); } catch { /* audio is cosmetic */ }
    }

    private static short[] Click()
    {
        var rng = new Random(7);
        var s = new short[Rate / 40];
        for (int i = 0; i < s.Length; i++)
        {
            double env = Math.Exp(-i / (Rate * 0.002));
            s[i] = (short)((rng.NextDouble() * 2 - 1) * env * 14000);
        }

        return s;
    }

    private static short[] Hum()
    {
        // One second of 60 Hz + harmonics; loops seamlessly because 60 divides the sample count.
        var s = new short[Rate];
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / Rate;
            double v = 0.6 * Math.Sin(2 * Math.PI * 60 * t) + 0.25 * Math.Sin(2 * Math.PI * 120 * t) + 0.1 * Math.Sin(2 * Math.PI * 180 * t);
            s[i] = (short)(v * 3500);
        }

        return s;
    }

    private static short[] Beep()
    {
        var s = new short[Rate / 5];
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / Rate;
            double env = Math.Min(1, Math.Min(i / 200.0, (s.Length - i) / 200.0));
            s[i] = (short)(Math.Sign(Math.Sin(2 * Math.PI * 880 * t)) * env * 6000);
        }

        return s;
    }

    private static SoundPlayer Make(short[] samples)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples.Length * 2;
            w.Write("RIFF"u8.ToArray());
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(Rate);
            w.Write(Rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8.ToArray());
            w.Write(dataBytes);
            foreach (var x in samples) w.Write(x);
        }

        ms.Position = 0;
        var player = new SoundPlayer(ms);
        try { player.Load(); } catch { /* ignore */ }
        return player;
    }

    public void Dispose()
    {
        _click.Dispose();
        _hum.Dispose();
        _beep.Dispose();
    }
}
