namespace WasmChasm.Core;

/// <summary>Synthesises the app's optional sounds as mono samples, so no audio assets ship.</summary>
public static class SoundSynth
{
    public const int Rate = 22050;

    /// <summary>A short relay click.</summary>
    public static float[] Click()
    {
        var rng = new Random(7);
        var s = new float[Rate / 40];
        for (int i = 0; i < s.Length; i++)
        {
            double env = Math.Exp(-i / (Rate * 0.002));
            s[i] = (float)((rng.NextDouble() * 2 - 1) * env * 0.43);
        }

        return s;
    }

    /// <summary>A soft, rounded note: nearly pure tones with a gentle attack and a long fade.</summary>
    public static float[] Chime()
    {
        const double fundamental = 392; // G4
        const double peak = 0.28;
        var partials = new (double Freq, double Level, double Decay)[]
        {
            (fundamental, 1.0, 0.50),
            (fundamental + 1.2, 0.35, 0.50), // slightly detuned, for warmth
            (fundamental * 2, 0.16, 0.25),
            (fundamental * 3, 0.03, 0.12),
        };

        var s = new double[(int)(Rate * 1.4)];
        int attack = (int)(Rate * 0.015);
        int release = (int)(Rate * 0.05);
        double max = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / Rate;
            double v = 0;
            foreach (var p in partials) v += p.Level * Math.Exp(-t / p.Decay) * Math.Sin(2 * Math.PI * p.Freq * t);
            if (i < attack) v *= 0.5 - 0.5 * Math.Cos(Math.PI * i / attack);
            int left = s.Length - 1 - i;
            if (left < release) v *= (double)left / release;
            s[i] = v;
            max = Math.Max(max, Math.Abs(v));
        }

