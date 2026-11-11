using System.Text.Json;
using System.Text.Json.Serialization;
using AirePeek.Core;
using AirePeek.Ssh;

if (args.Length != 2) throw new ArgumentException("Usage: protocol-smoke <fixture-state.json> <result.json>");
var ports = ReadPorts(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(args[0])));
int Port(string name) => ports[name];
var target = new SshEndpoint("localhost", Port("target"), "fixture-user");
var gateway = new SshEndpoint("127.0.0.1", Port("gateway"), "fixture-user");
var passwordHost = new SshEndpoint("127.0.0.1", Port("password"), "fixture-user");
var secondCluster = new SshEndpoint("localhost", Port("password_then_duo"), "fixture-user");
var ui = new FixtureUi();
var keys = new FixtureKeys();
var results = new List<string>();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
await using (var session = new SshCommandSession())
{
    await session.ConnectAsync(new(target, gateway), ui, keys, timeout.Token);
    Require(session.IsConnected, "two-hop connection established");
    Require(ui.Challenges.Count == 6, "three arbitrary keyboard-interactive rounds per host");
    Require(ui.Challenges.Select(c => c.Endpoint).Distinct().Count() == 2, "both real endpoints shown");
    Require(ui.Trusted.Count == 2 && ui.Trusted[1].Endpoint == target, "tunnel target key pinned to real host/port");
    Require(ui.Challenges.Count(c => c.Prompts.Count == 0) == 2, "zero-prompt Duo information round acknowledged on both hops");
    foreach (var command in new[] { "squeue --me", "scontrol -o -d show nodes", "sacct -j 123", "squeue --me" })
        Require((await session.ExecuteAsync(command, timeout.Token)).Trim() == "mock:target", "command response via authenticated target");
    Require(ui.Challenges.Count == 6, "four commands caused no additional authentication prompts");
    await session.DisconnectAsync();
    try { await session.ExecuteAsync("squeue --me", timeout.Token); throw new Exception("Disconnected session executed query"); }
    catch (SessionDisconnectedException) { results.Add("disconnected query does not reconnect"); }
}
await using (var session = new SshCommandSession())
{
    var sharedProfile = new ConnectionProfile(target, gateway, ReuseGatewayPassword: true);
    var before = ui.Challenges.Count;
    await session.ConnectAsync(sharedProfile, ui, keys, timeout.Token);
    var challenges = ui.Challenges.Skip(before).ToArray();
    Require(session.IsConnected && challenges.Length == 5, "opt-in two-hop authentication asks for one password instead of two");
    Require(challenges.Count(c => c.Prompts.Any(p => p.Text == "Password:")) == 1 && challenges[0].Endpoint == gateway, "gateway password is entered once and target uses the transient copy");
    Require(challenges.Count(c => c.Prompts.Any(p => !p.IsSecret)) == 2 && challenges.Count(c => c.Prompts.Count == 0) == 2, "both hosts retain their own Duo choice and approval rounds");
    Require((await session.ExecuteAsync("squeue --me", timeout.Token)).Trim() == "mock:target", "shared-password connection supports authenticated queries");
    Require(ui.Challenges.Count == before + 5, "shared-password query does not authenticate again");
    before = ui.Challenges.Count;
    await session.ConnectAsync(sharedProfile, ui, keys, timeout.Token);
    Require(ui.Challenges.Count == before + 5 && ui.Challenges[before].Endpoint == gateway, "explicit reconnect requires a newly entered gateway password");
    Require((await session.ExecuteAsync("squeue --me", timeout.Token)).Trim() == "mock:target", "reconnected shared-password session supports queries");
}
await using (var session = new SshCommandSession())
{
    var before = ui.Challenges.Count;
    await session.ConnectAsync(new(passwordHost), ui, keys, timeout.Token);
    Require(ui.Challenges.Count == before + 1 && ui.Challenges[^1].Name == "password", "password-only server lazily requests one password");
    Require((await session.ExecuteAsync("squeue --me", timeout.Token)).Trim() == "mock:password", "password-only command works");
}
var vault = new FixtureVault(); // In-memory only; this fixture must never write the user's OS credential store.
await using (var calder = new SshCommandSession(new SshNetWireFactory(), vault))
await using (var aire = new SshCommandSession(new SshNetWireFactory(), vault))
{
    await calder.ConnectAsync(new(target, gateway, ReuseGatewayPassword: true, RememberPassword: true), ui, keys, timeout.Token);
    var before = ui.Challenges.Count;
    var aireProfile = new ConnectionProfile(secondCluster, gateway, ReuseGatewayPassword: true, RememberPassword: true);
    await aire.ConnectAsync(aireProfile, ui, keys, timeout.Token);
    Require(calder.IsConnected && aire.IsConnected, "Calder-first then Aire preserves both independently authenticated sessions");
    var challenges = ui.Challenges.Skip(before).ToArray();
    Require(challenges.Count(c => c.Endpoint == secondCluster) == 2 && challenges.Where(c => c.Endpoint == secondCluster).All(c => c.Name == "keyboard-interactive"), "password partial success proceeds to target Duo without replaying its password");
    Require(challenges.Count(c => c.Prompts.Any(p => !p.IsSecret)) == 2, "gateway and second cluster each receive their own Duo choice");
    Require((await calder.ExecuteAsync("squeue --me", timeout.Token)).Trim() == "mock:target", "first cluster remains usable after second-cluster authentication");
    Require((await aire.ExecuteAsync("scontrol -o -d show nodes", timeout.Token)).Trim() == "mock:password_then_duo", "second cluster executes queries after password-plus-Duo authentication");
    Require(ui.Challenges.Count == before + challenges.Length, "switching reads across both established clusters does not authenticate");
    await aire.DisconnectAsync();
    before = ui.Challenges.Count;
    await aire.ConnectAsync(aireProfile, ui, keys, timeout.Token);
    Require(calder.IsConnected && aire.IsConnected, "explicit second-cluster reconnect leaves the first cluster online");
    Require(ui.Challenges.Skip(before).All(c => c.Name == "keyboard-interactive" && c.Prompts.All(p => !p.IsSecret)) && ui.Challenges.Count == before + 4,
        "remembered endpoint passwords still allow separate gateway and target Duo without another password field");
    Require(vault.Values.Count == 3 && vault.Values.Values.All(value => value == "fake-password"), "only ordinary passwords are stored separately for gateway and two targets");
    Require((await aire.ExecuteAsync("squeue --me", timeout.Token)).Trim() == "mock:password_then_duo", "reconnected second cluster supports queries without reauthentication");
}
await using (var session = new SshCommandSession())
{
    var before = ui.Challenges.Count;
    keys.Save(target, "SHA256:deliberately-wrong-fixture-pin");
    try { await session.ConnectAsync(new(target), ui, keys, timeout.Token); throw new Exception("Wrong host pin accepted"); }
    catch (HostKeyMismatchException) { results.Add("changed real endpoint fingerprint blocks connection"); }
    Require(ui.Challenges.Count == before, "host key mismatch sends no credentials");
}
if (OperatingSystem.IsWindows())
{
    var endpoint = new SshEndpoint("hpcpeek-protocol-" + Guid.NewGuid().ToString("N") + ".invalid", 2222, "synthetic-only");
    var nativeVault = new OsPasswordStore();
    char[] secret = "synthetic-only-学员-🔒".ToCharArray();
    char[]? read = null;
    try
    {
        Require(nativeVault.Read(endpoint) is null, "isolated synthetic native vault record starts absent");
        nativeVault.Save(endpoint, secret); read = nativeVault.Read(endpoint);
        Require(read is not null && secret.SequenceEqual(read), "published SSH layer roundtrips a Unicode password through Windows Credential Manager");
        nativeVault.Delete(endpoint);
        Require(nativeVault.Read(endpoint) is null, "synthetic native vault record is deleted without touching existing credentials");
    }
    finally
    {
        nativeVault.Delete(endpoint);
        Array.Clear(secret); if (read is not null) Array.Clear(read);
    }
}
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new ProtocolResult(true, results,
    ui.Challenges.Count, ui.Trusted.Count, AppContext.BaseDirectory, typeof(SshCommandSession).Assembly.Location),
    ProtocolJsonContext.Default.ProtocolResult));

