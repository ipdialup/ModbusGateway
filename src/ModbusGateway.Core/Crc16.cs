namespace ModbusGateway.Core;

/// <summary>Modbus RTU CRC-16 (polynomial 0xA001, init 0xFFFF, low byte first on the wire).</summary>
public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    public static byte[] Append(ReadOnlySpan<byte> frame)
    {
        var o = new byte[frame.Length + 2];
        frame.CopyTo(o);
        var c = Compute(frame);
        o[^2] = (byte)(c & 0xFF);
        o[^1] = (byte)(c >> 8);
        return o;
    }

    public static bool Check(ReadOnlySpan<byte> frameWithCrc)
    {
        if (frameWithCrc.Length < 4) return false;
        var c = Compute(frameWithCrc[..^2]);
        return frameWithCrc[^2] == (byte)(c & 0xFF) && frameWithCrc[^1] == (byte)(c >> 8);
    }
}
