using WasmChasm.Core;

namespace WasmChasm.Tests;

public class SoundSynthTests
{
    private static double Rms(float[] s) => Math.Sqrt(s.Sum(x => (double)x * x) / s.Length);

    /// <summary>How much of the signal is rapid change: low for mellow tones, high for harsh ones.</summary>
    private static double Harshness(float[] s)
    {
        double diff = 0;
        for (int i = 1; i < s.Length; i++) diff += (double)(s[i] - s[i - 1]) * (s[i] - s[i - 1]);
        return Math.Sqrt(diff / (s.Length - 1)) / Rms(s);
    }

    private static float[] OldSquareBeep()
    {
        var s = new float[SoundSynth.Rate / 5];
        for (int i = 0; i < s.Length; i++)
            s[i] = Math.Sign(Math.Sin(2 * Math.PI * 880 * i / SoundSynth.Rate)) * 0.18f;
        return s;
    }

    [Fact]
    public void Chime_is_far_less_harsh_than_the_old_square_beep()
    {
        Assert.True(Harshness(SoundSynth.Chime()) < Harshness(OldSquareBeep()) / 3);
    }

    [Fact]
    public void Chime_fades_in_and_out_so_it_never_pops()
    {
        var s = SoundSynth.Chime();
        Assert.True(Math.Abs(s[0]) < 0.001);
        Assert.True(Math.Abs(s[^1]) < 0.001);
        Assert.True(s.Max(Math.Abs) <= 0.2801f);
        for (int i = 1; i < s.Length; i++) Assert.True(Math.Abs(s[i] - s[i - 1]) < 0.08);
    }

    [Fact]
    public void Power_up_builds_gradually_with_no_sudden_start()
    {
        var s = SoundSynth.PowerOn();
        double Level(double from, double to) => Rms(s[(int)(s.Length * from)..(int)(s.Length * to)]);

        // Each stretch of the opening is louder than the last, and the start is close to silent.
        Assert.True(Level(0, 0.05) < Level(0.10, 0.15));
        Assert.True(Level(0.10, 0.15) < Level(0.25, 0.30));
        Assert.True(Level(0.25, 0.30) < Level(0.50, 0.60));
        Assert.True(Level(0, 0.05) < 0.005);

        Assert.True(s.Max(Math.Abs) <= 0.1301f);
        Assert.True(Math.Abs(s[^1]) < 0.001);
        Assert.True(Harshness(s) < Harshness(OldSquareBeep()) / 10);
    }

    [Fact]
    public void Power_up_rises_in_pitch()
    {
        var s = SoundSynth.PowerOn();
        static int Crossings(float[] a, int from, int to)
        {
            int n = 0;
            for (int i = from + 1; i < to; i++) if (a[i - 1] < 0 != a[i] < 0) n++;
            return n;
        }

        int third = s.Length / 3;
        Assert.True(Crossings(s, third * 2, s.Length) > Crossings(s, 0, third));
    }

    [Fact]
    public void Disintegrate_falls_in_pitch_and_never_pops()
    {
        var s = SoundSynth.Disintegrate();
        static int Crossings(float[] a, int from, int to)
        {
            int n = 0;
            for (int i = from + 1; i < to; i++) if (a[i - 1] < 0 != a[i] < 0) n++;
            return n;
        }

        int quarter = s.Length / 4;
        Assert.True(Crossings(s, 0, quarter) > Crossings(s, quarter * 3, s.Length) * 2);
        Assert.True(s.Max(Math.Abs) <= 0.2001f);
        Assert.True(Math.Abs(s[0]) < 0.001);
        Assert.True(Math.Abs(s[^1]) < 0.001);
        Assert.True(Harshness(s) < Harshness(OldSquareBeep()));
    }

    [Fact]
    public void Ratchet_rises_in_pitch_and_level_with_the_count()
    {
        static int Crossings(float[] a)
        {
            int n = 0;
            for (int i = 1; i < a.Length; i++) if (a[i - 1] < 0 != a[i] < 0) n++;
            return n;
        }

        var low = SoundSynth.Ratchet(0);
        var high = SoundSynth.Ratchet(SoundSynth.RatchetTopCount);
        Assert.True(Crossings(high) > Crossings(low) * 2);
        Assert.True(high.Max(Math.Abs) > low.Max(Math.Abs));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(SoundSynth.RatchetTopCount)]
    [InlineData(500)]
    public void Ratchet_stays_short_soft_and_free_of_pops(int count)
    {
        var s = SoundSynth.Ratchet(count);
        Assert.True(s.Length < SoundSynth.Rate / 10);
        Assert.True(s.Max(Math.Abs) <= 0.1901f);
        Assert.True(Math.Abs(s[0]) < 0.001);
        Assert.True(Math.Abs(s[^1]) < 0.001);
    }

    [Fact]
    public void Each_marked_aircraft_raises_the_ratchet()
    {
        static double Crossings(float[] a)
        {
            int n = 0;
            for (int i = 1; i < a.Length; i++) if (a[i - 1] < 0 != a[i] < 0) n++;
            return n;
        }

        // Compared across several steps, since a single semitone is close to the resolution of a 75 ms sample.
        for (int count = 0; count + 6 <= SoundSynth.RatchetTopCount; count += 6)
            Assert.True(Crossings(SoundSynth.Ratchet(count + 6)) > Crossings(SoundSynth.Ratchet(count)));
    }

    [Fact]
    public void Hum_is_gentle_and_free_of_harsh_content()
    {
        var s = SoundSynth.AmbientHum();
        Assert.True(s.Max(Math.Abs) <= 0.1001f);
        Assert.True(Harshness(s) < Harshness(OldSquareBeep()) / 10);
    }

    [Fact]
    public void Hum_loops_without_a_jump_at_the_seam()
    {
        var s = SoundSynth.AmbientHum();
        double largestStep = 0;
        for (int i = 1; i < s.Length; i++) largestStep = Math.Max(largestStep, Math.Abs(s[i] - s[i - 1]));
        Assert.True(Math.Abs(s[0] - s[^1]) <= largestStep);
    }

    [Fact]
    public void Row_tick_is_short_quiet_and_softer_than_the_click()
    {
        var tick = SoundSynth.Tick();
        Assert.True(tick.Length < SoundSynth.Rate / 10);
        Assert.True(tick.Max(Math.Abs) < SoundSynth.Click().Max(Math.Abs) / 3);
        Assert.True(Math.Abs(tick[0]) < 0.001);
        Assert.True(Math.Abs(tick[^1]) < 0.005);
    }
}
