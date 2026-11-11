using System.Net.Sockets;
using ModbusGateway.Core;

namespace ModbusGateway.Tests;

public class Crc16Tests
{
    [Fact]
    public void KnownVector()
    {
        // 01 03 00 00 00 0A -> CRC C5 CD
        var f = Crc16.Append(new byte[] { 1, 3, 0, 0, 0, 0x0A });
        Assert.Equal(0xC5, f[6]);
        Assert.Equal(0xCD, f[7]);
        Assert.True(Crc16.Check(f));
    }
}

public sealed class GatewayTests : IAsyncLifetime
{
    private readonly FakeSlave _slave = new();
    private readonly CancellationTokenSource _cts = new();
    private GatewayEngine _gw = null!;
    private Task _run = null!;

    public Task InitializeAsync()
    {
        var o = new GatewayOptions
        {
            Tcp = { Bind = "127.0.0.1", Port = 0 },
            ResponseTimeoutMs = 200,
            InterFrameDelayMs = 5,
            ReadRetries = 1,
        };
        _gw = new GatewayEngine(o, _slave, new GatewayLog(null));
        _run = _gw.Start(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        await _run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task<byte[]> Send(TcpClient c, ushort tid, byte[] unitAndPdu)
    {
        var s = c.GetStream();
        var mbap = new byte[] { (byte)(tid >> 8), (byte)tid, 0, 0, (byte)(unitAndPdu.Length >> 8), (byte)unitAndPdu.Length };
        await s.WriteAsync(mbap.Concat(unitAndPdu).ToArray());
        var head = new byte[6];
        await s.ReadExactlyAsync(head);
        Assert.Equal(tid >> 8, head[0]);
        Assert.Equal(tid & 0xFF, head[1]);
        var body = new byte[head[4] << 8 | head[5]];
        await s.ReadExactlyAsync(body);
        return body;
    }

    private async Task<TcpClient> Connect()
    {
        var c = new TcpClient();
        await c.ConnectAsync(_gw.LocalEndpoint!);
        return c;
    }

    [Fact]
    public async Task ReadsHoldingRegisters()
    {
        _slave.Registers[43074] = 3100;
        _slave.Registers[43075] = 0xBEEF;
        using var c = await Connect();
        var r = await Send(c, 0x1234, new byte[] { 1, 3, 0xA8, 0x42, 0, 2 });
        Assert.Equal(new byte[] { 1, 3, 4, 0x0C, 0x1C, 0xBE, 0xEF }, r);
        Assert.Equal(1, _gw.Stats.Responses);
    }

    [Fact]
    public async Task WritesSingleRegister()
    {
        using var c = await Connect();
        var r = await Send(c, 7, new byte[] { 1, 6, 0xA8, 0x42, 0, 60 });
        Assert.Equal(new byte[] { 1, 6, 0xA8, 0x42, 0, 60 }, r);
        Assert.Equal(60, _slave.Registers[43074]);
    }

    [Fact]
    public async Task PassesSlaveExceptionThrough()
    {
        using var c = await Connect();
        var r = await Send(c, 1, new byte[] { 1, 4, 0xEA, 0x60, 0, 10 });
        Assert.Equal(new byte[] { 1, 0x84, 2 }, r);
        Assert.Equal(1, _gw.Stats.ModbusExceptions);
    }

    [Fact]
    public async Task RetriesAReadAfterATimeout()
    {
        _slave.SilentRequests = 1;
        _slave.Registers[100] = 42;
        using var c = await Connect();
        var r = await Send(c, 2, new byte[] { 1, 3, 0, 100, 0, 1 });
        Assert.Equal(new byte[] { 1, 3, 2, 0, 42 }, r);
        Assert.Equal(1, _gw.Stats.Timeouts);
        Assert.Equal(1, _gw.Stats.Retries);
    }

    [Fact]
    public async Task NeverRetriesAWrite()
    {
        _slave.SilentRequests = 1;
        using var c = await Connect();
        var r = await Send(c, 3, new byte[] { 1, 6, 0, 1, 0, 9 });
        Assert.Equal(new byte[] { 1, 0x86, 0x0B }, r);
        Assert.Equal(1, _slave.Received);
        Assert.Equal(0, _gw.Stats.Retries);
    }

    [Fact]
    public async Task GivesUpWithGatewayTargetFailedWhenSlaveIsSilent()
    {
        _slave.SilentRequests = 5;
        using var c = await Connect();
        var r = await Send(c, 4, new byte[] { 1, 3, 0, 0, 0, 1 });
        Assert.Equal(new byte[] { 1, 0x83, 0x0B }, r);
        Assert.Equal(2, _gw.Stats.Timeouts);
        Assert.Equal(1, _gw.Stats.GatewayErrors);
    }

    [Fact]
    public async Task RecoversFromACorruptReply()
    {
        _slave.CorruptReplies = 1;
        _slave.Registers[5] = 5;
        using var c = await Connect();
        var r = await Send(c, 5, new byte[] { 1, 3, 0, 5, 0, 1 });
        Assert.Equal(new byte[] { 1, 3, 2, 0, 5 }, r);
        Assert.Equal(1, _gw.Stats.CrcErrors);
    }

    [Fact]
    public async Task SkipsNoiseBeforeTheReply()
    {
        _slave.NoiseBeforeReply = new byte[] { 0x00, 0xFF, 0x07 };
        _slave.Registers[9] = 9;
        using var c = await Connect();
        var r = await Send(c, 6, new byte[] { 1, 3, 0, 9, 0, 1 });
        Assert.Equal(new byte[] { 1, 3, 2, 0, 9 }, r);
        Assert.Equal(3, _gw.Stats.StrayBytes);
    }

    [Fact]
    public async Task SerialisesManyClients()
    {
        for (int i = 0; i < 50; i++) _slave.Registers[1000 + i] = (ushort)i;
        var tasks = Enumerable.Range(0, 5).Select(async k =>
        {
            using var c = await Connect();
            for (int i = 0; i < 10; i++)
            {
                int reg = 1000 + k * 10 + i;
                var r = await Send(c, (ushort)(k * 100 + i), new byte[] { 1, 3, (byte)(reg >> 8), (byte)reg, 0, 1 });
                Assert.Equal(k * 10 + i, r[3] << 8 | r[4]);
            }
        });
        await Task.WhenAll(tasks);
        Assert.Equal(50, _gw.Stats.Responses);
    }

    [Fact]
    public async Task ReportsPathUnavailableWhenPortIsClosed()
    {
        using var c = await Connect();
        await Send(c, 1, new byte[] { 1, 3, 0, 0, 0, 1 });   // port is open now
        _slave.Close();
        var r = await Send(c, 2, new byte[] { 1, 3, 0, 0, 0, 1 });
        Assert.Equal(new byte[] { 1, 0x83, 0x0A }, r);
    }
}
