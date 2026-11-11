using System.Text.RegularExpressions;

namespace AirePeek.Core;

public static partial class SlurmCommands
{
    // --array keeps task identity stable as other elements leave a pending array.
    public const string Queue = "LC_ALL=C squeue --me --array --noheader --format='%i|%j|%T|%M|%l|%N|%r'";
    public const string Nodes = "LC_ALL=C scontrol -o -d show nodes";

    public static string Accounting(IEnumerable<string> jobIds)
    {
        var ids = jobIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0 || ids.Any(id => !SafeId().IsMatch(id))) throw new ArgumentException("Only numeric Slurm job / array IDs are accepted.", nameof(jobIds));
        return "LC_ALL=C sacct --noheader --parsable2 --allocations --array --jobs='" + string.Join(',', ids) + "' --format=JobID%128,State%64,ExitCode";
    }

    [GeneratedRegex(@"^\d+(?:_(?:\d+|\[[\d,:%-]+\]))?(?:\+\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}
