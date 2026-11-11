using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ModbusGateway.Core;

namespace ModbusGateway.Tray;

public sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "ModbusGatewayTray";
    private const string LogFolder = @"C:\ProgramData\ModbusGateway\logs";

    private readonly NotifyIcon _icon;
    private readonly StatusClient _client = new(5021);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Dictionary<HealthLevel, Icon> _icons = new();
    private readonly ToolStripMenuItem _startWithWindows;
    private StatusForm? _form;
    private HealthLevel _lastLevel = HealthLevel.Good;
    private bool _busy;

    public GatewayStatus? Latest { get; private set; }
    public event Action<GatewayStatus?>? Updated;

    public TrayContext(bool openWindow)
    {
        foreach (HealthLevel h in Enum.GetValues<HealthLevel>()) _icons[h] = MakeIcon(Health.ColorOf(h));

        _startWithWindows = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = IsStartupEnabled() };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open status", null, (_, _) => ShowForm()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogs());
        menu.Items.Add("Restart gateway service...", null, (_, _) => RestartService());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startWithWindows);
        menu.Items.Add("Exit tray icon", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = _icons[HealthLevel.Unknown],
            Text = "Modbus Gateway: connecting...",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowForm();

        _timer.Tick += async (_, _) => await Poll();
        _timer.Start();
        _ = Poll();
        if (openWindow) ShowForm();
    }

    private async Task Poll()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            Latest = await _client.Get();
            var level = Health.Of(Latest);
            _icon.Icon = _icons[level];
            var text = "Modbus Gateway: " + Health.Describe(Latest);
            _icon.Text = text.Length > 127 ? text[..127] : text;

            if (level != _lastLevel)
            {
                if (level is HealthLevel.Bad or HealthLevel.Unknown)
                    _icon.ShowBalloonTip(5000, "Modbus Gateway", Health.Describe(Latest), ToolTipIcon.Warning);
                else if (_lastLevel is HealthLevel.Bad or HealthLevel.Unknown)
                    _icon.ShowBalloonTip(3000, "Modbus Gateway", "Back to normal: " + Health.Describe(Latest), ToolTipIcon.Info);
                _lastLevel = level;
            }
            Updated?.Invoke(Latest);
        }
        finally { _busy = false; }
    }

    private void ShowForm()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new StatusForm(this);
            _form.Show();
        }
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private static void OpenLogs()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", LogFolder) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Modbus Gateway"); }
    }

    private static void RestartService()
    {
        if (MessageBox.Show("Restart the Modbus Gateway service?\nHome Assistant will lose the inverter for a few seconds.",
                "Modbus Gateway", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -Command Restart-Service ModbusGateway")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (System.ComponentModel.Win32Exception) { /* UAC cancelled */ }
    }

    private static bool IsStartupEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) != null;
    }

    private void ToggleStartup()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsStartupEnabled()) k.DeleteValue(RunName, false);
        else k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        _startWithWindows.Checked = IsStartupEnabled();
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _form?.Close();
        _client.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private static Icon MakeIcon(Color c)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(c);
            g.FillEllipse(fill, 3, 3, 26, 26);
            using var ring = new Pen(Color.FromArgb(220, Color.White), 2.5f);
            g.DrawEllipse(ring, 3, 3, 26, 26);
            // two arrows: data in and out
            using var arrow = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(arrow, 10, 12, 22, 12); g.DrawLine(arrow, 18, 8.5f, 22, 12); g.DrawLine(arrow, 18, 15.5f, 22, 12);
            g.DrawLine(arrow, 22, 20, 10, 20); g.DrawLine(arrow, 14, 16.5f, 10, 20); g.DrawLine(arrow, 14, 23.5f, 10, 20);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }
}
