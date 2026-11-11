using System.Text.Json.Serialization;

namespace AirePeek.Desktop.Infrastructure;

internal sealed record BenchmarkReady(bool Ready, double ManagedStartupMs, double Width, double Height, string Mode, string Language, string Runtime);
internal sealed record SmokeSuccess(bool Success, List<string> Checks, string Os, string Architecture, double NativeDisplayScale,
    string CompactDip, string ExpandedDip, bool RealAireAccountTested, bool RealCalderAccountTested, DateTimeOffset TimestampUtc);
internal sealed record SmokeFailure(bool Success, List<string> Checks, string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(BenchmarkReady))]
[JsonSerializable(typeof(SmokeSuccess))]
[JsonSerializable(typeof(SmokeFailure))]
internal partial class EvidenceJsonContext : JsonSerializerContext { }
