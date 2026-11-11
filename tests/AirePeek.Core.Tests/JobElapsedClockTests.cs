using AirePeek.Core;

namespace AirePeek.Core.Tests;

public sealed class JobElapsedClockTests
{
    private static readonly DateTimeOffset Sample = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static SlurmJob Job(string elapsed, string state = "RUNNING") => new("42_7", "training", state, elapsed, "UNLIMITED", "gpu001", "None");

    [Theory]
    [InlineData("0:00", 0)]
    [InlineData("59:59", 3599)]
    [InlineData("90:00", 5400)]
    [InlineData("01:00:00", 3600)]
    [InlineData("24:00:00", 86400)]
    [InlineData("2-03:04:05", 183845)]
    [InlineData(" 0-00:00:01 ", 1)]
    public void ReadsSlurmElapsedWithoutConfusingMinutesAndHours(string text, long seconds)
    {
        Assert.True(JobElapsedClock.TryParse(text, out var actual));
        Assert.Equal(seconds, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("INVALID")]
    [InlineData("UNLIMITED")]
    [InlineData("N/A")]
    [InlineData("00:60")]
    [InlineData("1:60:00")]
    [InlineData("1-24:00:00")]
    [InlineData("-1:00")]
    [InlineData("1-00:00")]
    [InlineData("1:2:3:4")]
    [InlineData("999999999999999999-00:00:00")]
    public void InvalidOrOverflowingElapsedRemainsUnknown(string text)
    {
        var clock = new JobElapsedClock(); clock.Update(Job(text), Sample);
        Assert.False(JobElapsedClock.TryParse(text, out _));
        Assert.Equal(new JobElapsedReading("Unknown", false, false), clock.Read(Sample.AddMinutes(1), true));
    }

    [Fact]
    public void RunningEstimateAdvancesPerSecondAndCrossesDayBoundary()
    {
        var clock = new JobElapsedClock(); clock.Update(Job("23:59:59"), Sample);
        Assert.Equal(new JobElapsedReading("23:59:59", true, true), clock.Read(Sample.AddMilliseconds(900), true));
        Assert.Equal(new JobElapsedReading("1-00:00:00", true, true), clock.Read(Sample.AddSeconds(1), true));
        Assert.Equal(new JobElapsedReading("1-00:00:01", true, true), clock.Read(Sample.AddSeconds(2), true));
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("PD")]
    [InlineData("COMPLETING")]
    [InlineData("CG")]
    [InlineData("SUSPENDED")]
    [InlineData("S")]
    [InlineData("COMPLETED")]
    [InlineData("FAILED")]
    [InlineData("TIMEOUT")]
    public void NonRunningStatesNeverAccumulateLocalTime(string state)
    {
        var clock = new JobElapsedClock(); clock.Update(Job("01:02:03", state), Sample);
        Assert.Equal(new JobElapsedReading("01:02:03", true, false), clock.Read(Sample.AddMinutes(2), true));
    }

    [Fact]
    public void ShortRunningStateCanAdvanceButMissingSampleCannot()
    {
        var clock = new JobElapsedClock(); clock.Update(Job("0:30", "R"), Sample);
        Assert.Equal("00:00:31", clock.Read(Sample.AddSeconds(1), true).Text);
        clock.Update(Job("0:30", "R"), null);
        Assert.Equal(new JobElapsedReading("00:00:30", true, false), clock.Read(Sample.AddHours(1), true));
    }

    [Fact]
    public void OfflineOrStaleUsesServerSnapshotInsteadOfInventingMoreTime()
    {
        var clock = new JobElapsedClock(); clock.Update(Job("01:02:03"), Sample);
        Assert.Equal("01:02:13", clock.Read(Sample.AddSeconds(10), true).Text);
        Assert.Equal(new JobElapsedReading("01:02:03", true, false), clock.Read(Sample.AddHours(5), false));
    }

    [Fact]
    public void FreshSampleReanchorsIncludingSchedulerCorrection()
    {
        var clock = new JobElapsedClock(); clock.Update(Job("01:02:03"), Sample);
        Assert.Equal("01:07:03", clock.Read(Sample.AddMinutes(5), true).Text);
        clock.Update(Job("01:04:00"), Sample.AddMinutes(5));
        Assert.Equal("01:04:01", clock.Read(Sample.AddMinutes(5).AddSeconds(1), true).Text);
    }

    [Fact]
    public void ClockRollbackNeverSubtractsFromSampledElapsed()
    {
        var clock = new JobElapsedClock(); clock.Update(Job("01:02:03"), Sample);
        Assert.Equal("01:02:03", clock.Read(Sample.AddMinutes(-10), true).Text);
        Assert.Equal("01:02:13", clock.Read(Sample.AddSeconds(10), true).Text);
        Assert.Equal("01:02:13", clock.Read(Sample.AddSeconds(5), true).Text);
        Assert.Equal("01:02:13", clock.Read(Sample.AddMinutes(-1), true).Text);
    }

    [Fact]
    public void RerenderingTheSameQueueSampleDoesNotResetItsClock()
    {
        var clock = new JobElapsedClock(); var job = Job("01:02:03"); clock.Update(job, Sample);
        Assert.Equal("01:02:13", clock.Read(Sample.AddSeconds(10), true).Text);
        clock.Update(job with { Name = "renamed display text" }, Sample);
        Assert.Equal("01:02:13", clock.Read(Sample.AddSeconds(5), true).Text);
    }

    [Fact]
    public void ExtremeValidDurationDoesNotOverflowDuringInterpolation()
    {
        var clock = new JobElapsedClock(); clock.Update(Job("10675199-02:48:05"), Sample);
        Assert.True(JobElapsedClock.TryParse("10675199-02:48:05", out _));
        Assert.Equal("10675199-02:48:05", clock.Read(Sample.AddDays(1), true).Text);
    }
}
