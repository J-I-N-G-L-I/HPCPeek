using AirePeek.Core;

namespace AirePeek.Core.Tests;

public class StorageParserTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadsSpaceQuotasInBytesAndKeepsProjectScope()
    {
        var sample = StorageParser.Parse(DemoSession.StorageQuotas(ClusterCatalog.CalderId), SampleTime);
        Assert.Equal(16L * 1024 * 1024 * 1024, sample.Home.UsedBytes);
        Assert.Equal(65L * 1024 * 1024 * 1024, sample.Home.LimitBytes);
        Assert.Equal("/scratch-calder/users/demo", sample.Scratch.Path);
        Assert.Equal("project 1234", sample.Scratch.Scope);
        Assert.Equal(0.6, sample.Scratch.Fraction!.Value, 6);
        Assert.Equal(SampleTime, sample.Scratch.SampledAt);
    }

    [Theory]
    [InlineData("100* 80 120", 100L, 80L)]
    [InlineData("0 0 120", 0L, 120L)]
    [InlineData("100 0 0", 100L, null)]
    [InlineData("9999999999999999999 80 120", null, null)]
    public void SupportsWrappedRowsOverQuotaAndHardLimitFallback(string values, long? used, long? limit)
    {
        string output = DemoSession.StorageQuotas().Replace("homes:/users 16777216 68157440 68157440", "homes:/users\n    " + values);
        var home = StorageParser.Parse(output, SampleTime).Home;
        Assert.Equal(used * 1024, home.UsedBytes);
        Assert.Equal(limit * 1024, home.LimitBytes);
        Assert.Equal(used is not null && limit is not null ? (double)used / limit : null, home.Fraction);
    }

    [Fact]
    public void UnavailableQuotasStayUnknownAndIncompleteResponsesAreRejected()
    {
        var sample = StorageParser.Parse(DemoSession.StorageQuotas(partial: true), SampleTime);
        Assert.NotNull(sample.Home.Fraction);
        Assert.Null(sample.Scratch.Fraction);
        Assert.Null(sample.Scratch.SampledAt);
        Assert.Throws<FormatException>(() => StorageParser.Parse("Filesystem Size Used Available Use% Mounted on\n/dev/sda 1000 500 500 50% /users", SampleTime));
    }
}
