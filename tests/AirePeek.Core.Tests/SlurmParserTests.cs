using AirePeek.Core;

namespace AirePeek.Core.Tests;

public class SlurmParserTests
{
    private static string Node(string name, int configured, int allocated, string state = "MIXED") =>
        $"NodeName={name} State={state} CfgTRES=cpu=64,gres/gpu={configured},gres/gpu:l40s={configured} AllocTRES=cpu=32,gres/gpu={allocated},gres/gpu:l40s={allocated}";

    [Fact]
    public void TwoNodesEachWithOneFreeGpu_DoNotOfferSingleNodeDualGpu()
    {
        var summary = SlurmParser.ParseNodes(Node("gpu-a", 4, 3) + "\n" + Node("gpu-b", 4, 3)).Summarize("L40S", 2);
        Assert.Equal(2, summary.Unallocated);
        Assert.Equal(1, summary.MaxPerNode);
        Assert.Empty(summary.Candidates);
    }

    [Fact]
    public void GenericAndTypedTresAreNotAddedTogether()
    {
        var summary = SlurmParser.ParseNodes(Node("gpu-a", 4, 2)).Summarize("L40S", 2);
        Assert.Equal(4, summary.Configured);
        Assert.Equal(2, summary.Allocated);
        Assert.Equal(2, summary.Unallocated);
        Assert.Single(summary.Candidates);
    }

    [Fact]
    public void CrossPartitionRecordsAreDeduplicatedByRealNodeName()
    {
        var summary = SlurmParser.ParseNodes(Node("gpu-a", 4, 2) + " Partitions=short\n" + Node("gpu-a", 4, 2) + " Partitions=gpu").Summarize("l40s");
        Assert.Equal(4, summary.Configured);
        Assert.Equal(2, summary.Unallocated);
        Assert.Single(summary.Candidates);
    }

    [Theory]
    [InlineData("DOWN")]
    [InlineData("MIXED+DRAIN")]
    [InlineData("IDLE+MAINT")]
    [InlineData("DRAINING")]
    [InlineData("FAIL")]
    public void UnavailableNodesExcludedFromBothCompactMetrics(string state)
    {
        var summary = SlurmParser.ParseNodes(Node("blocked", 8, 0, state) + "\n" + Node("usable", 4, 3)).Summarize("L40S");
        Assert.Equal(12, summary.Configured);
        Assert.Equal(1, summary.Unallocated);
        Assert.Equal(1, summary.MaxPerNode);
        Assert.Single(summary.ExcludedNodes);
        Assert.Equal("usable", Assert.Single(summary.Candidates).NodeName);
    }

    [Fact]
    public void GresAndGresUsedSupportTopologyLists()
    {
        var summary = SlurmParser.ParseNodes("NodeName=n State=MIXED Gres=gpu:l40s:4(S:0-1) GresUsed=gpu:l40s:2(IDX:0,2)").Summarize("L40S");
        Assert.Equal(4, summary.Configured);
        Assert.Equal(2, summary.Unallocated);
        Assert.False(summary.IsPartial);
    }

    [Fact]
    public void MissingAllocationIsUnknown_NotZero()
    {
        var summary = SlurmParser.ParseNodes("NodeName=n State=MIXED CfgTRES=gres/gpu=4,gres/gpu:l40s=4").Summarize("L40S");
        Assert.Null(summary.Allocated);
        Assert.Null(summary.Unallocated);
        Assert.Null(summary.MaxPerNode);
        Assert.Empty(summary.Candidates);
        Assert.True(summary.IsPartial);
    }

    [Fact]
    public void ExplicitEmptyAllocTresMeansNoResourcesAllocated()
    {
        var summary = SlurmParser.ParseNodes("NodeName=n State=IDLE CfgTRES=gres/gpu=4,gres/gpu:l40s=4 AllocTRES=").Summarize("L40S");
        Assert.Equal(4, summary.Unallocated);
        Assert.False(summary.IsPartial);
    }

