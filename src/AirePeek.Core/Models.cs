namespace AirePeek.Core;

public sealed record SlurmJob(string Id, string Name, string State, string Elapsed, string TimeLimit, string Nodes, string Reason)
{
    public bool IsPending => State is "PENDING" or "PD";
    public bool IsRunning => State is "RUNNING" or "R" or "COMPLETING" or "CG";
    public string StateLabel => State switch
    {
        "PENDING" or "PD" => "Queued", "RUNNING" or "R" => "Running", "COMPLETING" or "CG" => "Completing",
        "COMPLETED" or "CD" => "Completed", "FAILED" or "F" => "Failed", "TIMEOUT" or "TO" => "Timed out",
        "CANCELLED" or "CA" => "Cancelled", "OUT_OF_MEMORY" or "OOM" => "Out of memory", _ => State
    };
}

public sealed record DepartedJob(SlurmJob Job, string State = "UNKNOWN", string ExitCode = "")
{
    public string StateLabel => State == "UNKNOWN" ? "Left queue; result pending" : (Job with { State = State }).StateLabel;
}

public sealed record JobSnapshot(DateTimeOffset SampledAt, IReadOnlyList<SlurmJob> Jobs, IReadOnlyList<DepartedJob> Departed);
public sealed record JobNotification(string JobId, string Title, string Message, string? State = null, string? JobName = null, string? Cluster = null);
public sealed record AccountingResult(string Id, string State, string ExitCode)
{
    public bool IsTerminal => State is "COMPLETED" or "FAILED" or "TIMEOUT" or "CANCELLED" or "OUT_OF_MEMORY" or "NODE_FAIL" or "PREEMPTED" or "BOOT_FAIL" or "DEADLINE" or "REVOKED";
}

public sealed record GpuCount(string Model, int? Configured, int? Allocated)
{
    public int? Available => Configured is int configured && Allocated is int allocated && allocated <= configured ? configured - allocated : null;
}

public sealed record GpuNode(string Name, string State, IReadOnlyList<GpuCount> Gpus, bool IsExcluded, bool IsPartial, string? Note)
{
    public bool HasKnownState => !GpuNodeStates.IsUnknown(State);
    public bool CanOfferCapacity => HasKnownState && !IsExcluded;
}

internal static class GpuNodeStates
{
    internal static bool IsUnknown(string? state) => string.IsNullOrWhiteSpace(state) ||
        state.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
        state.Trim().ToUpperInvariant() is "(NULL)" or "N/A" or "NONE" or "-";
}
public sealed record GpuCandidate(string NodeName, int Available);
public sealed record GpuSummary(string Model, int? Configured, int? Allocated, int? Unallocated, int? MaxPerNode,
    IReadOnlyList<GpuCandidate> Candidates, IReadOnlyList<GpuNode> ExcludedNodes, bool IsPartial);

public sealed record GpuSnapshot(DateTimeOffset SampledAt, IReadOnlyList<GpuNode> Nodes, bool IsPartial)
{
    public IReadOnlyList<string> Models => Nodes.SelectMany(n => n.Gpus).Select(g => g.Model).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public GpuSummary Summarize(string model, int requiredPerNode = 1)
    {
        if (requiredPerNode < 1) throw new ArgumentOutOfRangeException(nameof(requiredPerNode));
        var matching = Nodes.Select(n => (Node: n, Gpu: n.Gpus.FirstOrDefault(g => g.Model.Equals(model, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Gpu is not null).Select(x => (x.Node, Gpu: x.Gpu!)).ToArray();
        var eligible = matching.Where(x => x.Node.CanOfferCapacity).ToArray();
        // Unknown quantities remain unknown; known nodes may still be listed as candidates.
        bool hasUnattributed = Nodes.Any(n => n.Gpus.Any(g => g.Model.Equals(SlurmParser.UnknownModel, StringComparison.OrdinalIgnoreCase)));
        bool hasEligibleUnattributed = Nodes.Any(n => n.CanOfferCapacity && n.Gpus.Any(g => g.Model.Equals(SlurmParser.UnknownModel, StringComparison.OrdinalIgnoreCase)));
        bool unknown = eligible.Any(x => x.Gpu.Available is null) || hasEligibleUnattributed || (Nodes.Count == 0 && IsPartial);
        var candidates = eligible.Where(x => !x.Gpu.Model.Equals(SlurmParser.UnknownModel, StringComparison.OrdinalIgnoreCase) && x.Gpu.Available >= requiredPerNode)
            .Select(x => new GpuCandidate(x.Node.Name, x.Gpu.Available!.Value)).OrderByDescending(c => c.Available).ThenBy(c => c.NodeName, StringComparer.Ordinal).ToArray();
        return new(model,
            SumOrUnknown(matching.Select(x => x.Gpu.Configured), hasUnattributed),
            SumOrUnknown(matching.Select(x => x.Gpu.Allocated), hasUnattributed),
            unknown ? null : eligible.Sum(x => x.Gpu.Available!.Value),
            unknown ? null : eligible.Select(x => x.Gpu.Available!.Value).DefaultIfEmpty(0).Max(),
            candidates, matching.Where(x => !x.Node.CanOfferCapacity).Select(x => x.Node).ToArray(),
            IsPartial || matching.Any(x => x.Node.IsPartial || !x.Node.HasKnownState) || hasUnattributed);
    }

    private static int? SumOrUnknown(IEnumerable<int?> counts, bool unknown)
    {
        var items = counts.ToArray();
        return unknown || items.Any(x => x is null) ? null : items.Sum(x => x!.Value);
    }
}