        return s.Select(v => (float)(v / max * peak)).ToArray();
    }

    /// <summary>
    /// A row crumbling into the pit, in the manner of an old arcade machine: a triangle wave stepping downwards in
    /// pitch, over grainy noise whose grains get coarser as it falls. Triangle rather than square keeps it mellow.
    /// </summary>
    public static float[] Disintegrate()
    {
        const double seconds = 1.0;
        const double peak = 0.20;
        var rng = new Random(31);
        var s = new double[(int)(Rate * seconds)];
        int stepLength = (int)(Rate * 0.03);
        int attack = (int)(Rate * 0.004);
        int release = (int)(Rate * 0.12);
        double phase = 0, freq = 0, grain = 0, smooth = 0, max = 0;
        int grainLeft = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double progress = (double)i / s.Length;
            if (i % stepLength == 0) freq = 760 * Math.Pow(70.0 / 760, progress); // stepped, like a sound chip

            phase = (phase + freq / Rate) % 1;
            double triangle = 4 * Math.Abs(phase - 0.5) - 1;

            if (grainLeft-- <= 0)
            {
                grain = rng.NextDouble() * 2 - 1;
                grainLeft = (int)(3 + progress * 45);
            }

            smooth += 0.3 * (grain - smooth);
            double v = (0.75 * triangle + 0.45 * smooth) * Math.Pow(1 - progress, 1.3);
            if (i < attack) v *= (double)i / attack;
            int left = s.Length - 1 - i;
            if (left < release) v *= (double)left / release;
            s[i] = v;
            max = Math.Max(max, Math.Abs(v));
        }

        return s.Select(v => (float)(v / max * peak)).ToArray();
    }

    /// <summary>
    /// A gradual power-up: it fades in from silence while the pitch climbs, like equipment spinning up,
    /// then fades away as the ambient hum takes over. There is no attack to startle anyone.
    /// </summary>
    public static float[] PowerOn()
    {
        const double seconds = 2.4;
        const double peak = 0.13;
        var s = new double[(int)(Rate * seconds)];
        double low = 0, whine = 0, max = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double progress = (double)i / s.Length;
            double rise = 1 - Math.Pow(1 - progress, 2.2); // climbs quickly at first, then settles

            low += 2 * Math.PI * (36 + 74 * rise) / Rate;
            whine += 2 * Math.PI * (220 + 440 * rise) / Rate;
            double v = Math.Sin(low) + 0.4 * Math.Sin(2 * low) + 0.12 * Math.Sin(3 * low) + 0.05 * Math.Sin(whine);

            // Smooth swell over the first 60%, smooth fade over the rest.
            double env = progress < 0.6 ? Smooth(progress / 0.6) : 1 - Smooth((progress - 0.6) / 0.4);
            s[i] = v * env;
            max = Math.Max(max, Math.Abs(s[i]));
        }

        return s.Select(v => (float)(v / max * peak)).ToArray();

        static double Smooth(double x) => x * x * (3 - 2 * x);
    }

    /// <summary>
    /// Eight seconds of gentle retro sci-fi drone: soft low tones with slightly detuned pairs that drift against
    /// each other, and a slow swell. It loops seamlessly because every frequency is a multiple of 1/8 Hz.
    /// </summary>
    public static float[] AmbientHum()
    {
        const double peak = 0.10;
        const int seconds = 8;
        var partials = new (double Freq, double Level)[]
        {
            (55, 1.0),
            (55.25, 0.8),
            (110, 0.55),
            (110.375, 0.4),
            (165, 0.16),
            (220.125, 0.10),
            (440.25, 0.025), // a faint shimmer on top
        };

        var s = new double[Rate * seconds];
        double max = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / Rate;
            double v = 0;
            foreach (var p in partials) v += p.Level * Math.Sin(2 * Math.PI * p.Freq * t);
            v *= 1 + 0.15 * Math.Sin(2 * Math.PI * t / seconds);
            s[i] = v;
            max = Math.Max(max, Math.Abs(v));
        }

        return s.Select(v => (float)(v / max * peak)).ToArray();
    }

    /// <summary>The number of marked aircraft at which the ratchet reaches its highest note.</summary>
    public const int RatchetTopCount = 30;

    /// <summary>
    /// One tooth of a ratchet, for marking a row. Each marked aircraft raises the pitch a semitone and the level
    /// a little, so the sound winds up with the count. It stays soft: a rounded triangle tone with a faint tick.
    /// </summary>
    public static float[] Ratchet(int markedCount)
    {
        int count = Math.Max(0, Math.Min(RatchetTopCount, markedCount));
        double freq = 150 * Math.Pow(2, count / 12.0); // two and a half octaves at the top
        double peak = 0.10 + 0.09 * count / RatchetTopCount;
        var rng = new Random(13);
        var s = new double[(int)(Rate * 0.075)];
        int attack = (int)(Rate * 0.002);
        double phase = 0, max = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / Rate;
            phase = (phase + freq * (1 - 0.08 * t / 0.075) / Rate) % 1;
            double triangle = 4 * Math.Abs(phase - 0.5) - 1;
            double tick = (rng.NextDouble() * 2 - 1) * Math.Exp(-t / 0.0015) * 0.35;
            double v = triangle * Math.Exp(-t / 0.022) + tick;
            if (i < attack) v *= (double)i / attack;
            int left = s.Length - 1 - i;
            if (left < attack) v *= (double)left / attack;
            s[i] = v;
            max = Math.Max(max, Math.Abs(v));
        }

        return s.Select(v => (float)(v / max * peak)).ToArray();
    }

    /// <summary>A very short, soft blip for moving between rows; deliberately quieter than the click for marking.</summary>
    public static float[] Tick()
    {
        const double peak = 0.09;
        var s = new float[(int)(Rate * 0.05)];
        int attack = (int)(Rate * 0.002);
        double phase = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / Rate;
            phase += 2 * Math.PI * (560 + 160 * Math.Exp(-t / 0.012)) / Rate; // the pitch falls slightly
            double v = Math.Exp(-t / 0.012) * Math.Sin(phase);
            if (i < attack) v *= (double)i / attack;
            s[i] = (float)(v * peak);
        }

        return s;
    }
}
