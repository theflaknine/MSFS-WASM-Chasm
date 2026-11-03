namespace WasmChasm.Core;

/// <summary>
/// The depth gauge's scale. It is linear, and a full bar means all the data that could be purged,
/// so the ticks show real sizes and marking everything fills the bar.
/// </summary>
public static class GaugeScale
{
    private const int Divisions = 4;

    public static double Fill(long markedBytes, long totalBytes) =>
        totalBytes <= 0 ? 0 : Math.Max(0, Math.Min(1, (double)markedBytes / totalBytes));

    /// <summary>Evenly spaced ticks from zero to the total. With nothing to purge there is only the zero tick.</summary>
    public static IReadOnlyList<(double Position, string Label)> Ticks(long totalBytes)
    {
        var ticks = new List<(double, string)> { (0, "0") };
        if (totalBytes <= 0) return ticks;

        for (int i = 1; i <= Divisions; i++)
        {
            double position = (double)i / Divisions;
            ticks.Add((position, ShortSize((long)(totalBytes * position))));
        }

        return ticks;
    }

    /// <summary>A size in four characters at most, to fit beside the bar: 764K, 3.5M, 305M, 1.2G.</summary>
    public static string ShortSize(long bytes)
    {
        const double k = 1024;
        if (bytes < k) return $"{bytes}B";
        if (bytes < k * k) return Compact(bytes / k, "K");
        if (bytes < k * k * k) return Compact(bytes / (k * k), "M");
        return Compact(bytes / (k * k * k), "G");

        static string Compact(double value, string unit) =>
            value < 10
                ? value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + unit
                : Math.Round(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + unit;
    }
}
