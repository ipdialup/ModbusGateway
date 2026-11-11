using System.Collections.Concurrent;
using ModbusGateway.Core;

namespace ModbusGateway.Tests;

/// <summary>An in-memory Modbus RTU slave standing in for the inverter.</summary>
public sealed class FakeSlave : ISerialLink
{
    public byte Unit = 1;
    public readonly ushort[] Registers = new ushort[70000];
    public int SilentRequests;          // ignore this many requests (timeout)
    public int CorruptReplies;          // flip a byte in this many replies (CRC error)
    public byte[] NoiseBeforeReply = Array.Empty<byte>();
    public int Received;
    private readonly BlockingCollection<byte> _rx = new();
    private bool _open;

    public bool IsOpen => _open;
    public void Open() => _open = true;
    public void Close() => _open = false;
    public void Dispose() { }

    public int DiscardInput()
    {
        int n = 0;
        while (_rx.TryTake(out _)) n++;
        return n;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        var req = buffer.AsSpan(offset, count).ToArray();
        Interlocked.Increment(ref Received);
        Assert.True(Crc16.Check(req), "gateway sent a bad CRC");
        if (req[0] != Unit && req[0] != 0) return;
        if (SilentRequests > 0) { SilentRequests--; return; }
        var reply = Handle(req[..^2]);
        if (reply == null || req[0] == 0) return;
        var wire = Crc16.Append(reply);
        if (CorruptReplies > 0) { CorruptReplies--; wire[3] ^= 0xFF; }
        foreach (var b in NoiseBeforeReply) _rx.Add(b);
        NoiseBeforeReply = Array.Empty<byte>();
        foreach (var b in wire) _rx.Add(b);
    }

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        if (!_rx.TryTake(out var first, timeoutMs)) return 0;
        buffer[offset] = first;
        int n = 1;
        while (n < count && _rx.TryTake(out var b)) buffer[offset + n++] = b;
        return n;
    }

    private byte[]? Handle(byte[] f)
    {
        byte fc = f[1];
        int addr = f[2] << 8 | f[3];
        switch (fc)
        {
            case 3:
            case 4:
            {
                int count = f[4] << 8 | f[5];
                if (count is < 1 or > 125) return new byte[] { f[0], (byte)(fc | 0x80), 3 };
                if (addr + count > 60000) return new byte[] { f[0], (byte)(fc | 0x80), 2 };
                var r = new byte[3 + count * 2];
                r[0] = f[0]; r[1] = fc; r[2] = (byte)(count * 2);
                for (int i = 0; i < count; i++) { r[3 + i * 2] = (byte)(Registers[addr + i] >> 8); r[4 + i * 2] = (byte)Registers[addr + i]; }
                return r;
            }
            case 6:
                Registers[addr] = (ushort)(f[4] << 8 | f[5]);
                return f;
            case 16:
            {
                int count = f[4] << 8 | f[5];
                for (int i = 0; i < count; i++) Registers[addr + i] = (ushort)(f[7 + i * 2] << 8 | f[8 + i * 2]);
                return f[..6];
            }
            default:
                return new byte[] { f[0], (byte)(fc | 0x80), 1 };
        }
    }
}
