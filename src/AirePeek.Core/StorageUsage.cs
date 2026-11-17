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
    // Query the documented filesystem roots, not a guessed per-user mount or directory project ID.
    public static string Query(string clusterId = ClusterCatalog.AireId)
    {
        bool project = ClusterCatalog.Get(clusterId).Id == ClusterCatalog.CalderId;
        var command = "LC_ALL=C; export LC_ALL; PATH=$PATH:/usr/sbin:/sbin; export PATH; " +
            (project ? "hp_scope=project; hp_root=/scratch-calder; hp_default=/scratch-calder/users;\n" : "hp_scope=user; hp_root=/scratch; hp_default=/mnt/scratch;\n") + """
            hp_user=$(id -un) || exit 1
            hp_home=${HOME:-/users/$hp_user}
            hp_scratch=${SCRATCH:-$hp_default/$hp_user}
            case "$hp_home" in /*) ;; *) hp_home=/users/$hp_user ;; esac
            case "$hp_scratch" in /*) ;; *) hp_scratch=$hp_default/$hp_user ;; esac
            printf 'HPCPEEK_HOME|%s|user\n' "$hp_home"
            quota -s 2>/dev/null
            hp_status=$?
            printf '\nHPCPEEK_HOME_STATUS|%s\nHPCPEEK_HOME_END\n' "$hp_status"
            if [ "$hp_scope" = project ]; then
                hp_project=$(id -u)
                case "$hp_project" in
                    ''|*[!0-9]*|0) printf 'HPCPEEK_SCRATCH|%s|project\n' "$hp_scratch"; hp_status=1 ;;
                    *) printf 'HPCPEEK_SCRATCH|%s|project %s\n' "$hp_scratch" "$hp_project"
                       lfs quota -h -p "$hp_project" "$hp_root" 2>/dev/null
                       hp_status=$? ;;
                esac
            else
                printf 'HPCPEEK_SCRATCH|%s|user\n' "$hp_scratch"
                lfs quota -h -u "$hp_user" "$hp_root" 2>/dev/null
                hp_status=$?
            fi
            printf '\nHPCPEEK_SCRATCH_STATUS|%s\nHPCPEEK_SCRATCH_END\n' "$hp_status"
            """;
        // A raw string compiled from a Windows checkout contains CRLF. Bash treats CR as
        // part of tokens (e.g. "then\r"), so normalize before transmitting the SSH command.
        return command.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
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
        // Only read the first three space columns. Inode counts and per-target rows must
        // never be added to the already aggregated filesystem total.
        string body = string.Join('\n', lines[(start + 1)..end]);
        var rows = QuotaRow().Matches(body).Cast<Match>().Where(candidate => !LustreTarget().IsMatch(candidate.Groups["fs"].Value)).ToArray();
        var matching = rows.Where(candidate => MatchesDirectory(candidate.Groups["fs"].Value, path, section)).ToArray();
        var row = matching.Length == 1 ? matching[0] : null;
        bool human = HumanHeader().IsMatch(body);
        if (row is not null && TryBytes(row.Groups["used"].Value, human, out long used) &&
            TryBytes(row.Groups["soft"].Value, human, out long soft) && TryBytes(row.Groups["hard"].Value, human, out long hard))
        {
            long limit = soft > 0 ? soft : hard;
            return new(path, used, limit > 0 ? limit : null, scope,
                limit == 0 ? "No finite space quota reported." : soft > 0 ? "Space quota (soft limit)." : "Space quota (hard limit).", sampledAt);
        }
        string? status = lines[(start + 1)..end].FirstOrDefault(line => line.StartsWith($"HPCPEEK_{section}_STATUS|", StringComparison.Ordinal))?.Split('|').Last();
        return new(path, null, null, scope, scope == "project"
            ? "Project quota unavailable; the account UID could not be determined."
            : status == "127" ? "Quota command is unavailable in the remote SSH environment."
            : status is not null and not "0" ? $"Quota query failed (exit {status}). Refresh to retry."
            : rows.Length > 1 ? "Quota response contains multiple filesystems; the account directory could not be matched uniquely."
            : "Quota output could not be read. The command may be unsupported or access may be restricted.");
    }

    private static bool MatchesDirectory(string filesystem, string path, string section)
    {
        var mount = filesystem.Contains(":/", StringComparison.Ordinal) ? filesystem[(filesystem.IndexOf(":/", StringComparison.Ordinal) + 1)..] : filesystem;
        mount = mount.TrimEnd('/');
        if (mount.Length > 0 && (path == mount || path.StartsWith(mount + "/", StringComparison.Ordinal))) return true;
        if (section == "SCRATCH") return mount is "/scratch" or "/mnt/scratch" or "/scratch-calder";
        var directoryRoot = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return directoryRoot is "users" or "home" && mount.EndsWith("/" + directoryRoot, StringComparison.Ordinal);
    }

    private static bool TryBytes(string value, bool human, out long bytes)
    {
        bytes = 0;
        var match = Size().Match(value.TrimEnd('*'));
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return false;
        string unit = match.Groups[2].Value.ToUpperInvariant();
        // Raw quota blocks/kbytes are KiB; human-readable output supplies a suffix.
        // A bare zero is safe in either format, but do not guess a nonzero human unit.
        if (human && unit.Length == 0 && number != 0) return false;
        int power = unit.Length == 0 ? 1 : unit == "B" ? 0 : "KMGTPE".IndexOf(unit[0]) + 1;
        decimal multiplier = 1;
        for (int i = 0; i < power; i++) multiplier *= 1024;
        if (number > long.MaxValue / multiplier) return false;
        bytes = (long)decimal.Round(number * multiplier, 0, MidpointRounding.AwayFromZero);
        return true;
    }

    [GeneratedRegex(@"(?m)^[ \t]*(?<fs>/\S+|[^\s:]+:/\S*)[ \t]*(?:\n[ \t]+)?[ \t]+(?<used>\d+(?:\.\d+)?(?:[KMGTPE](?:i?B)?|B)?\*?)[ \t]+(?<soft>\d+(?:\.\d+)?(?:[KMGTPE](?:i?B)?|B)?\*?)[ \t]+(?<hard>\d+(?:\.\d+)?(?:[KMGTPE](?:i?B)?|B)?\*?)(?:[ \t]|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QuotaRow();
    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)([KMGTPE](?:i?B)?|B)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Size();
    [GeneratedRegex(@"(?m)^[ \t]*Filesystem[ \t]+(?:space|used)[ \t]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HumanHeader();
    [GeneratedRegex(@"-(?:OST|MDT)[0-9a-f]+(?:_UUID)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LustreTarget();
    [GeneratedRegex(@"^project [1-9]\d*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectScope();
}
