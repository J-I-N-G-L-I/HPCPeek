namespace AirePeek.Core;

public enum GpuNodeAvailability { Available, Full, Unavailable, Unknown }

/// <summary>A node-sized view of whole-card capacity; unknown and unavailable nodes are never shown as free.</summary>
public sealed record GpuNodeTile(string Name, string Model, string State, int? Configured, int? Allocated,
    int? Available, GpuNodeAvailability Availability, bool IsCandidate, bool IsPartial, string StatusText, string DetailText);

public static class GpuNodeProjection
{
    public static IReadOnlyList<GpuNodeTile> Create(GpuSnapshot snapshot, string model, int requiredPerNode = 1)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (requiredPerNode < 1) throw new ArgumentOutOfRangeException(nameof(requiredPerNode));
        var result = new List<GpuNodeTile>();
        foreach (var node in snapshot.Nodes.OrderBy(n => n.Name, NodeNameComparer.Instance))
        {
            var gpu = node.Gpus.FirstOrDefault(g => g.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
            // Unattributed GPUs remain visible when filtering: their model cannot safely be ruled out.
            gpu ??= node.Gpus.FirstOrDefault(g => g.Model.Equals(SlurmParser.UnknownModel, StringComparison.OrdinalIgnoreCase));
            if (gpu is null) continue;
            bool stateUnknown = !node.HasKnownState;
            bool unknownModel = gpu.Model.Equals(SlurmParser.UnknownModel, StringComparison.OrdinalIgnoreCase);
            var availability = stateUnknown ? GpuNodeAvailability.Unknown
                : !node.CanOfferCapacity ? GpuNodeAvailability.Unavailable
                : unknownModel || gpu.Available is null ? GpuNodeAvailability.Unknown
                : gpu.Available == 0 ? GpuNodeAvailability.Full : GpuNodeAvailability.Available;
            int? available = availability is GpuNodeAvailability.Available or GpuNodeAvailability.Full ? gpu.Available : null;
            bool candidate = availability == GpuNodeAvailability.Available && available >= requiredPerNode;
            string status = availability switch
            {
                GpuNodeAvailability.Available => $"{available} free",
                GpuNodeAvailability.Full => "Full",
                GpuNodeAvailability.Unavailable => "Unavailable",
                _ => "Unknown"
            };
            string counts = $"{Count(gpu.Configured)} configured · {Count(gpu.Allocated)} allocated";
            string requirement = candidate ? $"Meets {requiredPerNode} GPU{(requiredPerNode == 1 ? "" : "s")} on one node"
                : availability == GpuNodeAvailability.Available ? $"Below the {requiredPerNode}-GPU requirement"
                : availability == GpuNodeAvailability.Unavailable ? "Excluded from available capacity"
                : availability == GpuNodeAvailability.Unknown ? "Not counted as available capacity" : "No free GPUs";
            string detail = $"{node.Name} · {gpu.Model}\n{status} · {node.State}\n{counts}\n{requirement}";
            if (!string.IsNullOrWhiteSpace(node.Note)) detail += "\n" + node.Note;
            result.Add(new(node.Name, gpu.Model, node.State, gpu.Configured, gpu.Allocated, available, availability,
                candidate, node.IsPartial || stateUnknown || unknownModel, status, detail));
        }
        return result;
    }

    private static string Count(int? value) => value?.ToString() ?? "Unknown";

    // Compare digit runs by magnitude instead of parsing into a bounded integer or sorting gpu10 before gpu2.
    private sealed class NodeNameComparer : IComparer<string>
    {
        internal static readonly NodeNameComparer Instance = new();
        public int Compare(string? left, string? right)
        {
            if (left is null || right is null) return string.Compare(left, right, StringComparison.Ordinal);
            int l = 0, r = 0;
            while (l < left.Length && r < right.Length)
            {
                if (char.IsAsciiDigit(left[l]) && char.IsAsciiDigit(right[r]))
                {
                    int leftStart = l, rightStart = r;
                    while (l < left.Length && char.IsAsciiDigit(left[l])) l++;
                    while (r < right.Length && char.IsAsciiDigit(right[r])) r++;
                    int ln = leftStart, rn = rightStart;
                    while (ln < l && left[ln] == '0') ln++;
                    while (rn < r && right[rn] == '0') rn++;
                    int difference = (l - ln).CompareTo(r - rn);
                    if (difference != 0) return difference;
                    difference = left.AsSpan(ln, l - ln).SequenceCompareTo(right.AsSpan(rn, r - rn));
                    if (difference != 0) return difference;
                    difference = (l - leftStart).CompareTo(r - rightStart);
                    if (difference != 0) return difference;
                }
                else
                {
                    int difference = char.ToUpperInvariant(left[l]).CompareTo(char.ToUpperInvariant(right[r]));
                    if (difference != 0) return difference;
                    l++; r++;
                }
            }
            int length = (left.Length - l).CompareTo(right.Length - r);
            return length != 0 ? length : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
