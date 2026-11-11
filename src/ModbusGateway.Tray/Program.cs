namespace ModbusGateway.Tray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\ModbusGatewayTray", out bool first);
        if (!first) return;   // already running in this session

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(openWindow: args.Contains("--show")));
    }
}
