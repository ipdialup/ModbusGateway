namespace ModbusGateway.Core;

public sealed class GatewayOptions
{
    public SerialOptions Serial { get; set; } = new();
    public TcpOptions Tcp { get; set; } = new();

    /// <summary>How long to wait for the slave's reply to one request.</summary>
    public int ResponseTimeoutMs { get; set; } = 1000;

    /// <summary>Quiet time between the end of one reply and the next request (Solis needs a gap).</summary>
    public int InterFrameDelayMs { get; set; } = 50;

    /// <summary>Extra attempts for READ requests (FC 1-4) after a timeout or CRC error. Writes are never retried.</summary>
    public int ReadRetries { get; set; } = 1;

    /// <summary>Status endpoint for the tray app (http://127.0.0.1:port/status).</summary>
    public int StatusPort { get; set; } = 5021;

    public string LogFolder { get; set; } = @"C:\ProgramData\ModbusGateway\logs";
    public int LogRetentionDays { get; set; } = 30;
}

public sealed class SerialOptions
{
    public string PortName { get; set; } = "COM4";
    public int BaudRate { get; set; } = 9600;
    public string Parity { get; set; } = "None";
    public int DataBits { get; set; } = 8;
    public string StopBits { get; set; } = "One";
    public bool DtrEnable { get; set; } = true;
    public bool RtsEnable { get; set; } = false;
    public int ReopenDelayMs { get; set; } = 5000;

    public string Describe() => $"{PortName} {BaudRate} {DataBits}{Parity[0]}{(StopBits == "Two" ? 2 : StopBits == "OnePointFive" ? 1.5 : 1)}";
}

public sealed class TcpOptions
{
    public string Bind { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 502;
    public int MaxClients { get; set; } = 8;
    /// <summary>Drop a client that sends nothing for this long (0 = never).</summary>
    public int IdleTimeoutSeconds { get; set; } = 600;
}
