using ModbusGateway.Core;

namespace ModbusGateway.Tray;

public enum HealthLevel { Unknown, Good, Warning, Bad }

public static class Health
{
    public static HealthLevel Of(GatewayStatus? s)
    {
        if (s == null) return HealthLevel.Unknown;
        if (!s.PortOpen) return HealthLevel.Bad;
        return ErrorsLastMinute(s) > 0 ? HealthLevel.Warning : HealthLevel.Good;
    }

    public static double ErrorsLastMinute(GatewayStatus s) => s.ErrorsPerSecond.TakeLast(60).Sum();
    public static double RequestsLastMinute(GatewayStatus s) => s.RequestsPerSecond.TakeLast(60).Sum();

    public static Color ColorOf(HealthLevel h) => h switch
    {
        HealthLevel.Good => Color.FromArgb(0x2E, 0xA0, 0x43),
        HealthLevel.Warning => Color.FromArgb(0xE0, 0x9B, 0x10),
        HealthLevel.Bad => Color.FromArgb(0xD0, 0x3B, 0x3B),
        _ => Color.FromArgb(0x8A, 0x8A, 0x8A),
    };

    public static string Describe(GatewayStatus? s) => s == null
        ? "Gateway service not running"
        : !s.PortOpen ? $"{s.PortName}: {s.PortState}"
        : $"{s.PortName} open, {s.RequestsPerSecond.TakeLast(10).DefaultIfEmpty(0).Average():0.0} req/s"
          + (ErrorsLastMinute(s) > 0 ? $", {ErrorsLastMinute(s):0} errors in the last min" : "");

    public static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0.00} MB",
        _ => $"{b / 1073741824.0:0.00} GB",
    };

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
        : $"{t.Seconds}s";
}
