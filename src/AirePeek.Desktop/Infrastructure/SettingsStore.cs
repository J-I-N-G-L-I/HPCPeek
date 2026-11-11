using System.Text.Json;
using AirePeek.Core;

namespace AirePeek.Desktop.Infrastructure;

public sealed class Preferences
{
    public Dictionary<string, ClusterPreferences> Clusters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string SelectedClusterId { get; set; } = ClusterCatalog.AireId;
    // Keep the old JSON fields and caller API as aliases to Aire, never to whichever cluster is selected.
    public ConnectionProfile? Connection { get => GetCluster(ClusterCatalog.AireId).Connection; set => GetCluster(ClusterCatalog.AireId).Connection = value; }
    public Dictionary<string, string> HostKeys { get; set; } = new(StringComparer.Ordinal);
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool IsPinned { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public string GpuModel { get => GetCluster(ClusterCatalog.AireId).GpuModel; set => GetCluster(ClusterCatalog.AireId).GpuModel = value; }
    public int GpusPerNode { get; set; } = 2;
    public int RefreshIntervalSeconds { get; set; } = 300;
    public string Theme { get; set; } = "system";
    public string Language { get; set; } = "en";
    public ClusterPreferences GetCluster(string id)
    {
        var cluster = ClusterCatalog.Get(id);
        Clusters ??= new(StringComparer.OrdinalIgnoreCase);
        if (!Clusters.TryGetValue(cluster.Id, out var value) || value is null)
            Clusters[cluster.Id] = value = new() { GpuModel = cluster.DefaultGpuModel };
        return value;
    }
}

public sealed class ClusterPreferences
{
    public ConnectionProfile? Connection { get; set; }
    public string GpuModel { get; set; } = "";
}

/// <summary>Only explicit, non-secret fields are persisted. The macOS app bundle is never modified.</summary>
public sealed class SettingsStore : IHostKeyStore
{
    private readonly object sync = new();
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public Preferences Value { get; }

