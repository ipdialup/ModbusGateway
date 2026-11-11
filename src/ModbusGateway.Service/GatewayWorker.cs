using ModbusGateway.Core;

namespace ModbusGateway.Service;

public sealed class GatewayWorker : BackgroundService
{
    private readonly GatewayOptions _o;
    private readonly ILogger<GatewayWorker> _logger;
    private readonly IHostApplicationLifetime _life;

    public GatewayWorker(GatewayOptions options, ILogger<GatewayWorker> logger, IHostApplicationLifetime life)
    {
        _o = options; _logger = logger; _life = life;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var log = new GatewayLog(_o.LogFolder, _o.LogRetentionDays);
        log.Written += e =>
        {
            if (e.Level == "ERROR") _logger.LogError("{Message}", e.Message);
            else if (e.Level == "WARN") _logger.LogWarning("{Message}", e.Message);
            else _logger.LogInformation("{Message}", e.Message);
        };
        var version = typeof(GatewayWorker).Assembly.GetName().Version?.ToString(3) ?? "?";
        log.Info($"Modbus Gateway {version} starting");

        using var link = new SerialPortLink(_o.Serial);
        var engine = new GatewayEngine(_o, link, log);

        Task running;
        // The TCP port may still be held for a moment (old gateway closing): keep trying.
        while (true)
        {
            try { running = engine.Start(ct); break; }
            catch (System.Net.Sockets.SocketException ex) when (!ct.IsCancellationRequested)
            {
                log.Error($"Can't listen on {_o.Tcp.Bind}:{_o.Tcp.Port}: {ex.Message} - retrying in 10 s");
                engine.Stats.SetPortState(false, $"TCP port {_o.Tcp.Port} busy");
                try { await Task.Delay(10_000, ct); } catch (OperationCanceledException) { return; }
            }
        }

        var status = new StatusServer(_o.StatusPort, () =>
        {
            var s = engine.Stats.Snapshot();
            s.Version = version;
            s.PortName = _o.Serial.PortName;
            s.PortSettings = _o.Serial.Describe();
            s.TcpEndpoint = engine.LocalEndpoint?.ToString() ?? "";
            s.QueueDepth = engine.QueueDepth;
            s.RecentEvents = log.Recent(100);
            return s;
        }, log);
        var statusTask = status.Run(ct);

        try { await running; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Error("Gateway stopped unexpectedly: " + ex);
            Environment.ExitCode = 1;
            _life.StopApplication();   // service recovery restarts us
        }
        await statusTask;
        log.Info("Modbus Gateway stopped");
    }
}
