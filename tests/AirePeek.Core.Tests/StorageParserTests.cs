using AirePeek.Core;

namespace AirePeek.Core.Tests;

public class StorageParserTests
{
    private static readonly DateTimeOffset SampleTime = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    // Anonymized from the actual Aire output reported with the v1.1.0 failure.
    private const string AireOutput = """
        HPCPEEK_HOME|/users/demo|user
        Disk quotas for user demo (uid 12345):
             Filesystem   space   quota   limit   grace   files   quota   limit   grace
        10.0.0.1:/export/users
                         42968M  66560M  66560M            659k   1500k   1500k
        HPCPEEK_HOME_STATUS|0
        HPCPEEK_HOME_END
        HPCPEEK_SCRATCH|/mnt/scratch/demo|user
        Disk quotas for usr demo (uid 12345):
             Filesystem    used   quota   limit   grace   files   quota   limit   grace
               /scratch  614.4G      0k   1000G       - 1333295       0 1500000       -
        HPCPEEK_SCRATCH_STATUS|0
        HPCPEEK_SCRATCH_END
        """;

    [Fact]
    public void ReadsReportedAireHumanReadableOutputIncludingWrappedHomeAndScratchHardLimit()
    {
        var sample = StorageParser.Parse(AireOutput, SampleTime);
        Assert.Equal(42968L * 1024 * 1024, sample.Home.UsedBytes);
        Assert.Equal(65L * 1024 * 1024 * 1024, sample.Home.LimitBytes);
        Assert.Equal(42968.0 / 66560, sample.Home.Fraction!.Value, 10);
        Assert.Equal(1000L * 1024 * 1024 * 1024, sample.Scratch.LimitBytes);
        Assert.Equal(.6144, sample.Scratch.Fraction!.Value, 10);
        Assert.Equal(SampleTime, sample.Home.SampledAt);
        Assert.Equal(SampleTime, sample.Scratch.SampledAt);
    }

    [Fact]
    public void RemoteScriptsUseUnixNewlinesAndDocumentedMountQueries()
    {
        string aire = StorageCommands.Query();
        string calder = StorageCommands.Query(ClusterCatalog.CalderId);
        Assert.DoesNotContain('\r', aire); Assert.DoesNotContain('\r', calder);
        Assert.Contains("quota -s", aire);
        Assert.Contains("hp_root=/scratch;", aire);
        Assert.Contains("lfs quota -h -u \"$hp_user\" \"$hp_root\"", aire);
        Assert.Contains("hp_root=/scratch-calder;", calder);
        Assert.Contains("hp_project=$(id -u)", calder);
        Assert.Contains("lfs quota -h -p \"$hp_project\" \"$hp_root\"", calder);
        Assert.DoesNotContain("quota -w -v -f", aire);
        Assert.DoesNotContain("lfs project -d", calder);
    }

    [Fact]
    public void SelectsTheHomeFilesystemWithoutAddingOtherQuotasOrInodes()
    {
        string extra = "/dev/other 50M 100M 100M - 1 2 2\n";
        var sample = StorageParser.Parse(AireOutput.Replace("HPCPEEK_HOME_STATUS", extra + "HPCPEEK_HOME_STATUS"), SampleTime);
        Assert.Equal(42968L * 1024 * 1024, sample.Home.UsedBytes);
        Assert.Equal(65L * 1024 * 1024 * 1024, sample.Home.LimitBytes);
        string ambiguous = AireOutput.Replace("HPCPEEK_HOME_STATUS", "10.0.0.2:/export/users 5G 65G 65G - 1 2 2\nHPCPEEK_HOME_STATUS");
        Assert.Null(StorageParser.Parse(ambiguous, SampleTime).Home.Fraction);
        var unrelated = AireOutput.Replace("10.0.0.1:/export/users", "10.0.0.1:/export/other");
        Assert.Null(StorageParser.Parse(unrelated, SampleTime).Home.Fraction);
    }

    [Fact]
    public void ParsesBinaryUnitsAndOverQuotaMarkerWithoutRejectingValidNonzeroExit()
    {
        var sample = StorageParser.Parse(AireOutput.Replace("614.4G      0k   1000G", "1.5TiB*    1T   2T")
            .Replace("HPCPEEK_SCRATCH_STATUS|0", "HPCPEEK_SCRATCH_STATUS|1"), SampleTime);
        Assert.Equal(1.5, sample.Scratch.Fraction);
    }

    [Fact]
    public void MissingCommandReportsADiagnosticInsteadOfZeroUsage()
    {
        var output = AireOutput.Replace("       /scratch  614.4G      0k   1000G       - 1333295       0 1500000       -", "")
            .Replace("HPCPEEK_SCRATCH_STATUS|0", "HPCPEEK_SCRATCH_STATUS|127");
        var scratch = StorageParser.Parse(output, SampleTime).Scratch;
        Assert.Null(scratch.Fraction);
        Assert.Null(scratch.SampledAt);
        Assert.Contains("unavailable in the remote SSH environment", scratch.Note);
    }

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
