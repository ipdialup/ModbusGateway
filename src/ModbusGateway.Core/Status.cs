namespace ModbusGateway.Core;

/// <summary>What the service publishes on /status for the tray app.</summary>
public sealed class GatewayStatus
{
    public DateTime Now { get; set; }
    public DateTime StartedAt { get; set; }
    public string Version { get; set; } = "";

    public string PortName { get; set; } = "";
    public string PortSettings { get; set; } = "";
    public bool PortOpen { get; set; }
    public string PortState { get; set; } = "";
    public DateTime PortStateSince { get; set; }
    public string TcpEndpoint { get; set; } = "";
    public int QueueDepth { get; set; }

    public long Requests { get; set; }
    public long Responses { get; set; }
    public long Timeouts { get; set; }
    public long CrcErrors { get; set; }
    public long ModbusExceptions { get; set; }
    public long GatewayErrors { get; set; }
    public long Retries { get; set; }
    public long StrayBytes { get; set; }
    public long TotalConnections { get; set; }

    public long TcpBytesIn { get; set; }
    public long TcpBytesOut { get; set; }
    public long SerialBytesOut { get; set; }
    public long SerialBytesIn { get; set; }

    public double LastLatencyMs { get; set; }
    public double AvgLatencyMs { get; set; }
    public double MaxLatencyMs { get; set; }

    public string? LastError { get; set; }
    public DateTime? LastErrorTime { get; set; }

    public List<ClientStatus> Clients { get; set; } = new();
    public Dictionary<string, long> FunctionCodes { get; set; } = new();

    /// <summary>Per-second history, oldest first (up to 120 s).</summary>
    public double[] RequestsPerSecond { get; set; } = Array.Empty<double>();
    public double[] ErrorsPerSecond { get; set; } = Array.Empty<double>();
    public double[] BytesPerSecond { get; set; } = Array.Empty<double>();

    public List<EventEntry> RecentEvents { get; set; } = new();
}

public sealed class ClientStatus
{
    public string Endpoint { get; set; } = "";
    public DateTime ConnectedAt { get; set; }
    public DateTime LastActivity { get; set; }
    public long Requests { get; set; }
}

public sealed class EventEntry
{
    public DateTime Time { get; set; }
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
}
