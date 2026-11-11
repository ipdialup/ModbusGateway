namespace ModbusGateway.Core;

/// <summary>Keeps the last events in memory for the tray and appends them to a daily log file.</summary>
public sealed class GatewayLog
{
    private readonly object _lock = new();
    private readonly LinkedList<EventEntry> _recent = new();
    private readonly string? _folder;
    private readonly int _retentionDays;
    private DateTime _lastPurge;
    public const int Keep = 200;

    public GatewayLog(string? folder, int retentionDays = 30)
    {
        _folder = folder;
        _retentionDays = retentionDays;
        if (_folder != null) { try { Directory.CreateDirectory(_folder); } catch { _folder = null; } }
    }

    public event Action<EventEntry>? Written;

    public void Info(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg) => Write("ERROR", msg);

    public void Write(string level, string msg)
    {
        var e = new EventEntry { Time = DateTime.Now, Level = level, Message = msg };
        lock (_lock)
        {
            _recent.AddLast(e);
            while (_recent.Count > Keep) _recent.RemoveFirst();
            if (_folder != null)
            {
                try
                {
                    File.AppendAllText(Path.Combine(_folder, $"gateway-{e.Time:yyyy-MM-dd}.log"),
                        $"{e.Time:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {msg}{Environment.NewLine}");
                    if ((e.Time - _lastPurge).TotalHours > 6) { _lastPurge = e.Time; Purge(); }
                }
                catch { /* logging must never break the gateway */ }
            }
        }
        Written?.Invoke(e);
    }

    public List<EventEntry> Recent(int count)
    {
        lock (_lock) return _recent.Reverse().Take(count).ToList();
    }

    private void Purge()
    {
        foreach (var f in Directory.GetFiles(_folder!, "gateway-*.log"))
            if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-_retentionDays))
                try { File.Delete(f); } catch { }
    }
}
