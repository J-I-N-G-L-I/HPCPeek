using System.Globalization;
using System.Text.RegularExpressions;

namespace AirePeek.Core;

public static partial class SlurmParser
{
    public const string UnknownModel = "Unknown model";

    public static IReadOnlyList<SlurmJob> ParseQueue(string output)
    {
        var jobs = new Dictionary<string, SlurmJob>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            var fields = line.Split('|');
            if (fields.Length < 7) throw new FormatException("Queue output is incomplete; retaining the last successful snapshot.");
            // Work back from fixed trailing columns so a pipe in a job name does not corrupt state/ID.
            int tail = fields.Length - 5;
            var job = new SlurmJob(fields[0].Trim(), string.Join('|', fields[1..tail]).Trim(), fields[tail].Trim().ToUpperInvariant(),
                fields[tail + 1].Trim(), fields[tail + 2].Trim(), fields[tail + 3].Trim(), fields[tail + 4].Trim());
            if (string.IsNullOrWhiteSpace(job.Id) || string.IsNullOrWhiteSpace(job.State)) throw new FormatException("Queue output is missing a job ID or state.");
            jobs[job.Id] = job;
        }
        return jobs.Values.ToArray();
    }

    public static IReadOnlyList<AccountingResult> ParseAccounting(string output)
    {
        var result = new List<AccountingResult>();
        foreach (var line in Lines(output))
        {
            var fields = line.Split('|');
            if (fields.Length < 3 || fields[0].Contains('.')) continue;
            var state = fields[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd('+').ToUpperInvariant() ?? "UNKNOWN";
            result.Add(new(fields[0].Trim(), state, fields[2].Trim()));
        }
        return result;
    }

    public static GpuSnapshot ParseNodes(string output, DateTimeOffset? sampledAt = null)
    {
        var nodes = new Dictionary<string, GpuNode>(StringComparer.Ordinal);
        bool partial = false;
        foreach (var line in Lines(output))
        {
            var fields = ParseFields(line);
            if (!fields.TryGetValue("NodeName", out var name) || string.IsNullOrWhiteSpace(name)) { partial = true; continue; }
            var node = ParseNode(name, fields);
            if (nodes.TryGetValue(name, out var previous))
            {
                // Partition membership is not identity. Contradictory duplicate records are uncertain.
                if (previous.State != node.State || !previous.Gpus.SequenceEqual(node.Gpus))
                {
                    var models = previous.Gpus.Concat(node.Gpus).Select(g => g.Model).Distinct(StringComparer.OrdinalIgnoreCase);
                    node = node with { Gpus = models.Select(m => new GpuCount(m, null, null)).ToArray(), IsPartial = true,
                        IsExcluded = previous.IsExcluded || node.IsExcluded, Note = "Conflicting records for this node; counts are unknown" };
                }
            }
            nodes[name] = node;
            partial |= node.IsPartial;
        }
        if (nodes.Count == 0) partial = true;
        return new(sampledAt ?? DateTimeOffset.UtcNow, nodes.Values.OrderBy(n => n.Name, StringComparer.Ordinal).ToArray(), partial);
    }

    /// <summary>Reads the four-column `sinfo -N -O NodeList,StateLong,Gres,GresUsed` shape supplied for offline comparison.</summary>
    public static GpuSnapshot ParseSinfoNodes(string output, DateTimeOffset? sampledAt = null)
    {
        var records = new List<string>();
        foreach (var line in Lines(output))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 4 && fields[0].Equals("NODELIST", StringComparison.OrdinalIgnoreCase) &&
                fields[1].Equals("STATE", StringComparison.OrdinalIgnoreCase) &&
                fields[2].Equals("GRES", StringComparison.OrdinalIgnoreCase) && fields[3].Equals("GRES_USED", StringComparison.OrdinalIgnoreCase)) continue;
            if (fields.Length != 4 || fields[0].IndexOfAny(['[', ']', ',']) >= 0)
                throw new FormatException("Expected one real node per row with NodeList, StateLong, Gres and GresUsed.");
            records.Add($"NodeName={fields[0]} State={fields[1]} Gres={fields[2]} GresUsed={fields[3]}");
        }
        // The same conservative field parser, duplicate handling and candidate rules remain authoritative.
        return ParseNodes(string.Join('\n', records), sampledAt);
    }

    private static GpuNode ParseNode(string name, Dictionary<string, string> f)
    {
        string state = f.GetValueOrDefault("State", "UNKNOWN").ToUpperInvariant();
        bool stateUnknown = GpuNodeStates.IsUnknown(state);
        bool excluded = stateUnknown || ExcludedState().IsMatch(state);
        bool partial = stateUnknown;
        var notes = new List<string>();
        if (stateUnknown) notes.Add("Node state is unknown");
        var cfgTres = ParseTres(f.GetValueOrDefault("CfgTRES"));
        var allocTres = ParseTres(f.GetValueOrDefault("AllocTRES"));
        var gres = ParseGres(f.GetValueOrDefault("Gres"));
        var used = ParseGres(f.GetValueOrDefault("GresUsed"));
        var configured = cfgTres.Typed.Count > 0 ? cfgTres.Typed : gres.Typed;
        int? configuredTotal = cfgTres.Total ?? gres.Total;
        if (configured.Count == 0 && configuredTotal > 0) configured = new(StringComparer.OrdinalIgnoreCase) { [UnknownModel] = configuredTotal.Value };
        bool fieldsMissing = !f.ContainsKey("CfgTRES") && !f.ContainsKey("Gres");
        if (fieldsMissing) { configured[UnknownModel] = null; partial = true; notes.Add("Configuration fields are missing"); }

        bool unsupported = SharedOrMig().IsMatch(f.GetValueOrDefault("Gres", "") + "," + f.GetValueOrDefault("CfgTRES", ""));
        if (unsupported) { partial = true; notes.Add("Shared GPU / MIG resources cannot be reliably converted to whole GPUs"); }
        bool hasAllocation = f.ContainsKey("AllocTRES") || f.ContainsKey("GresUsed");
        var allocated = allocTres.Typed.Count > 0 ? allocTres.Typed : used.Typed;
        int? allocatedTotal = allocTres.Total ?? used.Total;
        if (hasAllocation && allocated.Count == 0 && allocatedTotal is null && !allocTres.Invalid && !used.Invalid) allocatedTotal = 0;
        if (!hasAllocation && configured.Count > 0) { partial = true; notes.Add("Allocation fields are missing"); }
        if (cfgTres.Invalid || allocTres.Invalid || gres.Invalid || used.Invalid) { partial = true; notes.Add("GPU fields could not be fully parsed"); }
        if (configuredTotal is int configuredCount && configured.Values.All(v => v.HasValue) && configured.Values.Sum(v => v!.Value) != configuredCount)
        { partial = true; notes.Add("GPU total and model entries disagree"); configured[UnknownModel] = null; }

        var counts = new List<GpuCount>();
        var missingModels = configured.Keys.Where(m => !allocated.ContainsKey(m)).ToArray();
        int knownAllocated = allocated.Values.Where(v => v.HasValue).Sum(v => v!.Value);
        bool allocationInconsistent = allocated.Keys.Any(m => !configured.ContainsKey(m)) ||
            (allocatedTotal is int total && (knownAllocated > total || (missingModels.Length == 0 && knownAllocated != total)));
        if (allocationInconsistent) { partial = true; notes.Add("GPU allocation total and model entries disagree"); }
        foreach (var (model, config) in configured.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            int? alloc = null;
            if (allocated.TryGetValue(model, out var typedAllocation)) alloc = typedAllocation;
            else if (hasAllocation && allocatedTotal == 0) alloc = 0;
            else if (hasAllocation && allocatedTotal is int allocationCount && allocated.Values.All(v => v.HasValue))
            {
                int remainder = allocationCount - knownAllocated;
                if (remainder == 0 || missingModels.Length == 1) alloc = remainder;
            }
            // A complete per-type GresUsed with all configured models is usable without a generic total.
            if (unsupported || model == UnknownModel || allocationInconsistent) alloc = null;
            int? configValue = unsupported ? null : config;
            if (alloc < 0 || alloc > configValue) { alloc = null; partial = true; notes.Add("Allocation and configuration counts disagree"); }
            if (configValue is null || alloc is null) partial = true;
            counts.Add(new(model, configValue, alloc));
        }
        if (unsupported && counts.Count == 0) counts.Add(new(UnknownModel, null, null));
        if (configured.ContainsKey(UnknownModel)) { partial = true; notes.Add("GPU model attribution is unknown"); }
        return new(name, state, counts, excluded, partial, notes.Count > 0 ? string.Join("; ", notes.Distinct()) : null);
    }

    private sealed record GpuFields(Dictionary<string, int?> Typed, int? Total, bool Invalid);
    private static GpuFields ParseTres(string? value)
    {
        var typed = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        int? total = null; bool invalid = false;
        foreach (var item in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = item.Split('=', 2);
            if (!pair[0].Equals("gres/gpu", StringComparison.OrdinalIgnoreCase) && !pair[0].StartsWith("gres/gpu:", StringComparison.OrdinalIgnoreCase)) continue;
            int? count = pair.Length == 2 && int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
            if (count is null) invalid = true;
            if (pair[0].Equals("gres/gpu", StringComparison.OrdinalIgnoreCase)) total = count;
            else typed[NormalizeModel(pair[0][9..])] = count;
        }
        return new(typed, total, invalid);
    }

    private static GpuFields ParseGres(string? value)
    {
        var typed = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        int? total = null; bool invalid = false;
        foreach (var item in Parentheses().Replace(value ?? "", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':');
            if (!parts[0].Equals("gpu", StringComparison.OrdinalIgnoreCase)) continue;
            if (parts.Length < 2) { invalid = true; continue; }
            int? count = int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
            if (count is null) invalid = true;
            if (parts.Length == 2) total = count;
            else
            {
                string model = NormalizeModel(string.Join(':', parts[1..^1]));
                // Multiple topology groups of the same type add; a generic total is still separate.
                typed[model] = typed.TryGetValue(model, out var previous) ? previous + count : count;
            }
        }
        return new(typed, total, invalid);
    }

    private static string NormalizeModel(string model) => model.Trim().ToUpperInvariant();
    private static IEnumerable<string> Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static Dictionary<string, string> ParseFields(string line)
    {
        var matches = FieldStart().Matches(line);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i]; int start = match.Index + match.Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : line.Length;
            fields[match.Groups[1].Value] = line[start..end].Trim();
        }
        return fields;
    }

    [GeneratedRegex(@"(?:^|\s)([A-Za-z][A-Za-z0-9_]*)=", RegexOptions.CultureInvariant)] private static partial Regex FieldStart();
    [GeneratedRegex(@"DOWN|DRAIN|MAINT|FAIL|FUTURE|POWER_DOWN|NO_RESPOND|INVAL|NOT_RESPONDING", RegexOptions.CultureInvariant)] private static partial Regex ExcludedState();
    [GeneratedRegex(@"\([^)]*\)", RegexOptions.CultureInvariant)] private static partial Regex Parentheses();
    [GeneratedRegex(@"(?:\bmps\b|\bshard\b|mig|\d+g\.\d+gb)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex SharedOrMig();
}
