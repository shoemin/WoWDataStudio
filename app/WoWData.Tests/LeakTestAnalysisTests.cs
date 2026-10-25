using System.Linq;
using WoWDataStudio.Services;

namespace WoWData.Tests;

/// <summary>The verdict of the `--leaktest` mode (LeakTestRunner.Analyze): a steady rise is growth; a cache that fills
/// and then stays, and a view that swings between a few states, are not.</summary>
public class LeakTestAnalysisTests
{
    private static LeakTestRunner.Sample S(int i, double managedMb, double privateMb = 500, int handles = 1000, int gl = 20) =>
        new(i, i, (long)(managedMb * 1048576), (long)(privateMb * 1048576), (long)(privateMb * 1048576), handles, 50, 50, 40, 4, gl, 0, "");

    [Fact]
    public void SteadyRiseIsGrowth()
    {
        var s = Enumerable.Range(0, 200).Select(i => S(i, 200 + i * 0.5, 500 + i * 0.2, 1000 + 2 * i)).ToList();
        var r = LeakTestRunner.Analyze("v", s, 0);
        Assert.Contains(r.Grows, g => g.StartsWith("managed heap"));
        Assert.Contains(r.Grows, g => g.StartsWith("private bytes"));
        Assert.Contains(r.Grows, g => g.StartsWith("handles"));
        Assert.InRange(r.ManagedKb, 500, 524);
        Assert.InRange(r.Handles, 1.99, 2.01);
    }

    [Fact]
    public void FillingCacheAndRotatingStatesAreFlat()
    {
        // a cache filling for 60 cycles, then full (with noise); GL objects likewise
        var fill = Enumerable.Range(0, 200).Select(i => S(i, 200 + Math.Min(i, 60) * 1.2 + (i % 7) * 0.3, 600 + Math.Min(i, 60) + (i % 5), 1000, 20 + Math.Min(i, 60))).ToList();
        Assert.Equal("flat", LeakTestRunner.Analyze("fill", fill, 0).Verdict);
        // six tables in turn: the private bytes swing by 40 MB
        var rotate = Enumerable.Range(0, 100).Select(i => S(i, 400 + (i % 6) * 0.4, 1030 + (i % 6) * 8)).ToList();
        Assert.Equal("flat", LeakTestRunner.Analyze("rotate", rotate, 0).Verdict);
    }

    [Fact]
    public void ClosedWindowsStillAliveAreReported()
    {
        var s = Enumerable.Range(0, 50).Select(i => S(i, 200) with { Alive = i }).ToList();
        Assert.Contains(LeakTestRunner.Analyze("w", s, 200).Grows, g => g.Contains("still alive"));
    }
}
