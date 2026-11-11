using AirePeek.Core;

namespace AirePeek.Core.Tests;

public class GpuNodeProjectionTests
{
    private static string Sample => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "aire28-sinfo.txt"));

    [Fact]
    public void SuppliedTwentyEightNodesExposeOnlyGpu020AsDualCardCandidate()
    {
        var snapshot = SlurmParser.ParseSinfoNodes(Sample);
        var summary = snapshot.Summarize("L40S", 2);
        var tiles = GpuNodeProjection.Create(snapshot, "l40s", 2);

        Assert.Equal(28, snapshot.Nodes.Count);
        Assert.Equal(84, summary.Configured);
        Assert.Equal(73, summary.Allocated);
        Assert.Equal(2, summary.Unallocated);
        Assert.Equal(2, summary.MaxPerNode);
        Assert.Equal("gpu020", Assert.Single(summary.Candidates).NodeName);
        Assert.Equal(3, summary.ExcludedNodes.Count);
        Assert.False(summary.IsPartial);
        Assert.Equal(25, snapshot.Nodes.Count(n => !n.IsExcluded));
        Assert.Equal(75, snapshot.Nodes.Where(n => !n.IsExcluded).Sum(n => n.Gpus.Single().Configured));

        Assert.Equal(28, tiles.Count);
        Assert.Equal(Enumerable.Range(1, 28).Select(n => $"gpu{n:000}"), tiles.Select(t => t.Name));
        Assert.Equal(24, tiles.Count(t => t.Availability == GpuNodeAvailability.Full));
        Assert.Equal("gpu020", Assert.Single(tiles, t => t.IsCandidate).Name);
        Assert.Equal(2, tiles.Single(t => t.Name == "gpu020").Available);
        Assert.Equal(new[] { "gpu021", "gpu023", "gpu028" }, tiles.Where(t => t.Availability == GpuNodeAvailability.Unavailable).Select(t => t.Name));
        Assert.All(tiles.Where(t => t.Availability == GpuNodeAvailability.Unavailable), tile =>
        {
            Assert.Null(tile.Available);
            Assert.False(tile.IsCandidate);
            Assert.Equal("Unavailable", tile.StatusText);
            Assert.DoesNotContain("3 free", tile.DetailText);
        });
    }

    [Fact]
    public void DemoTwentyEightNodeRecordsMatchTheSuppliedSinfoSample()
    {
        var expected = SlurmParser.ParseSinfoNodes(Sample);
        var actual = SlurmParser.ParseNodes(DemoSession.Aire28Nodes);
        Assert.Equal(expected.Nodes.Count, actual.Nodes.Count);
        for (int n = 0; n < actual.Nodes.Count; n++)
        {
            Assert.Equal(expected.Nodes[n].Name, actual.Nodes[n].Name);
            Assert.Equal(expected.Nodes[n].State, actual.Nodes[n].State);
            Assert.Equal(expected.Nodes[n].Gpus, actual.Nodes[n].Gpus);
            Assert.Equal(expected.Nodes[n].IsExcluded, actual.Nodes[n].IsExcluded);
        }
        Assert.Equal(GpuNodeProjection.Create(expected, "L40S", 2), GpuNodeProjection.Create(actual, "L40S", 2));
    }

    [Fact]
    public async Task TwentyEightNodeDemoUsesTheExistingMonitorAndParsers()
    {
        await using var session = new DemoSession { Scenario = DemoScenario.Aire28 };
        await session.ConnectAsync(new(new("demo", 22, "demo")), new UnusedAuthentication(), new UnusedKeys());
        var monitor = new MonitorService(session);
        monitor.ResetAfterConnect(); await monitor.RefreshAsync();
        Assert.Equal(28, monitor.Gpus!.Nodes.Count);
        Assert.Equal(2, monitor.Gpus.Summarize("L40S", 2).Unallocated);
        Assert.Single(monitor.Gpus.Summarize("L40S", 2).Candidates);
        Assert.Equal(2, monitor.Jobs!.Jobs.Count(j => j.IsRunning));
        Assert.All(monitor.Jobs.Jobs.Where(j => j.IsRunning), job => Assert.Contains(monitor.Gpus.Nodes, node => node.Name == job.Nodes));
        Assert.Single(monitor.Jobs.Jobs, j => j.IsPending);
        Assert.Equal(1, session.ConnectionCount);
    }

    [Fact]
    public void MixedStateDoesNotImplyFreeGpus()
    {
        var tile = Assert.Single(GpuNodeProjection.Create(SlurmParser.ParseNodes("NodeName=gpu1 State=MIXED Gres=gpu:l40s:3 GresUsed=gpu:l40s:3(IDX:0-2)"), "L40S"));
        Assert.Equal(GpuNodeAvailability.Full, tile.Availability);
        Assert.Equal(0, tile.Available);
        Assert.False(tile.IsCandidate);
        Assert.Equal("Full", tile.StatusText);
    }

    [Fact]
    public void MissingAllocationAndUnknownStateRemainDistinctFromFullOrFree()
    {
        var snapshot = SlurmParser.ParseNodes("NodeName=gpu1 State=MIXED Gres=gpu:l40s:3\nNodeName=gpu2 Gres=gpu:l40s:3 GresUsed=gpu:l40s:0(IDX:N/A)");
        Assert.All(GpuNodeProjection.Create(snapshot, "L40S", 2), tile =>
        {
            Assert.Equal(GpuNodeAvailability.Unknown, tile.Availability);
            Assert.Null(tile.Available);
            Assert.False(tile.IsCandidate);
            Assert.True(tile.IsPartial);
        });
    }

    [Fact]
    public void FilterIncludesUnattributedNodesAsUnknownButExcludesOtherKnownModels()
    {
        var snapshot = SlurmParser.ParseNodes("NodeName=gpu1 State=IDLE Gres=gpu:h100:4 GresUsed=gpu:h100:0\nNodeName=gpu2 State=IDLE Gres=gpu:4 GresUsed=gpu:0\nNodeName=gpu3 State=IDLE Gres=gpu:l40s:3 GresUsed=gpu:l40s:0");
        var tiles = GpuNodeProjection.Create(snapshot, "l40s", 2);
        Assert.Equal(new[] { "gpu2", "gpu3" }, tiles.Select(t => t.Name));
        Assert.Equal(SlurmParser.UnknownModel, tiles[0].Model);
        Assert.Equal(GpuNodeAvailability.Unknown, tiles[0].Availability);
        Assert.Null(tiles[0].Available);
        Assert.Equal(GpuNodeAvailability.Available, tiles[1].Availability);
        Assert.Equal("3 free", tiles[1].StatusText);
    }

    [Theory]
    [InlineData("gpu:a100_1g.5gb:7", "gpu:a100_1g.5gb:2", "A100_1G.5GB")]
    [InlineData("gpu:a100:4,mps:400", "gpu:a100:1,mps:100", "A100")]
    public void SharedAndMigCapacityNeverProducesAvailableTiles(string configuration, string allocation, string model)
    {
        var tile = Assert.Single(GpuNodeProjection.Create(SlurmParser.ParseNodes($"NodeName=n State=MIXED Gres={configuration} GresUsed={allocation}"), model));
        Assert.Equal(GpuNodeAvailability.Unknown, tile.Availability);
        Assert.Null(tile.Available);
        Assert.False(tile.IsCandidate);
    }

    [Fact]
    public void NodeOrderIsNaturalWithoutIntegerOverflow()
    {
        string[] names = ["gpu10", "gpu2", "gpu001", "gpu00000000000000000000000000000003", "gpu10000000000000000000000000000000", "gpu20"];
        var snapshot = SlurmParser.ParseNodes(string.Join('\n', names.Select(n => $"NodeName={n} State=IDLE Gres=gpu:l40s:3 GresUsed=gpu:l40s:0")));
        Assert.Equal(new[] { "gpu001", "gpu2", "gpu00000000000000000000000000000003", "gpu10", "gpu20", "gpu10000000000000000000000000000000" }, GpuNodeProjection.Create(snapshot, "L40S").Select(t => t.Name));
    }

    [Fact]
    public void DuplicatePartitionRecordsDoNotAddTiles()
    {
        var sample = Sample + "\ngpu020 mixed gpu:l40s:3 gpu:l40s:1(IDX:2)";
        var snapshot = SlurmParser.ParseSinfoNodes(sample);
        Assert.Equal(28, GpuNodeProjection.Create(snapshot, "L40S").Count);
        Assert.Equal(2, snapshot.Summarize("L40S").Unallocated);
    }

    [Fact]
    public void BelowRequirementStillShowsItsKnownFreeCapacity()
    {
        var tiles = GpuNodeProjection.Create(SlurmParser.ParseNodes(DemoSession.Aire28Nodes), "L40S", 3);
        var tile = tiles.Single(t => t.Name == "gpu020");
        Assert.Equal(GpuNodeAvailability.Available, tile.Availability);
        Assert.Equal(2, tile.Available);
        Assert.False(tile.IsCandidate);
        Assert.Contains("Below the 3-GPU requirement", tile.DetailText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    [InlineData("unknown*")]
    [InlineData("(null)")]
    [InlineData("N/A")]
    public void UnknownStateHasTheSameCandidateScopeInSummaryAndMatrix(string state)
    {
        var snapshot = SlurmParser.ParseNodes($"NodeName=gpu1 State={state} Gres=gpu:l40s:3 GresUsed=gpu:l40s:0");
        var summary = snapshot.Summarize("L40S", 2);
        var tile = Assert.Single(GpuNodeProjection.Create(snapshot, "L40S", 2));
        Assert.Empty(summary.Candidates);
        Assert.Equal(0, summary.Unallocated);
        Assert.Equal(0, summary.MaxPerNode);
        Assert.True(summary.IsPartial);
        Assert.Single(summary.ExcludedNodes);
        Assert.Equal(GpuNodeAvailability.Unknown, tile.Availability);
        Assert.Null(tile.Available);
        Assert.False(tile.IsCandidate);
    }

    [Fact]
    public void SummaryDoesNotRelyOnCallersSettingTheUnknownStateExclusionFlag()
    {
        var snapshot = new GpuSnapshot(DateTimeOffset.UtcNow,
            [new("gpu1", "UNKNOWN", [new("L40S", 3, 0)], false, false, null)], false);
        Assert.Empty(snapshot.Summarize("L40S", 2).Candidates);
        Assert.Equal(0, snapshot.Summarize("L40S", 2).Unallocated);
        Assert.False(Assert.Single(GpuNodeProjection.Create(snapshot, "L40S", 2)).IsCandidate);
    }

    [Fact]
    public void UnattributedCardsCannotBecomeCandidatesEvenIfCountsWereSupplied()
    {
        var snapshot = new GpuSnapshot(DateTimeOffset.UtcNow,
            [new("gpu1", "IDLE", [new("unknown model", 3, 0)], false, false, null)], false);
        Assert.Empty(snapshot.Summarize("Unknown model", 2).Candidates);
        Assert.Null(snapshot.Summarize("Unknown model", 2).Unallocated);
        Assert.False(Assert.Single(GpuNodeProjection.Create(snapshot, "Unknown model", 2)).IsCandidate);
    }

    [Theory]
    [InlineData("gpu[001-028] mixed gpu:l40s:3 gpu:l40s:0")]
    [InlineData("gpu020 mixed gpu:l40s:3")]
    [InlineData("permission denied")]
    public void SinfoAdapterRejectsRangesOrMalformedRowsInsteadOfInventingCapacity(string value) =>
        Assert.Throws<FormatException>(() => SlurmParser.ParseSinfoNodes(value));

    private sealed class UnusedAuthentication : IAuthenticationUI
    {
        public Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken) => throw new InvalidOperationException("Offline Demo must not authenticate.");
        public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken) => throw new InvalidOperationException("Offline Demo must not request host trust.");
    }
    private sealed class UnusedKeys : IHostKeyStore
    {
        public string? Get(SshEndpoint endpoint) => throw new InvalidOperationException();
        public void Save(SshEndpoint endpoint, string sha256) => throw new InvalidOperationException();
    }
}