    [Fact]
    public void SingleKnownTypeCanUseGenericAllocation()
    {
        var summary = SlurmParser.ParseNodes("NodeName=n State=MIXED Gres=gpu:l40s:4 CfgTRES=gres/gpu=4 AllocTRES=gres/gpu=1").Summarize("L40S");
        Assert.Equal(3, summary.Unallocated);
    }

    [Fact]
    public void AmbiguousMultiModelAllocationRemainsUnknown()
    {
        var snapshot = SlurmParser.ParseNodes("NodeName=n State=MIXED CfgTRES=gres/gpu=4,gres/gpu:l40s=2,gres/gpu:h100=2 AllocTRES=gres/gpu=1");
        Assert.Null(snapshot.Summarize("L40S").Unallocated);
        Assert.Null(snapshot.Summarize("H100").Unallocated);
        Assert.Empty(snapshot.Summarize("H100").Candidates);
    }

    [Fact]
    public void MultiModelZeroAllocationIsUnambiguous()
    {
        var snapshot = SlurmParser.ParseNodes("NodeName=n State=IDLE CfgTRES=gres/gpu=4,gres/gpu:l40s=2,gres/gpu:h100=2 AllocTRES=");
        Assert.Equal(2, snapshot.Summarize("L40S").Unallocated);
        Assert.Equal(2, snapshot.Summarize("H100").Unallocated);
    }

    [Theory]
    [InlineData("gres/gpu=3,gres/gpu:l40s=1")]
    [InlineData("gres/gpu=1,gres/gpu:h100=1")]
    public void UnexplainedAllocationsCannotProduceCandidates(string allocations)
    {
        var snapshot = SlurmParser.ParseNodes($"NodeName=n State=MIXED CfgTRES=gres/gpu=4,gres/gpu:l40s=4 AllocTRES={allocations}");
        Assert.True(snapshot.IsPartial);
        Assert.Null(snapshot.Summarize("L40S").Unallocated);
        Assert.Empty(snapshot.Summarize("L40S").Candidates);
    }

    [Fact]
    public void GenericOnlyModelIsNotInvented()
    {
        var snapshot = SlurmParser.ParseNodes("NodeName=n State=IDLE CfgTRES=gres/gpu=4 AllocTRES=");
        Assert.Contains(SlurmParser.UnknownModel, snapshot.Models);
        Assert.Null(snapshot.Summarize(SlurmParser.UnknownModel).Unallocated);
        Assert.Null(snapshot.Summarize("L40S").Unallocated);
    }

    [Theory]
    [InlineData("gpu:a100_1g.5gb:7", "gpu:a100_1g.5gb:2")]
    [InlineData("gpu:a100:4,mps:400", "gpu:a100:2,mps:100")]
    [InlineData("gpu:a100:4,shard:128", "gpu:a100:2,shard:32")]
    public void MigAndSharedResourcesAreNotWholeCardCapacity(string configured, string used)
    {
        var snapshot = SlurmParser.ParseNodes($"NodeName=n State=MIXED Gres={configured} GresUsed={used}");
        var summary = snapshot.Summarize(snapshot.Models[0]);
        Assert.True(snapshot.IsPartial);
        Assert.Null(summary.Configured);
        Assert.Null(summary.Unallocated);
        Assert.Empty(summary.Candidates);
    }

    [Fact]
    public void ConflictingDuplicateRecordsAreConservative()
    {
        var snapshot = SlurmParser.ParseNodes(Node("n", 4, 1) + "\n" + Node("n", 4, 2));
        Assert.Single(snapshot.Nodes);
        Assert.Null(snapshot.Summarize("L40S").Unallocated);
        Assert.True(snapshot.IsPartial);
    }

    [Fact]
    public void MissingStateCannotOfferCandidate()
    {
        var summary = SlurmParser.ParseNodes("NodeName=n CfgTRES=gres/gpu:l40s=4 AllocTRES=").Summarize("L40S");
        Assert.Empty(summary.Candidates);
        Assert.True(summary.IsPartial);
    }

