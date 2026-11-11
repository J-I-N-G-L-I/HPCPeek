using System.Text.Json.Serialization;
using AirePeek.Core;

namespace AirePeek.Desktop.Infrastructure;

// Explicit metadata keeps portable settings, migration and preference transfer independent of reflection trimming.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Preferences))]
[JsonSerializable(typeof(ConnectionProfile))]
[JsonSerializable(typeof(SettingsStore.TransferPreferences))]
internal partial class SettingsJsonContext : JsonSerializerContext { }
