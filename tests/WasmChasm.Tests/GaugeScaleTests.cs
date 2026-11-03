using WasmChasm.Core;

namespace WasmChasm.Tests;

public class GaugeScaleTests
{
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;

    [Fact]
    public void Fill_is_the_share_of_the_total_and_marking_everything_fills_the_bar()
    {
        Assert.Equal(0, GaugeScale.Fill(0, GB));
        Assert.Equal(0.5, GaugeScale.Fill(GB / 2, GB));
        Assert.Equal(1, GaugeScale.Fill(GB, GB));
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(0, 0)]
    [InlineData(10, -1)]
    public void Fill_is_empty_when_there_is_nothing_to_purge(long marked, long total)
    {
        Assert.Equal(0, GaugeScale.Fill(marked, total));
    }

    [Fact]
    public void Fill_never_overflows_the_bar()
    {
        Assert.Equal(1, GaugeScale.Fill(2 * GB, GB));
    }

    [Fact]
    public void Ticks_run_evenly_from_zero_to_the_total_with_real_sizes()
    {
        var ticks = GaugeScale.Ticks(1200 * MB);

        Assert.Equal(new[] { 0, 0.25, 0.5, 0.75, 1 }, ticks.Select(t => t.Position));
        Assert.Equal(new[] { "0", "300M", "600M", "900M", "1.2G" }, ticks.Select(t => t.Label));
    }

    [Fact]
    public void No_data_means_only_the_zero_tick()
    {
        Assert.Equal("0", Assert.Single(GaugeScale.Ticks(0)).Label);
    }

    [Theory]
    [InlineData(500, "500B")]
    [InlineData(764 * 1024, "764K")]
    [InlineData(3 * MB + MB / 2, "3.5M")]
    [InlineData(305 * MB, "305M")]
    [InlineData(GB + GB / 5, "1.2G")]
    [InlineData(15 * GB, "15G")]
    public void Short_sizes_fit_beside_the_bar(long bytes, string expected)
    {
        Assert.Equal(expected, GaugeScale.ShortSize(bytes));
        Assert.True(expected.Length <= 4);
    }
}
