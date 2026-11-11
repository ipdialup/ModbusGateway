using System.Collections.Concurrent;

namespace ModbusGateway.Core;

public sealed class ClientInfo
{
    public required string Endpoint { get; init; }
    public DateTime ConnectedAt { get; init; } = DateTime.Now;
    public DateTime LastActivity;
    public long Requests;
}

/// <summary>Counters shared by the TCP side and the serial worker. All updates are lock-free.</summary>
public sealed class GatewayStats
{
    public long Requests, Responses, Timeouts, CrcErrors, ModbusExceptions, GatewayErrors, Retries, StrayBytes, TotalConnections;
    public long TcpBytesIn, TcpBytesOut, SerialBytesOut, SerialBytesIn;

    private long _latencyTicksTotal, _latencyCount;
    private double _lastLatencyMs, _maxLatencyMs;
    private readonly object _stateLock = new();
    private string _portState = "Starting";
    private DateTime _portStateSince = DateTime.Now;
    private bool _portOpen;
    private string? _lastError;
    private DateTime? _lastErrorTime;

    public readonly ConcurrentDictionary<int, ClientInfo> Clients = new();
    public readonly ConcurrentDictionary<byte, long> FunctionCodes = new();

    // per-second history
    private const int HistoryLength = 120;
    private readonly Queue<(double req, double err, double bytes)> _history = new();
    private long _prevReq, _prevErr, _prevBytes;

    public DateTime StartedAt { get; } = DateTime.Now;

    public long ErrorTotal => Interlocked.Read(ref Timeouts) + Interlocked.Read(ref CrcErrors) + Interlocked.Read(ref GatewayErrors);

    public void SetPortState(bool open, string state)
    {
        lock (_stateLock)
        {
            if (_portState == state && _portOpen == open) return;
            _portOpen = open; _portState = state; _portStateSince = DateTime.Now;
        }
    }

    public void SetError(string error)
    {
        lock (_stateLock) { _lastError = error; _lastErrorTime = DateTime.Now; }
    }

    public void AddLatency(TimeSpan t)
    {
        var ms = t.TotalMilliseconds;
        Interlocked.Add(ref _latencyTicksTotal, t.Ticks);
        Interlocked.Increment(ref _latencyCount);
        _lastLatencyMs = ms;
        if (ms > _maxLatencyMs) _maxLatencyMs = ms;
    }

    /// <summary>Called once a second to record the rate history.</summary>
    public void Sample()
    {
        long req = Interlocked.Read(ref Requests), err = ErrorTotal;
        long bytes = Interlocked.Read(ref SerialBytesIn) + Interlocked.Read(ref SerialBytesOut);
        lock (_history)
        {
            _history.Enqueue((req - _prevReq, err - _prevErr, bytes - _prevBytes));
            while (_history.Count > HistoryLength) _history.Dequeue();
        }
        _prevReq = req; _prevErr = err; _prevBytes = bytes;
    }

    public GatewayStatus Snapshot()
    {
        var s = new GatewayStatus
        {
            Now = DateTime.Now,
            StartedAt = StartedAt,
            Requests = Interlocked.Read(ref Requests),
            Responses = Interlocked.Read(ref Responses),
            Timeouts = Interlocked.Read(ref Timeouts),
            CrcErrors = Interlocked.Read(ref CrcErrors),
            ModbusExceptions = Interlocked.Read(ref ModbusExceptions),
            GatewayErrors = Interlocked.Read(ref GatewayErrors),
            Retries = Interlocked.Read(ref Retries),
            StrayBytes = Interlocked.Read(ref StrayBytes),
            TotalConnections = Interlocked.Read(ref TotalConnections),
            TcpBytesIn = Interlocked.Read(ref TcpBytesIn),
            TcpBytesOut = Interlocked.Read(ref TcpBytesOut),
            SerialBytesOut = Interlocked.Read(ref SerialBytesOut),
            SerialBytesIn = Interlocked.Read(ref SerialBytesIn),
            LastLatencyMs = Math.Round(_lastLatencyMs, 1),
            MaxLatencyMs = Math.Round(_maxLatencyMs, 1),
        };
        long cnt = Interlocked.Read(ref _latencyCount);
        s.AvgLatencyMs = cnt == 0 ? 0 : Math.Round(TimeSpan.FromTicks(Interlocked.Read(ref _latencyTicksTotal) / cnt).TotalMilliseconds, 1);
        lock (_stateLock)
        {
            s.PortOpen = _portOpen; s.PortState = _portState; s.PortStateSince = _portStateSince;
            s.LastError = _lastError; s.LastErrorTime = _lastErrorTime;
        }
        s.Clients = Clients.Values.OrderBy(c => c.ConnectedAt).Select(c => new ClientStatus
        {
            Endpoint = c.Endpoint, ConnectedAt = c.ConnectedAt, LastActivity = c.LastActivity, Requests = Interlocked.Read(ref c.Requests),
        }).ToList();
        s.FunctionCodes = FunctionCodes.OrderBy(k => k.Key).ToDictionary(k => FunctionName(k.Key), k => k.Value);
        lock (_history)
        {
            s.RequestsPerSecond = _history.Select(h => h.req).ToArray();
            s.ErrorsPerSecond = _history.Select(h => h.err).ToArray();
            s.BytesPerSecond = _history.Select(h => h.bytes).ToArray();
        }
        return s;
    }

    public static string FunctionName(byte fc) => fc switch
    {
        1 => "01 Read coils",
        2 => "02 Read discrete inputs",
        3 => "03 Read holding registers",
        4 => "04 Read input registers",
        5 => "05 Write coil",
        6 => "06 Write register",
        15 => "15 Write coils",
        16 => "16 Write registers",
        0x17 => "23 Read/write registers",
        _ => $"{fc:D2} Function {fc}",
    };
}
