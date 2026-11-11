namespace AirePeek.Core;

public enum DemoScenario { Normal, Empty, Partial, Disconnected, Aire28, Calder7 }

/// <summary>Raw Slurm fixtures exercise the exact same parsers and monitor as a real SSH session.</summary>
public sealed class DemoSession : ICommandSession
{
    private int _step;
    private DemoScenario scenario;
    private DemoScenario lastConnectedScenario;
    public string ClusterId { get; }
    public DemoSession(string clusterId = ClusterCatalog.AireId)
    {
        ClusterId = ClusterCatalog.Get(clusterId).Id;
        Scenario = ClusterId == ClusterCatalog.CalderId ? DemoScenario.Calder7 : DemoScenario.Normal;
    }
    public DemoScenario Scenario
    {
        get => scenario;
        set { scenario = value; if (value != DemoScenario.Disconnected) lastConnectedScenario = value; }
    }
    public bool IsConnected { get; private set; }
    public int ConnectionCount { get; private set; }

    public Task ConnectAsync(ConnectionProfile profile, IAuthenticationUI authenticationUI, IHostKeyStore hostKeys, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnected = true;
        ConnectionCount++;
        if (Scenario == DemoScenario.Disconnected) Scenario = lastConnectedScenario;
        return Task.CompletedTask;
    }

    public void Advance() => _step = (_step + 1) % 3;

