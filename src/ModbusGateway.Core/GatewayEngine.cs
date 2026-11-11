using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ModbusGateway.Core;

/// <summary>
/// Modbus TCP server in front of a Modbus RTU master. Requests from any number of TCP clients
/// are queued and sent to the RS-485 bus one at a time.
/// </summary>
public sealed class GatewayEngine
{
    private readonly GatewayOptions _o;
    private readonly ISerialLink _link;
    private readonly GatewayLog _log;
    private readonly Channel<Pending> _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });
    private int _queueDepth, _nextClientId;
    private TcpListener? _listener;

    public GatewayStats Stats { get; } = new();
    public IPEndPoint? LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;
    public int QueueDepth => Volatile.Read(ref _queueDepth);

    private sealed record Pending(byte[] Frame, TaskCompletionSource<byte[]> Reply, DateTime Enqueued);

    public GatewayEngine(GatewayOptions options, ISerialLink link, GatewayLog log)
    {
        _o = options; _link = link; _log = log;
    }

    /// <summary>Binds the TCP port (throws if it is taken) and returns the task that runs until <paramref name="ct"/> is cancelled.</summary>
    public Task Start(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Parse(_o.Tcp.Bind), _o.Tcp.Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        _listener.Start();
        _log.Info($"Listening on {LocalEndpoint}, serial {_o.Serial.Describe()}, timeout {_o.ResponseTimeoutMs} ms, gap {_o.InterFrameDelayMs} ms, read retries {_o.ReadRetries}");

        var serial = Task.Factory.StartNew(() => SerialWorker(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var accept = AcceptLoop(ct);
        var sampler = Sampler(ct);
        return Task.WhenAll(serial, accept, sampler);
    }

    // ---------------------------------------------------------------- TCP side

    private async Task AcceptLoop(CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { _listener!.Stop(); } catch { } });
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.Warn("Accept failed: " + ex.Message); await Task.Delay(500, CancellationToken.None); continue; }

            if (Stats.Clients.Count >= _o.Tcp.MaxClients)
            {
                _log.Warn($"Refused {client.Client.RemoteEndPoint}: already {Stats.Clients.Count} clients");
                client.Dispose();
                continue;
            }
            _ = HandleClient(client, ct);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _nextClientId);
        var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var info = new ClientInfo { Endpoint = ep, LastActivity = DateTime.Now };
        Stats.Clients[id] = info;
        Interlocked.Increment(ref Stats.TotalConnections);
        _log.Info($"Client connected: {ep}");
        string why = "closed by client";
        try
        {
            client.NoDelay = true;
            using var _ = client;
            var s = client.GetStream();
            var head = new byte[7];
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (_o.Tcp.IdleTimeoutSeconds > 0) idle.CancelAfter(TimeSpan.FromSeconds(_o.Tcp.IdleTimeoutSeconds));
                if (!await ReadExact(s, head, idle.Token)) break;

                int len = head[4] << 8 | head[5];
                if (head[2] != 0 || head[3] != 0 || len < 2 || len > 254) { why = $"bad MBAP header (protocol {head[2]:X2}{head[3]:X2}, length {len})"; break; }
                var frame = new byte[len];           // unit + PDU
                frame[0] = head[6];
                if (!await ReadExact(s, frame.AsMemory(1), idle.Token)) break;

                Interlocked.Add(ref Stats.TcpBytesIn, 7 + len - 1);
                Interlocked.Increment(ref Stats.Requests);
                Interlocked.Increment(ref info.Requests);
                Stats.FunctionCodes.AddOrUpdate(frame[1], 1, (_, v) => v + 1);
                info.LastActivity = DateTime.Now;

                var p = new Pending(frame, new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously), DateTime.Now);
                Interlocked.Increment(ref _queueDepth);
                await _queue.Writer.WriteAsync(p, ct);
                var reply = await p.Reply.Task.WaitAsync(ct);
                if (reply.Length == 0) continue;     // broadcast: nothing to send back

                var outBuf = new byte[6 + reply.Length];
                outBuf[0] = head[0]; outBuf[1] = head[1];           // transaction id
                outBuf[4] = (byte)(reply.Length >> 8); outBuf[5] = (byte)reply.Length;
                Buffer.BlockCopy(reply, 0, outBuf, 6, reply.Length);
                await s.WriteAsync(outBuf, ct);
                Interlocked.Add(ref Stats.TcpBytesOut, outBuf.Length);
            }
            if (ct.IsCancellationRequested) why = "gateway stopping";
        }
        catch (OperationCanceledException) { why = ct.IsCancellationRequested ? "gateway stopping" : $"idle for {_o.Tcp.IdleTimeoutSeconds} s"; }
        catch (IOException ex) { why = ex.InnerException?.Message ?? ex.Message; }
        catch (SocketException ex) { why = ex.Message; }
        catch (Exception ex) { why = "error: " + ex.Message; _log.Error($"Client {ep}: {ex}"); }
        finally
        {
            Stats.Clients.TryRemove(id, out _);
            _log.Info($"Client disconnected: {ep} ({why}, {info.Requests} requests)");
        }
    }

    private static async Task<bool> ReadExact(NetworkStream s, Memory<byte> buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int r = await s.ReadAsync(buf[got..], ct);
            if (r == 0) return false;
            got += r;
        }
        return true;
    }

    // ---------------------------------------------------------------- serial side

    private void SerialWorker(CancellationToken ct)
    {
        var lastRx = Stopwatch.StartNew();
        DateTime nextOpenAttempt = DateTime.MinValue;
        string? lastOpenError = null;

        while (!ct.IsCancellationRequested)
        {
            if (!_link.IsOpen && DateTime.Now >= nextOpenAttempt)
            {
                try
                {
                    _link.Open();
                    Stats.SetPortState(true, "Open");
                    _log.Info($"Opened {_o.Serial.Describe()}");
                    lastOpenError = null;
                }
                catch (Exception ex)
                {
                    nextOpenAttempt = DateTime.Now.AddMilliseconds(_o.Serial.ReopenDelayMs);
                    Stats.SetPortState(false, "Can't open: " + ex.Message);
                    if (ex.Message != lastOpenError) { _log.Error($"Can't open {_o.Serial.PortName}: {ex.Message}"); Stats.SetError(ex.Message); }
                    lastOpenError = ex.Message;
                }
            }

            Pending p;
            try
            {
                // Wake up at least every second so a closed port gets retried.
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(1000);
                if (!_queue.Reader.WaitToReadAsync(wait.Token).AsTask().GetAwaiter().GetResult()) return;
                if (!_queue.Reader.TryRead(out p!)) continue;
            }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) return; continue; }

            Interlocked.Decrement(ref _queueDepth);

            if (!_link.IsOpen)
            {
                Reply(p, Exception(p.Frame, 0x0A));   // gateway path unavailable
                continue;
            }

            byte fc = p.Frame[1];
            bool isRead = fc is >= 1 and <= 4;
            int attempts = 1 + (isRead ? Math.Max(0, _o.ReadRetries) : 0);
            byte[]? reply = null;
            string? failure = null;

            for (int a = 0; a < attempts && reply == null; a++)
            {
                if (a > 0) Interlocked.Increment(ref Stats.Retries);
                int gap = _o.InterFrameDelayMs - (int)lastRx.ElapsedMilliseconds;
                if (gap > 0) Thread.Sleep(gap);

                var sw = Stopwatch.StartNew();
                ExchangeResult r;
                try { r = RtuExchange.Run(_link, p.Frame, _o.ResponseTimeoutMs); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    failure = "serial port lost: " + ex.Message;
                    _log.Error($"{_o.Serial.PortName} lost: {ex.Message}");
                    Stats.SetError(failure);
                    Stats.SetPortState(false, "Lost: " + ex.Message);
                    _link.Close();
                    nextOpenAttempt = DateTime.Now.AddMilliseconds(_o.Serial.ReopenDelayMs);
                    break;
                }
                lastRx.Restart();
                Interlocked.Add(ref Stats.SerialBytesOut, r.BytesSent);
                Interlocked.Add(ref Stats.SerialBytesIn, r.BytesReceived);
                Interlocked.Add(ref Stats.StrayBytes, r.StrayBytes);
                if (r.StrayBytes > 0) _log.Warn($"Dropped {r.StrayBytes} unexpected byte(s) on the bus");

                switch (r.Outcome)
                {
                    case ExchangeOutcome.Ok:
                        Stats.AddLatency(sw.Elapsed);
                        reply = r.Frame ?? Array.Empty<byte>();
                        if (reply.Length >= 3 && (reply[1] & 0x80) != 0)
                        {
                            Interlocked.Increment(ref Stats.ModbusExceptions);
                            _log.Warn($"Slave {reply[0]} exception {reply[2]:X2} ({ExceptionName(reply[2])}) for {Describe(p.Frame)}");
                        }
                        break;
                    case ExchangeOutcome.Timeout:
                        Interlocked.Increment(ref Stats.Timeouts);
                        failure = $"timeout ({r.Detail}) for {Describe(p.Frame)}";
                        break;
                    case ExchangeOutcome.CrcError:
                        Interlocked.Increment(ref Stats.CrcErrors);
                        failure = $"{r.Detail} for {Describe(p.Frame)}";
                        break;
                }
                if (reply == null)
                    _log.Warn($"Attempt {a + 1}/{attempts}: {failure}");
            }

            if (reply != null)
            {
                Interlocked.Increment(ref Stats.Responses);
                Reply(p, reply);
            }
            else
            {
                Stats.SetError(failure ?? "no reply");
                Reply(p, Exception(p.Frame, _link.IsOpen ? (byte)0x0B : (byte)0x0A));
            }
        }
    }

    private void Reply(Pending p, byte[] reply)
    {
        if (reply.Length >= 3 && (reply[1] & 0x80) != 0 && (reply[2] == 0x0A || reply[2] == 0x0B))
            Interlocked.Increment(ref Stats.GatewayErrors);
        p.Reply.TrySetResult(reply);
    }

    private static byte[] Exception(byte[] req, byte code) => new[] { req[0], (byte)(req[1] | 0x80), code };

    private async Task Sampler(CancellationToken ct)
    {
        using var t = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await t.WaitForNextTickAsync(ct)) Stats.Sample(); }
        catch (OperationCanceledException) { }
    }

    public static string Describe(byte[] f)
    {
        if (f.Length >= 6 && f[1] is >= 1 and <= 6)
            return $"unit {f[0]} fc {f[1]} @{(f[2] << 8 | f[3])} x{(f[4] << 8 | f[5])}";
        if (f.Length >= 6 && f[1] is 15 or 16)
            return $"unit {f[0]} fc {f[1]} @{(f[2] << 8 | f[3])} n{(f[4] << 8 | f[5])}";
        return $"unit {f[0]} fc {f[1]}";
    }

    public static string ExceptionName(byte code) => code switch
    {
        1 => "illegal function",
        2 => "illegal data address",
        3 => "illegal data value",
        4 => "slave device failure",
        5 => "acknowledge",
        6 => "slave busy",
        0x0A => "gateway path unavailable",
        0x0B => "gateway target failed to respond",
        _ => "unknown",
    };
}
