using AirePeek.Core;

namespace AirePeek.Core.Tests;

public sealed class SampleFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARecentQueueCannotHideStaleGpuData()
    {
        var queue = SampleFreshness.Evaluate(Now.AddSeconds(-8), MonitorService.QueueInterval, Now);
        var gpu = SampleFreshness.Evaluate(Now.AddMinutes(-11), MonitorService.GpuInterval, Now);
        Assert.Equal(FreshnessState.Fresh, queue.State);
        Assert.Equal("8s", queue.Label);
        Assert.Equal(FreshnessState.Stale, gpu.State);
        Assert.Equal("Stale · 11m", gpu.Label);
        Assert.True(gpu.NeedsAttention);
    }

    [Theory]
    [InlineData(120, 239, FreshnessState.Fresh)]
    [InlineData(120, 240, FreshnessState.Stale)]
    [InlineData(300, 599, FreshnessState.Fresh)]
    [InlineData(300, 600, FreshnessState.Stale)]
    public void StalenessStartsAfterTwoSamplingIntervals(int intervalSeconds, int ageSeconds, FreshnessState expected)
    {
        Assert.Equal(expected, SampleFreshness.Evaluate(Now.AddSeconds(-ageSeconds), TimeSpan.FromSeconds(intervalSeconds), Now).State);
    }

    [Fact]
    public void ADisconnectedRecentSampleIsNotReportedFresh()
    {
        var result = SampleFreshness.Evaluate(Now.AddSeconds(-8), MonitorService.QueueInterval, Now, disconnected: true);
        Assert.Equal(FreshnessState.Disconnected, result.State);
        Assert.Equal(TimeSpan.FromSeconds(8), result.Age);
        Assert.Equal("Disconnected · 8s", result.Label);
    }

    [Fact]
    public void MissingDataAndDisconnectedDataRemainDifferent()
    {
        Assert.Equal(FreshnessState.Never, SampleFreshness.Evaluate(null, MonitorService.QueueInterval, Now).State);
        Assert.Equal("Not sampled", SampleFreshness.Evaluate(null, MonitorService.QueueInterval, Now).Label);
        Assert.Equal(FreshnessState.Disconnected, SampleFreshness.Evaluate(null, MonitorService.QueueInterval, Now, true).State);
    }

    [Fact]
    public void WallClockAdjustmentDoesNotProduceNegativeAge()
    {
        var result = SampleFreshness.Evaluate(Now.AddMinutes(1), MonitorService.QueueInterval, Now);
        Assert.Equal(TimeSpan.Zero, result.Age);
        Assert.Equal("0s", result.Label);
    }
}
