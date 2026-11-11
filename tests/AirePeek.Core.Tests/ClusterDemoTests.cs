using AirePeek.Core;

namespace AirePeek.Core.Tests;

public sealed class ClusterDemoTests
{
    [Fact]
    public void CalderSimulationDeduplicatesPartitionsAndAccountsForExactlySevenEightCardNodes()
    {
        Assert.Equal(8, DemoSession.Calder7Nodes.Split('\n').Length); // Includes one partition duplicate.
        var snapshot = SlurmParser.ParseNodes(DemoSession.Calder7Nodes);
        Assert.Equal(7, snapshot.Nodes.Count);
        Assert.All(snapshot.Nodes, node => Assert.Equal(8, Assert.Single(node.Gpus).Configured));
        var summary = snapshot.Summarize("H200NVL");
        Assert.Equal(56, summary.Configured);
        Assert.Equal(28, summary.Allocated);
        Assert.Equal(20, summary.Unallocated);
        Assert.Equal(8, summary.MaxPerNode);
        Assert.Equal("gpu007", Assert.Single(summary.ExcludedNodes).Name);
        Assert.DoesNotContain(summary.Candidates, node => node.NodeName == "gpu007");
    }

    [Fact]
    public void CalderMissingAllocationRemainsUnknownWithoutAffectingAire()
    {
        var calder = SlurmParser.ParseNodes(DemoSession.CalderPartialNodes);
        var aire = SlurmParser.ParseNodes(DemoSession.Aire28Nodes);
        Assert.True(calder.IsPartial);
        Assert.Null(calder.Summarize("H200NVL").Unallocated);
        Assert.Equal(2, aire.Summarize("L40S").Unallocated);
        Assert.Equal(84, aire.Summarize("L40S").Configured);
    }
}
