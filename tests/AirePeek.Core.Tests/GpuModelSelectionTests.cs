using AirePeek.Core;

namespace AirePeek.Core.Tests;

public sealed class GpuModelSelectionTests
{
    [Fact]
    public void AllSevenIdleCalderNodesExposeFiftySixFreeCardsWithoutDoubleCountingTres()
    {
        var lines = Enumerable.Range(1, 7).Select(i => $"NodeName=gpu{i:000} State=IDLE CfgTRES=cpu=128,gres/gpu=8,gres/gpu:h200nvl=8 AllocTRES=gres/gpu=0");
        var sample = SlurmParser.ParseNodes(string.Join('\n', lines.Concat(lines)));
        var stats = sample.Summarize("H200NVL", 8);
        Assert.Equal(56, ClusterCatalog.Calder.ConfiguredGpuCount);
        Assert.Equal(7, sample.Nodes.Count);
        Assert.Equal(56, stats.Configured);
        Assert.Equal(56, stats.Unallocated);
        Assert.Equal(8, stats.MaxPerNode);
        Assert.Equal(7, stats.Candidates.Count);
        Assert.False(stats.IsPartial);
    }
    [Theory]
    [InlineData("L40")]
    [InlineData("L40S")]
    [InlineData("Unknown model")]
    public void CalderNeverInheritsAnUnsupportedPreferenceBeforeItsFirstSample(string saved)
    {
        var choice = GpuModelSelection.Resolve(ClusterCatalog.Calder, null, saved);
        Assert.Equal(new[] { "H200NVL" }, choice.Models);
        Assert.Equal("H200NVL", choice.SelectedModel);
        Assert.True(choice.UsesDocumentedDefault);
    }

    [Fact]
    public void LiveCalderModelsDoNotAppendAStaleSavedL40SOption()
    {
        var sample = SlurmParser.ParseNodes(DemoSession.Calder7Nodes);
        var choice = GpuModelSelection.Resolve(ClusterCatalog.Calder, sample, "L40S");
        Assert.Equal(new[] { "H200NVL" }, choice.Models);
        Assert.Equal("H200NVL", choice.SelectedModel);
        Assert.False(choice.UsesDocumentedDefault);
        Assert.False(choice.HasUnexpectedModel);
    }

    [Fact]
    public void ExplicitlyReportedUnexpectedHardwareIsNotHiddenByPublishedSpecifications()
    {
        var sample = SlurmParser.ParseNodes("NodeName=additional State=IDLE Gres=gpu:l40s:4 GresUsed=gpu:l40s:0");
        var choice = GpuModelSelection.Resolve(ClusterCatalog.Calder, sample, "H200NVL");
        Assert.Equal(new[] { "L40S" }, choice.Models);
        Assert.Equal("L40S", choice.SelectedModel);
        Assert.False(choice.UsesDocumentedDefault);
        Assert.True(choice.HasUnexpectedModel);
    }

    [Fact]
    public void MissingAllocationRetainsTheModelAndUnknownCapacity()
    {
        var sample = SlurmParser.ParseNodes("NodeName=gpu001 State=MIXED Gres=gpu:h200nvl:8");
        var choice = GpuModelSelection.Resolve(ClusterCatalog.Calder, sample, "H200NVL");
        Assert.Equal("H200NVL", choice.SelectedModel);
        Assert.True(sample.IsPartial);
        Assert.Null(sample.Summarize(choice.SelectedModel).Unallocated);
        Assert.Null(sample.Summarize(choice.SelectedModel).MaxPerNode);
    }

    [Fact]
    public void UnknownOnlySamplesUseTheClusterModelWithoutInventingAvailableCards()
    {
        var sample = SlurmParser.ParseNodes("NodeName=gpu001 State=MIXED CfgTRES=gres/gpu=8 AllocTRES=gres/gpu=5");
        var choice = GpuModelSelection.Resolve(ClusterCatalog.Calder, sample, "L40S");
        Assert.Equal(new[] { "H200NVL" }, choice.Models);
        Assert.True(choice.UsesDocumentedDefault);
        var tile = Assert.Single(GpuNodeProjection.Create(sample, choice.SelectedModel));
        Assert.Equal(GpuNodeAvailability.Unknown, tile.Availability);
        Assert.Null(tile.Available);
        Assert.Null(sample.Summarize(choice.SelectedModel).Unallocated);
    }

    [Fact]
    public void MixedTypedAndUnattributedDataKeepsOnlyRealModelChoicesAndShowsUnknownTiles()
    {
        var sample = SlurmParser.ParseNodes("""
            NodeName=gpu001 State=IDLE Gres=gpu:h200nvl:8 GresUsed=gpu:h200nvl:0
            NodeName=gpu002 State=MIXED CfgTRES=gres/gpu=8 AllocTRES=gres/gpu=3
            """);
        var choice = GpuModelSelection.Resolve(ClusterCatalog.Calder, sample, "H200NVL");
        Assert.Equal(new[] { "H200NVL" }, choice.Models);
        var tiles = GpuNodeProjection.Create(sample, choice.SelectedModel);
        Assert.Equal(8, tiles[0].Available);
        Assert.Equal(GpuNodeAvailability.Unknown, tiles[1].Availability);
        Assert.Null(sample.Summarize(choice.SelectedModel).MaxPerNode);
    }

    [Fact]
    public void LiveSpellingIsUsedAndCaseInsensitivePreferenceSurvives()
    {
        var sample = SlurmParser.ParseNodes("NodeName=gpu001 State=IDLE Gres=gpu:h200_nvl:8 GresUsed=gpu:h200_nvl:0");
        var choice = GpuModelSelection.Resolve(ClusterCatalog.Calder, sample, "h200_nvl");
        Assert.Equal("H200_NVL", choice.SelectedModel);
        Assert.Equal(8, sample.Summarize(choice.SelectedModel).MaxPerNode);
    }

    [Fact]
    public void EightFreeCardsAreNeverClampedToTheFormerThreePlusColorBucket()
    {
        var sample = SlurmParser.ParseNodes("NodeName=gpu001 State=IDLE Gres=gpu:h200nvl:8 GresUsed=gpu:h200nvl:0");
        var stats = sample.Summarize("H200NVL");
        var tile = Assert.Single(GpuNodeProjection.Create(sample, "H200NVL"));
        Assert.Equal(8, stats.Configured);
        Assert.Equal(8, stats.Unallocated);
        Assert.Equal(8, stats.MaxPerNode);
        Assert.Equal(8, tile.Available);
    }

    [Fact]
    public void ThreeFreeOnAnEightCardNodeIsCurrentAvailabilityNotHardwareCapacity()
    {
        var sample = SlurmParser.ParseNodes("NodeName=gpu001 State=MIXED Gres=gpu:h200nvl:8 GresUsed=gpu:h200nvl:5");
        var stats = sample.Summarize("H200NVL");
        var tile = Assert.Single(GpuNodeProjection.Create(sample, "H200NVL"));
        Assert.Equal(8, stats.Configured);
        Assert.Equal(5, stats.Allocated);
        Assert.Equal(3, stats.Unallocated);
        Assert.Equal(3, stats.MaxPerNode);
        Assert.Equal(8, tile.Configured);
        Assert.Equal(3, tile.Available);
    }
}
