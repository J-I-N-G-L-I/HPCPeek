using System.Globalization;
using System.Text.RegularExpressions;

namespace AirePeek.Core;

/// <summary>Space charged to a user/project quota, not the capacity of the shared filesystem.</summary>
public sealed record StorageUsage(string Path, long? UsedBytes, long? LimitBytes, string Scope = "user",
    string? Note = null, DateTimeOffset? SampledAt = null)
{
    public double? Fraction => UsedBytes is long used && LimitBytes is > 0 ? (double)used / LimitBytes.Value : null;
}

public sealed record StorageSnapshot(DateTimeOffset SampledAt, StorageUsage Home, StorageUsage Scratch);

public static class StorageCommands
{
    // All paths and account names are discovered on the authenticated host and shell-quoted.
    // df identifies the home mount only. Neither df capacity nor a recursive du is used as a quota.
    public static string Query(string clusterId = ClusterCatalog.AireId)
    {
        bool project = ClusterCatalog.Get(clusterId).Id == ClusterCatalog.CalderId;
        return "LC_ALL=C; export LC_ALL; " + (project ? "hp_scope=project; hp_default=/scratch-calder/users;\n" : "hp_scope=user; hp_default=/mnt/scratch;\n") + """
            hp_user=$(id -un) || exit 1
            hp_home=${HOME:-/users/$hp_user}
            hp_scratch=${SCRATCH:-$hp_default/$hp_user}
            case "$hp_home" in /*) ;; *) hp_home=/users/$hp_user ;; esac
            case "$hp_scratch" in /*) ;; *) hp_scratch=$hp_default/$hp_user ;; esac
            hp_home=$(cd -P "$hp_home" 2>/dev/null && pwd) || hp_home=${HOME:-/users/$hp_user}
            hp_scratch_real=$(cd -P "$hp_scratch" 2>/dev/null && pwd) && hp_scratch=$hp_scratch_real
            printf 'HPCPEEK_HOME|%s|user\n' "$hp_home"
            hp_mount=$(df -P "$hp_home" 2>/dev/null | awk 'NR==2 {print $6}')
            if [ -n "$hp_mount" ]; then quota -w -v -f "$hp_mount" 2>/dev/null; fi
            printf '\nHPCPEEK_HOME_END\n'
            if [ "$hp_scope" = project ]; then
                hp_project=$(lfs project -d "$hp_scratch" 2>/dev/null | awk 'NR==1 {print $1}')
                case "$hp_project" in
                    ''|*[!0-9]*|0) printf 'HPCPEEK_SCRATCH|%s|project\n' "$hp_scratch" ;;
                    *) printf 'HPCPEEK_SCRATCH|%s|project %s\n' "$hp_scratch" "$hp_project"
                       lfs quota -p "$hp_project" "$hp_scratch" 2>/dev/null ;;
                esac
            else
                printf 'HPCPEEK_SCRATCH|%s|user\n' "$hp_scratch"
                lfs quota -u "$hp_user" "$hp_scratch" 2>/dev/null
            fi
            printf '\nHPCPEEK_SCRATCH_END\n'
            """;
    }
}

public static partial class StorageParser
{
    public static StorageSnapshot Parse(string output, DateTimeOffset sampledAt)
    {
        var lines = output.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        return new(sampledAt, ReadSection(lines, "HOME", sampledAt), ReadSection(lines, "SCRATCH", sampledAt));
    }

    private static StorageUsage ReadSection(string[] lines, string section, DateTimeOffset sampledAt)
    {
        int start = Array.FindIndex(lines, line => line.StartsWith($"HPCPEEK_{section}|", StringComparison.Ordinal));
        int end = Array.FindIndex(lines, line => line == $"HPCPEEK_{section}_END");
        if (start < 0 || end <= start) throw new FormatException("Storage response is incomplete.");
        var header = lines[start].Split('|');
        if (header.Length != 3 || !header[1].StartsWith('/') ||
            !(header[2] == "user" || header[2] == "project" || ProjectScope().IsMatch(header[2])))
            throw new FormatException("Storage response has an invalid directory or quota scope.");
        string path = header[1], scope = header[2];
        // Both quota and lfs quota report KiB without their human-readable option. Allow a
        // wrapped filesystem name, but never parse inode counts or Lustre per-target rows.
        string body = string.Join('\n', lines[(start + 1)..end]);
        var rows = QuotaRow().Matches(body);
        if (rows.Count == 1 && TryBytes(rows[0].Groups[1].Value, out long used) &&
            TryBytes(rows[0].Groups[2].Value, out long soft) && TryBytes(rows[0].Groups[3].Value, out long hard))
        {
            long limit = soft > 0 ? soft : hard;
            return new(path, used, limit > 0 ? limit : null, scope,
                limit == 0 ? "No finite space quota reported." : soft > 0 ? "Space quota (soft limit)." : "Space quota (hard limit).", sampledAt);
        }
        return new(path, null, null, scope, scope == "project"
            ? "Project quota unavailable; the directory's project ID could not be determined."
            : "Quota unavailable. The quota command may be unsupported or access may be restricted.");
    }

    private static bool TryBytes(string value, out long bytes)
    {
        bytes = 0;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var kib) || kib > long.MaxValue / 1024) return false;
        bytes = kib * 1024;
        return true;
    }

    [GeneratedRegex(@"(?m)^\s*(?:/\S+|[^\s:]+:/\S*)[ \t]*(?:\n[ \t]+)?[ \t]+(\d+)\*?[ \t]+(\d+)\*?[ \t]+(\d+)\*?(?:[ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex QuotaRow();
    [GeneratedRegex(@"^project [1-9]\d*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectScope();
}
