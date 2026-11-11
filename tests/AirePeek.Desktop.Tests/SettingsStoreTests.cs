using System.Text;
using System.Text.Json;
using AirePeek.Core;
using AirePeek.Desktop.Infrastructure;

namespace AirePeek.Desktop.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AirePeekSettingsTests", Guid.NewGuid().ToString("N"));
    private static readonly SshEndpoint Target = new("aire.example.test", 2222, "private-account");

    [Fact]
    public void PortablePreferencesRoundTripAndEnglishIsDefault()
    {
        var store = new SettingsStore(directory);
        Assert.Equal("en", store.Value.Language);
        Assert.Equal(300, store.Value.RefreshIntervalSeconds);
        store.Value.Connection = new(Target, new("gateway.example.test", 22, "gateway-user"));
        store.Value.GpuModel = "H100";
        store.Value.GpusPerNode = 4;
        store.Value.RefreshIntervalSeconds = 900;
        store.Value.Theme = "dark";
        store.Value.IsPinned = false;
        store.Value.Left = 120;
        store.Value.Top = 75;
        store.Save(Target, "SHA256:test-pin");

        var loaded = new SettingsStore(directory);
        Assert.Equal(store.Value.Connection, loaded.Value.Connection);
        Assert.Equal("H100", loaded.Value.GpuModel);
        Assert.Equal(4, loaded.Value.GpusPerNode);
        Assert.Equal(900, loaded.Value.RefreshIntervalSeconds);
        Assert.Equal("dark", loaded.Value.Theme);
        Assert.False(loaded.Value.IsPinned);
        Assert.Equal(120, loaded.Value.Left);
        Assert.Equal(75, loaded.Value.Top);
        Assert.Equal("SHA256:test-pin", loaded.Get(Target));
    }

    [Fact]
    public void LegacyPinSettingMigratesAndOldLanguageDoesNotOverrideEnglishDefault()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), """
            {"AlwaysOnTop":false,"Language":"zh-CN","GpuModel":" L40S ","GpusPerNode":2}
            """);
        var store = new SettingsStore(directory);
        Assert.False(store.Value.IsPinned);
        Assert.Equal("en", store.Value.Language);
        Assert.Equal("L40S", store.Value.GpuModel);
        Assert.Equal(300, store.Value.RefreshIntervalSeconds);
    }

    [Fact]
    public void AChangedKeyCannotReplaceTrustInMemoryOrOnDisk()
    {
        var store = new SettingsStore(directory);
        store.Save(Target, "SHA256:original");
        Assert.Throws<HostKeyMismatchException>(() => store.Save(Target, "SHA256:changed"));
        Assert.Equal("SHA256:original", store.Get(Target));
        Assert.Equal("SHA256:original", new SettingsStore(directory).Get(Target));
    }

    [Fact]
    public void TrustUsesCaseNormalizedHostAndRealPort()
    {
        var store = new SettingsStore(directory);
        store.Save(Target, "SHA256:target");
        Assert.Equal("SHA256:target", store.Get(Target with { Host = " AIRE.EXAMPLE.TEST " }));
        Assert.Null(store.Get(Target with { Port = 22 }));
        Assert.Null(store.Get(Target with { Host = "127.0.0.1" }));
    }

    [Fact]
    public async Task ExportOmitsEndpointsUsersPinsCoordinatesAndCredentialLikeData()
    {
        var store = new SettingsStore(directory);
        store.Value.Connection = new(Target);
        store.Value.Left = 150;
        store.Value.Top = 200;
        store.Value.RefreshIntervalSeconds = 45;
        store.Save(Target, "SHA256:private-pin");
        using var output = new MemoryStream();
        await store.ExportAsync(output);
        var json = Encoding.UTF8.GetString(output.ToArray());
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("Format").GetInt32());
        Assert.Equal(45, parsed.RootElement.GetProperty("RefreshIntervalSeconds").GetInt32());
        foreach (var forbidden in new[] { "Connection", "HostKeys", "Left", "Top", "Password", "Duo", "Token" })
            Assert.False(parsed.RootElement.TryGetProperty(forbidden, out _));
        Assert.DoesNotContain(Target.Host, json);
        Assert.DoesNotContain(Target.Username, json);
        Assert.DoesNotContain("SHA256:private-pin", json);
    }

    [Fact]
    public async Task ImportChangesAppearanceButCannotImportConnectionOrHostTrust()
    {
        var store = new SettingsStore(directory);
        store.Value.Connection = new(Target);
        store.Save(Target, "SHA256:original");
        using var input = Stream("""
            {"Format":1,"IsPinned":false,"Notifications":false,"GpuModel":"H100","GpusPerNode":4,"Theme":"dark","Language":"zh-CN",
             "Connection":{"Target":{"Host":"attacker.test","Port":22,"Username":"attacker"}},
             "HostKeys":{"aire.example.test:2222":"SHA256:replacement"},"Password":"not-importable"}
            """);
        await store.ImportAsync(input);
        var loaded = new SettingsStore(directory);
        Assert.Equal(new ConnectionProfile(Target), loaded.Value.Connection);
        Assert.Equal("SHA256:original", loaded.Get(Target));
        Assert.Equal("H100", loaded.Value.GpuModel);
        Assert.Equal(4, loaded.Value.GpusPerNode);
        Assert.Equal("dark", loaded.Value.Theme);
        Assert.Equal("en", loaded.Value.Language);
        Assert.False(loaded.Value.IsPinned);
        Assert.False(loaded.Value.Notifications);
        Assert.Equal(300, loaded.Value.RefreshIntervalSeconds); // v0.3.0 export has no interval.
        Assert.DoesNotContain("not-importable", File.ReadAllText(loaded.FilePath));
    }

    [Theory]
    [InlineData("null", 2)]
    [InlineData("\"dark\"", -1)]
    [InlineData("\"light\"", 65)]
    public async Task InvalidImportLeavesExistingPreferencesAndTrustUntouched(string theme, int gpusPerNode)
    {
        var store = new SettingsStore(directory);
        store.Save(Target, "SHA256:original");
        var original = File.ReadAllText(store.FilePath);
        using var input = Stream($$"""
            {"Format":1,"IsPinned":false,"Notifications":false,"GpuModel":"H100","GpusPerNode":{{gpusPerNode}},"Theme":{{theme}},"Language":"en"}
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(input));
        Assert.Equal("L40S", store.Value.GpuModel);
        Assert.True(store.Value.IsPinned);
        Assert.Equal("SHA256:original", store.Get(Target));
        Assert.Equal(original, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void CorruptSettingsAreNotSilentlyOverwrittenOrTrusted()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{not valid JSON");
        Assert.Throws<JsonException>(() => new SettingsStore(directory));
        Assert.Equal("{not valid JSON", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(900)]
    [InlineData(86400)]
    public async Task RefreshIntervalRoundTripsThroughSafeTransfer(int seconds)
    {
        var source = new SettingsStore(directory);
        source.Value.RefreshIntervalSeconds = seconds;
        using var transfer = new MemoryStream(); await source.ExportAsync(transfer); transfer.Position = 0;
        var target = new SettingsStore(Path.Combine(directory, "imported"));
        await target.ImportAsync(transfer);
        Assert.Equal(seconds, new SettingsStore(Path.Combine(directory, "imported")).Value.RefreshIntervalSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(86401)]
    public async Task InvalidImportedIntervalCannotPartiallyApplyPreferences(int seconds)
    {
        var store = new SettingsStore(directory); store.Save(Target, "SHA256:original");
        var original = File.ReadAllText(store.FilePath);
        using var input = Stream($$"""
            {"Format":1,"IsPinned":false,"Notifications":false,"GpuModel":"H100","GpusPerNode":4,"Theme":"dark","Language":"en","RefreshIntervalSeconds":{{seconds}}}
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(input));
        Assert.Equal(300, store.Value.RefreshIntervalSeconds); Assert.True(store.Value.IsPinned);
        Assert.Equal(original, File.ReadAllText(store.FilePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86401)]
    public void InvalidStoredIntervalFallsBackToFiveMinutes(int seconds)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), $$"""{"RefreshIntervalSeconds":{{seconds}}}""");
        Assert.Equal(300, new SettingsStore(directory).Value.RefreshIntervalSeconds);
    }

    [Fact]
    public void LegacyFolderMigrationPreservesTrustAndDoesNotOverwriteNewSettings()
    {
        var legacyDirectory = Path.Combine(directory, "legacy");
        var newDirectory = Path.Combine(directory, "AirePeek");
        var legacy = new SettingsStore(legacyDirectory);
        legacy.Value.Connection = new(Target); legacy.Value.IsPinned = false;
        legacy.Save(Target, "SHA256:original");
        var original = File.ReadAllText(legacy.FilePath);

        var migrated = new SettingsStore(newDirectory, legacyDirectory);
        Assert.Equal(new ConnectionProfile(Target), migrated.Value.Connection);
        Assert.Equal("SHA256:original", migrated.Get(Target)); Assert.False(migrated.Value.IsPinned);
        Assert.Equal(300, migrated.Value.RefreshIntervalSeconds);
        Assert.True(File.Exists(migrated.FilePath)); Assert.Equal(original, File.ReadAllText(legacy.FilePath));

        migrated.Value.RefreshIntervalSeconds = 1800; migrated.Save();
        Assert.Equal(1800, new SettingsStore(newDirectory, legacyDirectory).Value.RefreshIntervalSeconds);
        File.WriteAllText(migrated.FilePath, "{corrupt");
        Assert.Throws<JsonException>(() => new SettingsStore(newDirectory, legacyDirectory));
        Assert.Equal("{corrupt", File.ReadAllText(migrated.FilePath));
    }

    [Fact]
    public void IndependentClusterProfilesAndModelsRoundTripWithLegacyAliasesPointingToAire()
    {
        var store = new SettingsStore(directory);
        var calder = new ConnectionProfile(Target with { Host = "calder.example.test" });
        store.Value.Connection = new(Target);
        store.GetCluster("calder").Connection = calder;
        store.GetCluster("calder").GpuModel = "H200NVL";
        store.GetCluster("aire").GpuModel = "L40S";
        store.Value.SelectedClusterId = "calder";
        store.Save(Target, "SHA256:aire-pin");
        store.Save(calder.Target, "SHA256:calder-pin");
        var loaded = new SettingsStore(directory);
        Assert.Equal("calder", loaded.Value.SelectedClusterId);
        Assert.Equal(new ConnectionProfile(Target), loaded.Value.Connection);
        Assert.Equal(calder, loaded.GetCluster("calder").Connection);
        Assert.Equal("L40S", loaded.Value.GpuModel);
        Assert.Equal("H200NVL", loaded.GetCluster("calder").GpuModel);
        Assert.Equal("SHA256:aire-pin", loaded.Get(Target));
        Assert.Equal("SHA256:calder-pin", loaded.Get(calder.Target));
    }

    [Fact]
    public void LegacyConnectionBeforeCalderOnlyMapIsMigratedWithoutDependingOnJsonPropertyOrder()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), """
            {"Connection":{"Target":{"Host":"aire.example.test","Port":2222,"Username":"private-account"}},
             "GpuModel":"L40S","Clusters":{"calder":{"GpuModel":"H200NVL"}},"SelectedClusterId":"unknown"}
            """);
        var store = new SettingsStore(directory);
        Assert.Equal(new ConnectionProfile(Target), store.GetCluster("aire").Connection);
        Assert.Null(store.GetCluster("calder").Connection);
        Assert.Equal("H200NVL", store.GetCluster("calder").GpuModel);
        Assert.Equal("aire", store.Value.SelectedClusterId);
    }

    [Fact]
    public void HpcPeekMigrationPrefersAirePeekThenSpacedLegacyFolderAndRetainsOriginalFiles()
    {
        var first = Path.Combine(directory, "AirePeek");
        var second = Path.Combine(directory, "AIRE Peek");
        var destination = Path.Combine(directory, "HPCPeek");
        var old = new SettingsStore(first); old.Value.Connection = new(Target); old.Save(Target, "SHA256:first");
        var older = new SettingsStore(second); older.Value.Theme = "dark"; older.Save(Target, "SHA256:second");
        var firstOriginal = File.ReadAllText(old.FilePath); var secondOriginal = File.ReadAllText(older.FilePath);
        var migrated = new SettingsStore(destination, new[] { first, second });
        Assert.Equal("SHA256:first", migrated.Get(Target));
        Assert.Equal(new ConnectionProfile(Target), migrated.GetCluster("aire").Connection);
        Assert.Null(migrated.GetCluster("calder").Connection);
        Assert.Equal(firstOriginal, File.ReadAllText(old.FilePath)); Assert.Equal(secondOriginal, File.ReadAllText(older.FilePath));

        var fallback = new SettingsStore(Path.Combine(directory, "fallback"), new[] { Path.Combine(directory, "missing"), second });
        Assert.Equal("SHA256:second", fallback.Get(Target));
        File.WriteAllText(migrated.FilePath, "{corrupt");
        Assert.Throws<JsonException>(() => new SettingsStore(destination, new[] { first, second }));
        Assert.Equal("{corrupt", File.ReadAllText(migrated.FilePath));
    }

    [Fact]
    public async Task ClusterTransferPreservesModelsButCannotTransferEitherConnectionOrPins()
    {
        var source = new SettingsStore(directory); source.Value.Connection = new(Target);
        var calder = new ConnectionProfile(Target with { Host = "calder-private.example.test", Username = "calder-private-user" });
        source.GetCluster("calder").Connection = calder;
        source.GetCluster("aire").GpuModel = "L40S"; source.GetCluster("calder").GpuModel = "H200NVL";
        source.Value.SelectedClusterId = "calder"; source.Save(calder.Target, "SHA256:calder-secret-pin");
        using var transfer = new MemoryStream(); await source.ExportAsync(transfer);
        string exported = Encoding.UTF8.GetString(transfer.ToArray());
        Assert.DoesNotContain(Target.Host, exported); Assert.DoesNotContain(calder.Target.Host, exported);
        Assert.DoesNotContain(calder.Target.Username, exported); Assert.DoesNotContain("SHA256:", exported);
        var destination = new SettingsStore(Path.Combine(directory, "destination"));
        transfer.Position = 0; await destination.ImportAsync(transfer);
        Assert.Equal("calder", destination.Value.SelectedClusterId);
        Assert.Equal("H200NVL", destination.GetCluster("calder").GpuModel);
        Assert.Null(destination.GetCluster("aire").Connection); Assert.Null(destination.GetCluster("calder").Connection);
        Assert.Empty(destination.Value.HostKeys);
    }

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    public void Dispose()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AirePeekSettingsTests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }
}
