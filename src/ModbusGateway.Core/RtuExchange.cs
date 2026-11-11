using System.Diagnostics;

namespace ModbusGateway.Core;

public enum ExchangeOutcome { Ok, Timeout, CrcError }

/// <param name="Frame">Reply without CRC (unit, function, data), or null for broadcasts.</param>
public readonly record struct ExchangeResult(ExchangeOutcome Outcome, byte[]? Frame, int BytesSent, int BytesReceived, int StrayBytes, string? Detail);

/// <summary>Sends one RTU request and reads its reply, using the function code to know the reply length.</summary>
public static class RtuExchange
{
    /// <summary>Total reply length incl. CRC once 3 bytes are in, or null if the function code has no fixed rule.</summary>
    public static int? ExpectedLength(byte requestFc, ReadOnlySpan<byte> head)
    {
        byte fc = head[1];
        if (fc == (requestFc | 0x80)) return 5;            // exception: unit, fc|0x80, code, crc
        return fc switch
        {
            1 or 2 or 3 or 4 or 0x17 => 3 + head[2] + 2,   // unit, fc, byteCount, data, crc
            5 or 6 or 15 or 16 or 8 => 8,                  // echo of address + value/count
            7 => 5,
            0x16 => 10,
            _ => null,
        };
    }

    public static ExchangeResult Run(ISerialLink link, byte[] requestNoCrc, int timeoutMs)
    {
        byte unit = requestNoCrc[0], fc = requestNoCrc[1];
        int stray = link.DiscardInput();
        var tx = Crc16.Append(requestNoCrc);
        link.Write(tx, 0, tx.Length);
        if (unit == 0) return new(ExchangeOutcome.Ok, null, tx.Length, 0, stray, null); // broadcast: no reply

        var buf = new byte[512];
        int n = 0, received = 0;
        int? total = null;
        var sw = Stopwatch.StartNew();

        while (true)
        {
            if (total is null || n < total)
            {
                int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (remaining <= 0)
                    return new(ExchangeOutcome.Timeout, null, tx.Length, received, stray,
                        n == 0 ? "no reply" : $"partial reply ({n} of {total?.ToString() ?? "?"} bytes)");
                int want = Math.Min((total ?? 3) - n, buf.Length - n);
                int r = link.Read(buf, n, want, remaining);
                if (r == 0) continue;
                n += r; received += r;
            }

            // Resync: drop leading noise until the frame starts with our unit + function code.
            int drop = 0;
            while (drop < n && (buf[drop] != unit || (n - drop >= 2 && buf[drop + 1] != fc && buf[drop + 1] != (fc | 0x80))))
                drop++;
            if (drop > 0)
            {
                Buffer.BlockCopy(buf, drop, buf, 0, n - drop);
                n -= drop; stray += drop; total = null;
                continue;
            }

            if (total is null && n >= 3)
            {
                total = ExpectedLength(fc, buf);
                if (total is null)
                {
                    // Unknown function: take everything until the line goes quiet.
                    int r;
                    while (n < buf.Length && (r = link.Read(buf, n, buf.Length - n, 30)) > 0) { n += r; received += r; }
                    total = n;
                }
                if (total > buf.Length || total < 4)
                    return new(ExchangeOutcome.CrcError, null, tx.Length, received, stray, $"bad length {total}");
            }

            if (total is not null && n >= total)
            {
                var frame = buf.AsSpan(0, total.Value);
                if (!Crc16.Check(frame))
                    return new(ExchangeOutcome.CrcError, null, tx.Length, received, stray, "CRC mismatch");
                stray += n - total.Value;
                return new(ExchangeOutcome.Ok, frame[..^2].ToArray(), tx.Length, received, stray, null);
            }
        }
    }
}
