using System.IO.Ports;

namespace ModbusGateway.Core;

/// <summary>The RS-485 side, abstracted so tests can plug in a fake slave.</summary>
public interface ISerialLink : IDisposable
{
    bool IsOpen { get; }
    void Open();
    void Close();
    /// <summary>Throws away anything already in the receive buffer; returns how many bytes were dropped.</summary>
    int DiscardInput();
    void Write(byte[] buffer, int offset, int count);
    /// <summary>Reads up to <paramref name="count"/> bytes, waiting at most <paramref name="timeoutMs"/>. Returns 0 on timeout.</summary>
    int Read(byte[] buffer, int offset, int count, int timeoutMs);
}

public sealed class SerialPortLink : ISerialLink
{
    private readonly SerialOptions _o;
    private SerialPort? _port;

    public SerialPortLink(SerialOptions options) => _o = options;

    public bool IsOpen => _port?.IsOpen == true;

    public void Open()
    {
        Close();
        var p = new SerialPort(_o.PortName, _o.BaudRate,
            Enum.Parse<Parity>(_o.Parity, true), _o.DataBits, Enum.Parse<StopBits>(_o.StopBits, true))
        {
            Handshake = Handshake.None,
            DtrEnable = _o.DtrEnable,
            RtsEnable = _o.RtsEnable,
            ReadBufferSize = 4096,
            WriteBufferSize = 1024,
            WriteTimeout = 1000,
        };
        p.Open();
        p.DiscardInBuffer();
        _port = p;
    }

    public void Close()
    {
        var p = _port;
        _port = null;
        if (p == null) return;
        try { if (p.IsOpen) p.Close(); } catch { /* USB adapter may already be gone */ }
        p.Dispose();
    }

    public int DiscardInput()
    {
        var p = Port;
        int n = p.BytesToRead;
        if (n > 0) p.DiscardInBuffer();
        return n;
    }

    public void Write(byte[] buffer, int offset, int count) => Port.Write(buffer, offset, count);

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        var p = Port;
        p.ReadTimeout = Math.Max(1, timeoutMs);
        try { return p.Read(buffer, offset, count); }
        catch (TimeoutException) { return 0; }
    }

    private SerialPort Port => _port is { IsOpen: true } p ? p : throw new InvalidOperationException("Serial port is not open");

    public void Dispose() => Close();
}