    public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected || Scenario == DemoScenario.Disconnected)
        { IsConnected = false; throw new SessionDisconnectedException(); }
        if (command == SlurmCommands.Queue)
        {
            if (Scenario == DemoScenario.Empty) return Task.FromResult("");
            bool calder = ClusterId == ClusterCatalog.CalderId || Scenario == DemoScenario.Calder7;
            string firstNode = Scenario == DemoScenario.Aire28 || calder ? "gpu001" : "aire-gpu-a01";
            string secondNode = Scenario == DemoScenario.Aire28 || calder ? "gpu002" : "aire-gpu-a02";
            string nextNode = calder ? "gpu004" : Scenario == DemoScenario.Aire28 ? "gpu020" : "aire-gpu-a03";
            string experiment = calder ? "H200 vision foundation model finetuning" : "Vision foundation model multi-GPU finetuning";
            // Deliberately overlapping job IDs and node names exercise cluster-qualified UI identity.
            var queue = $"482103|{(calder ? "H200 large model pretraining" : "Vision Transformer large-scale pretraining")}|RUNNING|06:24:18|1-00:00:00|{firstNode}|None\n482087_12|High-resolution semantic segmentation ablation|RUNNING|02:11:43|12:00:00|{secondNode}|None";
            if (_step == 0) queue += $"\n482119|{experiment}|PENDING|0:00|08:00:00|(null)|ReqNodeNotAvail, Reserved for maintenance";
            else if (_step == 1) queue += $"\n482119|{experiment}|RUNNING|0:01|08:00:00|{nextNode}|None";
            return Task.FromResult(queue);
        }
        if (command == SlurmCommands.Nodes) return Task.FromResult(Scenario switch
        {
            DemoScenario.Partial when ClusterId == ClusterCatalog.CalderId => CalderPartialNodes,
            _ when ClusterId == ClusterCatalog.CalderId => Calder7Nodes,
            DemoScenario.Partial => PartialNodes,
            DemoScenario.Aire28 => Aire28Nodes,
            DemoScenario.Calder7 => Calder7Nodes,
            _ => NormalNodes
        });
        if (command.StartsWith("LC_ALL=C sacct ", StringComparison.Ordinal)) return Task.FromResult(_step == 2 ? "482119|COMPLETED|0:0" : "");
        throw new RemoteCommandException("Demo only supports read-only Slurm queries.");
    }

    public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }

    /// <summary>Simulated occupancy on the published seven-by-eight H200 NVL configuration. Not live readings.</summary>
    public static string Calder7Nodes { get; } = BuildCalderNodes(false);
    public static string CalderPartialNodes { get; } = BuildCalderNodes(true);
    private static string BuildCalderNodes(bool partial)
    {
        var rows = Enumerable.Range(1, 7).Select(number =>
        {
            int allocated = number switch { 1 or 5 => 8, 2 => 6, 3 => 4, 6 => 2, _ => 0 };
            string state = number == 7 ? "IDLE+MAINT" : allocated == 8 ? "ALLOCATED" : allocated == 0 ? "IDLE" : "MIXED";
            string allocation = partial && number == 2 ? "" : $" AllocTRES=cpu=32,gres/gpu={allocated},gres/gpu:h200nvl={allocated}";
            return $"NodeName=gpu{number:000} State={state} Partitions=gpu Gres=gpu:h200nvl:8 CfgTRES=cpu=128,gres/gpu=8,gres/gpu:h200nvl=8{allocation}";
        }).ToArray();
        return string.Join('\n', rows.Append(rows[2].Replace("Partitions=gpu", "Partitions=short", StringComparison.Ordinal)));
    }

    /// <summary>Offline reproduction of the user's 28-node snapshot; these are not live cluster readings.</summary>
    public static string Aire28Nodes { get; } = string.Join('\n', Enumerable.Range(1, 28).Select(number =>
    {
        string state = number switch
        {
            21 or 28 => "down$", 23 => "drained",
            4 or 5 or 8 or 9 or 10 or 12 or 15 or 16 or 17 or 22 or 25 => "allocated",
            _ => "mixed"
        };
        int allocated = number is 21 or 23 or 28 ? 0 : number == 20 ? 1 : 3;
        string indices = allocated switch { 0 => "N/A", 1 => "2", _ => "0-2" };
        return $"NodeName=gpu{number:000} State={state} Partitions=gpu Gres=gpu:l40s:3 GresUsed=gpu:l40s:{allocated}(IDX:{indices})";
    }));

    public const string NormalNodes = """
        NodeName=aire-gpu-a01 State=MIXED Partitions=gpu,short Gres=gpu:l40s:4 CfgTRES=cpu=64,mem=500G,gres/gpu=4,gres/gpu:l40s=4 AllocTRES=cpu=32,gres/gpu=2,gres/gpu:l40s=2
        NodeName=aire-gpu-a02 State=MIXED Partitions=gpu Gres=gpu:l40s:4 CfgTRES=cpu=64,gres/gpu=4,gres/gpu:l40s=4 AllocTRES=cpu=32,gres/gpu=2,gres/gpu:l40s=2
        NodeName=aire-gpu-a03 State=MIXED Partitions=gpu Gres=gpu:l40s:4 CfgTRES=cpu=64,gres/gpu=4,gres/gpu:l40s=4 AllocTRES=cpu=48,gres/gpu=3,gres/gpu:l40s=3
        NodeName=aire-gpu-a01 State=MIXED Partitions=short Gres=gpu:l40s:4 CfgTRES=cpu=64,mem=500G,gres/gpu=4,gres/gpu:l40s=4 AllocTRES=cpu=32,gres/gpu=2,gres/gpu:l40s=2
        NodeName=aire-gpu-a04 State=IDLE+DRAIN Partitions=gpu Gres=gpu:l40s:4 CfgTRES=cpu=64,gres/gpu=4,gres/gpu:l40s=4 AllocTRES=
        NodeName=aire-gpu-h01 State=MIXED Partitions=gpu Gres=gpu:h100:4 CfgTRES=cpu=64,gres/gpu=4,gres/gpu:h100=4 AllocTRES=gres/gpu=2,gres/gpu:h100=2
        """;

    public const string PartialNodes = """
        NodeName=aire-gpu-a01 State=MIXED CfgTRES=gres/gpu=4,gres/gpu:l40s=4 AllocTRES=gres/gpu=2,gres/gpu:l40s=2
        NodeName=aire-gpu-a02 State=MIXED CfgTRES=gres/gpu=4,gres/gpu:l40s=4
        NodeName=aire-gpu-a03 State=DOWN CfgTRES=gres/gpu=4,gres/gpu:l40s=4 AllocTRES=
        NodeName=aire-gpu-m01 State=MIXED Gres=gpu:a100_1g.5gb:7 GresUsed=gpu:a100_1g.5gb:2
        """;
}