    [Fact]
    public void MissingAllGpuFieldsIsPartial()
    {
        var snapshot = SlurmParser.ParseNodes("NodeName=n State=MIXED");
        Assert.True(snapshot.IsPartial);
        Assert.Null(snapshot.Summarize("L40S").Unallocated);
    }

    [Fact]
    public void UnknownGpuOnDownNodeDoesNotPoisonEligibleCapacity()
    {
        var summary = SlurmParser.ParseNodes(Node("usable", 4, 3) + "\nNodeName=down State=DOWN CfgTRES=gres/gpu=8 AllocTRES=").Summarize("L40S");
        Assert.Equal(1, summary.Unallocated);
        Assert.Equal(1, summary.MaxPerNode);
        Assert.True(summary.IsPartial);
    }

    [Fact]
    public void OverAllocationIsUnknownInsteadOfNegative()
    {
        var summary = SlurmParser.ParseNodes(Node("n", 4, 5)).Summarize("L40S");
        Assert.Null(summary.Unallocated);
        Assert.Empty(summary.Candidates);
    }

    [Fact]
    public void EmptyNodeResponseIsPartiallyVisible()
    {
        var snapshot = SlurmParser.ParseNodes("");
        Assert.True(snapshot.IsPartial);
        Assert.Null(snapshot.Summarize("L40S").Unallocated);
    }

    [Fact]
    public void DemoNumbersUseSameEligibleRange()
    {
        var summary = SlurmParser.ParseNodes(DemoSession.NormalNodes).Summarize("L40S", 2);
        Assert.Equal(5, summary.Unallocated);
        Assert.Equal(2, summary.MaxPerNode);
        Assert.Equal(2, summary.Candidates.Count);
        Assert.Single(summary.ExcludedNodes);
    }

    [Fact]
    public void QueueRetainsArrayTaskRangeAndLongNames()
    {
        var jobs = SlurmParser.ParseQueue("123_[1-8%2]|a long | experiment|PENDING|0:00|12:00:00|(null)|Priority\n124_3|train|RUNNING|01:20:30|2-00:00:00|n01|None");
        Assert.Equal("123_[1-8%2]", jobs[0].Id);
        Assert.Equal("a long | experiment", jobs[0].Name);
        Assert.Equal("Priority", jobs[0].Reason);
        Assert.True(jobs[0].IsPending);
        Assert.True(jobs[1].IsRunning);
        Assert.Equal("124_3", jobs[1].Id);
    }

    [Fact] public void EmptyQueueIsAValidSuccess() => Assert.Empty(SlurmParser.ParseQueue("\n"));
    [Fact] public void MalformedQueueDoesNotBecomeEmptySuccess() => Assert.Throws<FormatException>(() => SlurmParser.ParseQueue("Access denied"));

    [Fact]
    public void AccountingIgnoresStepsAndNormalizesTerminalState()
    {
        var records = SlurmParser.ParseAccounting("123_2|COMPLETED|0:0\n123_2.batch|FAILED|1:0\n124|CANCELLED by 42|0:15");
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.True(r.IsTerminal));
        Assert.Equal("CANCELLED", records[1].State);
    }

    [Theory]
    [InlineData("1'; touch /tmp/bad; '")]
    [InlineData("1\n2")]
    [InlineData("--allusers")]
    [InlineData("123.batch")]
    public void AccountingRejectsUnsafeOrStepIds(string id) => Assert.Throws<ArgumentException>(() => SlurmCommands.Accounting([id]));

    [Fact]
    public void AccountingAcceptsArrayIdentity()
    {
        var command = SlurmCommands.Accounting(["123_4", "124_[1-9%2]"]);
        Assert.Contains("--jobs='123_4,124_[1-9%2]'", command);
        Assert.Contains("--array", command);
    }
}
