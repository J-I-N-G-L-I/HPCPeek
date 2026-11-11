namespace AirePeek.Core;

/// <summary>Model choices supported by the current sample, or the documented cluster default before attribution is known.</summary>
public sealed record GpuModelSelection(IReadOnlyList<string> Models, string SelectedModel, bool UsesDocumentedDefault)
{
    /// <summary>Observed L40-family resources under Calder may indicate an Aire address was entered for Calder.</summary>
    public bool HasUnexpectedModel { get; init; }
    public static GpuModelSelection Resolve(ClusterDefinition cluster, GpuSnapshot? snapshot, string? rememberedModel)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var models = snapshot?.Models.Where(model => !string.IsNullOrWhiteSpace(model) &&
                !model.Equals(SlurmParser.UnknownModel, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if (models.Length == 0)
        {
            // A preference is not evidence that hardware exists on this cluster. In particular,
            // old Aire model preferences must not introduce an L40S option under Calder.
            return new(Array.AsReadOnly(new[] { cluster.DefaultGpuModel }), cluster.DefaultGpuModel, true);
        }

        // Never rewrite the raw model key supplied by Slurm; it is used by the statistics and projection.
        var selected = models.FirstOrDefault(model => model.Equals(rememberedModel?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(model => Equivalent(model, cluster.DefaultGpuModel))
            ?? models[0];
        return new(Array.AsReadOnly(models), selected, false)
        {
            HasUnexpectedModel = cluster.Id == ClusterCatalog.CalderId && models.Any(model => Normalize(model) is "L40" or "L40S")
        };
    }

    private static bool Equivalent(string left, string right) =>
        Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string model) => new string(model.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