    public SettingsStore(string? directory = null) : this(
        directory ?? (OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "HPCPeek")
            : Path.Combine(AppContext.BaseDirectory, "portable-data")),
        directory is null && OperatingSystem.IsMacOS()
            ? new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "AirePeek"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "AIRE Peek")
            } : Array.Empty<string>())
    { }

    internal SettingsStore(string directory, string? legacyDirectory) : this(directory, legacyDirectory is null ? Array.Empty<string>() : new[] { legacyDirectory }) { }

    internal SettingsStore(string directory, IReadOnlyList<string> legacyDirectories)
    {
        DirectoryPath = directory;
        var readPath = File.Exists(FilePath) ? FilePath : legacyDirectories.Select(path => Path.Combine(path, "settings.json")).FirstOrDefault(File.Exists);
        if (readPath is not null)
        {
            var json = File.ReadAllText(readPath);
            Value = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.Preferences) ?? throw new InvalidDataException("The settings file is empty.");
            using var parsed = JsonDocument.Parse(json);
            // JSON property order must not make a legacy Aire profile disappear when a Calder-only map follows it.
            JsonElement aire = default;
            if (parsed.RootElement.TryGetProperty("Clusters", out var clusters) && clusters.ValueKind == JsonValueKind.Object)
                aire = clusters.EnumerateObject().FirstOrDefault(p => p.Name.Equals(ClusterCatalog.AireId, StringComparison.OrdinalIgnoreCase)).Value;
            if ((aire.ValueKind != JsonValueKind.Object || !aire.TryGetProperty("Connection", out _)) && parsed.RootElement.TryGetProperty("Connection", out var oldConnection))
                Value.Connection = oldConnection.Deserialize(SettingsJsonContext.Default.ConnectionProfile);
            if ((aire.ValueKind != JsonValueKind.Object || !aire.TryGetProperty("GpuModel", out _)) && parsed.RootElement.TryGetProperty("GpuModel", out var oldModel) && oldModel.ValueKind == JsonValueKind.String)
                Value.GpuModel = oldModel.GetString()!;
            if (!parsed.RootElement.TryGetProperty("IsPinned", out _) && parsed.RootElement.TryGetProperty("AlwaysOnTop", out var legacyPin) && legacyPin.ValueKind is JsonValueKind.True or JsonValueKind.False)
                Value.IsPinned = legacyPin.GetBoolean();
        }
        else Value = new();
        Normalize(Value);
        if (readPath is not null && readPath != FilePath) Save();
    }

    private static void Normalize(Preferences value)
    {
        value.HostKeys ??= new(StringComparer.Ordinal);
        value.Clusters = new(value.Clusters ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var cluster in ClusterCatalog.All)
        {
            var preferences = value.GetCluster(cluster.Id);
            preferences.GpuModel = string.IsNullOrWhiteSpace(preferences.GpuModel) ? cluster.DefaultGpuModel : preferences.GpuModel.Trim();
        }
        value.SelectedClusterId = ClusterCatalog.All.FirstOrDefault(c => string.Equals(c.Id, value.SelectedClusterId, StringComparison.OrdinalIgnoreCase))?.Id ?? ClusterCatalog.AireId;
        value.GpusPerNode = Math.Clamp(value.GpusPerNode, 1, 64);
        if (value.RefreshIntervalSeconds is < 15 or > 86400) value.RefreshIntervalSeconds = 300;
        value.GpuModel = string.IsNullOrWhiteSpace(value.GpuModel) ? "L40S" : value.GpuModel.Trim();
        if (value.Theme is not ("system" or "light" or "dark")) value.Theme = "system";
        value.Language = "en";
        if (value.Left.HasValue && !double.IsFinite(value.Left.Value)) value.Left = null;
        if (value.Top.HasValue && !double.IsFinite(value.Top.Value)) value.Top = null;
    }

    public ClusterPreferences GetCluster(string id) { lock (sync) return Value.GetCluster(id); }

    public void Save()
    {
        lock (sync)
        {
            Normalize(Value);
            Directory.CreateDirectory(DirectoryPath);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Value, SettingsJsonContext.Default.Preferences));
            File.Move(temporary, FilePath, true);
        }
    }

    private static string HostKey(SshEndpoint endpoint) => $"{endpoint.Host.Trim().ToLowerInvariant()}:{endpoint.Port}";
    public string? Get(SshEndpoint endpoint) { lock (sync) return Value.HostKeys.GetValueOrDefault(HostKey(endpoint)); }
    public void Save(SshEndpoint endpoint, string sha256)
    {
        lock (sync)
        {
            var key = HostKey(endpoint);
            if (Value.HostKeys.TryGetValue(key, out var known) && !string.Equals(known, sha256, StringComparison.Ordinal))
                throw new HostKeyMismatchException("The host fingerprint changed. Connection stopped.");
            Value.HostKeys[key] = sha256;
            Save();
        }
    }

    // The transfer schema deliberately excludes credentials, host pins, account names, endpoints and window coordinates.
    public async Task ExportAsync(Stream output, CancellationToken cancellationToken = default)
    {
        var transfer = new TransferPreferences(1, Value.IsPinned, Value.Notifications, Value.GpuModel, Value.GpusPerNode, Value.Theme, "en", Value.RefreshIntervalSeconds,
            Value.SelectedClusterId, ClusterCatalog.All.ToDictionary(c => c.Id, c => GetCluster(c.Id).GpuModel));
        await JsonSerializer.SerializeAsync(output, transfer, SettingsJsonContext.Default.TransferPreferences, cancellationToken);
    }
    public async Task ImportAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var transfer = await JsonSerializer.DeserializeAsync(input, SettingsJsonContext.Default.TransferPreferences, cancellationToken)
            ?? throw new InvalidDataException("This file does not contain HPCPeek preferences.");
        if (transfer.Format != 1 || transfer.Theme is not ("system" or "light" or "dark") || transfer.GpusPerNode is < 1 or > 64 || string.IsNullOrWhiteSpace(transfer.GpuModel) || transfer.GpuModel.Length > 100 || transfer.RefreshIntervalSeconds is < 15 or > 86400)
            throw new InvalidDataException("Unsupported or invalid preference file.");
        if (transfer.SelectedClusterId is not null && !ClusterCatalog.All.Any(c => c.Id == transfer.SelectedClusterId) ||
            transfer.ClusterGpuModels?.Any(item => !ClusterCatalog.All.Any(c => c.Id == item.Key) || string.IsNullOrWhiteSpace(item.Value) || item.Value.Length > 100) == true)
            throw new InvalidDataException("Unsupported or invalid cluster preferences.");
        lock (sync)
        {
            Value.IsPinned = transfer.IsPinned;
            Value.Notifications = transfer.Notifications;
            Value.GpuModel = transfer.GpuModel;
            Value.GpusPerNode = transfer.GpusPerNode;
            Value.RefreshIntervalSeconds = transfer.RefreshIntervalSeconds ?? 300;
            Value.Theme = transfer.Theme;
            Value.Language = "en";
            if (transfer.SelectedClusterId is not null) Value.SelectedClusterId = transfer.SelectedClusterId;
            if (transfer.ClusterGpuModels is not null)
                foreach (var (id, model) in transfer.ClusterGpuModels) GetCluster(id).GpuModel = model;
            Save();
        }
    }
    public async Task ExportAsync(string path, CancellationToken cancellationToken = default)
    { await using var stream = File.Create(path); await ExportAsync(stream, cancellationToken); }
    public async Task ImportAsync(string path, CancellationToken cancellationToken = default)
    { await using var stream = File.OpenRead(path); await ImportAsync(stream, cancellationToken); }
    internal sealed record TransferPreferences(int Format, bool IsPinned, bool Notifications, string GpuModel, int GpusPerNode, string Theme, string Language, int? RefreshIntervalSeconds = null,
        string? SelectedClusterId = null, Dictionary<string, string>? ClusterGpuModels = null);
}