static Dictionary<string, int> ReadPorts(byte[] json)
{
    // Use serializer primitives that are present in the trimmed application runtime;
    // the test entry point must not introduce otherwise-unused reflection or JSON DOM APIs.
    var result = new Dictionary<string, int>(); var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
    while (reader.Read())
    {
        if (reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != "ports") continue;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Fixture ports are missing.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!; reader.Read(); result.Add(name, reader.GetInt32());
        }
        return result;
    }
    throw new InvalidDataException("Fixture ports are missing.");
}

void Require(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    results.Add(name);
}

sealed class FixtureUi : IAuthenticationUI
{
    public List<AuthenticationChallenge> Challenges { get; } = [];
    public List<HostKeyChallenge> Trusted { get; } = [];
    public Task<IReadOnlyList<string>?> PromptAsync(AuthenticationChallenge challenge, CancellationToken cancellationToken)
    {
        Challenges.Add(challenge);
        IReadOnlyList<string> responses = challenge.Prompts.Select(p => p.IsSecret ? "fake-password" : "7").ToArray();
        return Task.FromResult<IReadOnlyList<string>?>(responses);
    }
    public Task<bool> ConfirmHostKeyAsync(HostKeyChallenge challenge, CancellationToken cancellationToken)
    {
        Trusted.Add(challenge);
        return Task.FromResult(true);
    }
}

sealed class FixtureKeys : IHostKeyStore
{
    private readonly Dictionary<string, string> _keys = [];
    private static string Key(SshEndpoint endpoint) => $"{endpoint.Host.ToLowerInvariant()}:{endpoint.Port}";
    public string? Get(SshEndpoint endpoint) => _keys.GetValueOrDefault(Key(endpoint));
    public void Save(SshEndpoint endpoint, string sha256) => _keys[Key(endpoint)] = sha256;
}

sealed class FixtureVault : IPasswordStore
{
    public Dictionary<SshEndpoint, string> Values { get; } = [];
    public char[]? Read(SshEndpoint endpoint) => Values.GetValueOrDefault(endpoint)?.ToCharArray();
    public void Save(SshEndpoint endpoint, ReadOnlySpan<char> password) => Values[endpoint] = new string(password);
    public void Delete(SshEndpoint endpoint) => Values.Remove(endpoint);
}

internal sealed record ProtocolResult(bool Passed, List<string> Checks, int AuthenticationPrompts, int TrustedEndpoints, string RuntimeDirectory, string SshAssembly);
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ProtocolResult))]
internal partial class ProtocolJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
