namespace AirePeek.Core;

/// <summary>Published hardware context, not a promise about live availability or an SSH login address.</summary>
public sealed record ClusterDefinition(string Id, string Name, string DefaultGpuModel, int NodeCount, int GpusPerNode, string HardwareDocumentationUrl)
{
    public int ConfiguredGpuCount => NodeCount * GpusPerNode;
}

public static class ClusterCatalog
{
    public const string AireId = "aire";
    public const string CalderId = "calder";
    public static ClusterDefinition Aire { get; } = new(AireId, "Aire", "L40S", 28, 3,
        "https://arcdocs.leeds.ac.uk/aire/system/node_types.html");
    public static ClusterDefinition Calder { get; } = new(CalderId, "Calder", "H200NVL", 7, 8,
        "https://arcdocs.leeds.ac.uk/calder/system/node_types.html");
    public static IReadOnlyList<ClusterDefinition> All { get; } = Array.AsReadOnly(new[] { Aire, Calder });
    public static ClusterDefinition Get(string id) => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentOutOfRangeException(nameof(id), "Unknown cluster.");
}
