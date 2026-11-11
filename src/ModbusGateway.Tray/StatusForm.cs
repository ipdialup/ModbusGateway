using System.Drawing.Drawing2D;
using ModbusGateway.Core;

namespace ModbusGateway.Tray;

/// <summary>The window opened by double-clicking the tray icon. All built in code (no designer).</summary>
public sealed class StatusForm : Form
{
    private readonly TrayContext _tray;
    private readonly Label _banner = new() { Dock = DockStyle.Top, Height = 44, Font = new Font("Segoe UI", 12f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), ForeColor = Color.White };
    private readonly Dictionary<string, Label> _values = new();
    private readonly GraphPanel _graph = new() { Dock = DockStyle.Fill };
    private readonly ListView _clients = MakeList(("Client", 170), ("Connected", 110), ("Last activity", 100), ("Requests", 80));
    private readonly ListView _functions = MakeList(("Function", 230), ("Count", 90));
    private readonly ListView _events = MakeList(("Time", 80), ("Level", 60), ("Message", 620));

    private GatewayStatus? _prev;
    private DateTime _prevAt;

    public StatusForm(TrayContext tray)
    {
        _tray = tray;
        Text = "Modbus Gateway";
        Size = new Size(900, 760);
        MinimumSize = new Size(760, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        Icon = SystemIcons.Application;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(OverviewPage());
        tabs.TabPages.Add(Page("Clients", _clients));
        tabs.TabPages.Add(Page("Function codes", _functions));
        tabs.TabPages.Add(Page("Events", _events));

        Controls.Add(tabs);
        Controls.Add(_banner);

        _tray.Updated += OnUpdated;
        FormClosed += (_, _) => _tray.Updated -= OnUpdated;
        Shown += (_, _) => OnUpdated(_tray.Latest);
    }

    private TabPage OverviewPage()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(8) };
        for (int i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        void Section(string title)
        {
            var l = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 10, 3, 2), ForeColor = Color.FromArgb(0x30, 0x50, 0x80) };
            grid.Controls.Add(l);
            grid.SetColumnSpan(l, 4);
        }
        void Row(params string[] names)
        {
            foreach (var n in names)
            {
                grid.Controls.Add(new Label { Text = n, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 3, 3, 0) });
                var v = new Label { Text = "-", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 0) };
                _values[n] = v;
                grid.Controls.Add(v);
            }
            if (names.Length == 1) { grid.Controls.Add(new Label()); grid.Controls.Add(new Label()); }
        }

        Section("Serial port");
        Row("Port", "State");
        Row("Settings", "Since");
        Row("TCP server", "Clients");
        Row("Service up", "Queue");

        Section("Data flowed");
        Row("TCP in", "TCP out");
        Row("Serial out", "Serial in");
        Row("TCP in /s", "Serial in /s");

        Section("Counters");
        Row("Requests", "Responses");
        Row("Requests /s", "Retries");
        Row("Timeouts", "CRC errors");
        Row("Modbus exceptions", "Gateway errors");
        Row("Stray bytes", "Connections");

        Section("Latency");
        Row("Last", "Average");
        Row("Max", "Errors (1 min)");

        Section("Last error");
        var err = new Label { Text = "-", AutoSize = true, MaximumSize = new Size(820, 0), Margin = new Padding(3, 3, 3, 0) };
        _values["LastError"] = err;
        grid.Controls.Add(err);
        grid.SetColumnSpan(err, 4);

        var graphBox = new GroupBox { Text = "Last 2 minutes: requests/s (blue), errors/s (red)", Dock = DockStyle.Fill, Padding = new Padding(8) };
        graphBox.Controls.Add(_graph);

        var page = new TabPage("Overview");
        page.Controls.Add(graphBox);
        page.Controls.Add(grid);
        return page;
    }

    private static TabPage Page(string title, Control c)
    {
        var p = new TabPage(title);
        p.Controls.Add(c);
        return p;
    }

    private static ListView MakeList(params (string, int)[] cols)
    {
        var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        foreach (var (name, width) in cols) lv.Columns.Add(name, width);
        return lv;
    }

    private void Set(string key, string text)
    {
        if (_values.TryGetValue(key, out var l) && l.Text != text) l.Text = text;
    }

    private void OnUpdated(GatewayStatus? s)
    {
        if (IsDisposed) return;
        var level = Health.Of(s);
        _banner.BackColor = Health.ColorOf(level);
        _banner.Text = level switch
        {
            HealthLevel.Good => "OK  -  " + Health.Describe(s),
            HealthLevel.Warning => "Errors  -  " + Health.Describe(s),
            HealthLevel.Bad => "Port problem  -  " + Health.Describe(s),
            _ => "Can't reach the gateway service on 127.0.0.1:5021",
        };
        if (s == null) { _prev = null; return; }

        var now = DateTime.UtcNow;
        double secs = _prev == null ? 0 : (now - _prevAt).TotalSeconds;
        string Rate(long cur, long prev) => secs <= 0 ? "-" : Health.Bytes((long)Math.Max(0, (cur - prev) / secs)) + "/s";

        Set("Port", s.PortName);
        Set("State", s.PortState);
        Set("Settings", s.PortSettings);
        Set("Since", s.PortStateSince.ToLocalTime().ToString("dd MMM HH:mm:ss") + $"  ({Health.Duration(s.Now - s.PortStateSince)})");
        Set("TCP server", s.TcpEndpoint);
        Set("Clients", s.Clients.Count.ToString());
        Set("Service up", Health.Duration(s.Now - s.StartedAt) + $"  (v{s.Version})");
        Set("Queue", s.QueueDepth.ToString());

        Set("TCP in", Health.Bytes(s.TcpBytesIn));
        Set("TCP out", Health.Bytes(s.TcpBytesOut));
        Set("Serial out", Health.Bytes(s.SerialBytesOut));
        Set("Serial in", Health.Bytes(s.SerialBytesIn));
        Set("TCP in /s", _prev == null ? "-" : Rate(s.TcpBytesIn, _prev.TcpBytesIn));
        Set("Serial in /s", _prev == null ? "-" : Rate(s.SerialBytesIn, _prev.SerialBytesIn));

        Set("Requests", s.Requests.ToString("N0"));
        Set("Responses", s.Responses.ToString("N0"));
        Set("Requests /s", s.RequestsPerSecond.TakeLast(10).DefaultIfEmpty(0).Average().ToString("0.0"));
        Set("Retries", s.Retries.ToString("N0"));
        Set("Timeouts", s.Timeouts.ToString("N0"));
        Set("CRC errors", s.CrcErrors.ToString("N0"));
        Set("Modbus exceptions", s.ModbusExceptions.ToString("N0"));
        Set("Gateway errors", s.GatewayErrors.ToString("N0"));
        Set("Stray bytes", s.StrayBytes.ToString("N0"));
        Set("Connections", s.TotalConnections.ToString("N0"));

        Set("Last", $"{s.LastLatencyMs:0} ms");
        Set("Average", $"{s.AvgLatencyMs:0} ms");
        Set("Max", $"{s.MaxLatencyMs:0} ms");
        Set("Errors (1 min)", Health.ErrorsLastMinute(s).ToString("0"));
        Set("LastError", s.LastError == null ? "none" : $"{s.LastErrorTime?.ToLocalTime():dd MMM HH:mm:ss}  {s.LastError}");

        _graph.SetData(s.RequestsPerSecond, s.ErrorsPerSecond);

        FillList(_clients, s.Clients.Select(c => new[]
        {
            c.Endpoint, c.ConnectedAt.ToLocalTime().ToString("dd MMM HH:mm"),
            Health.Duration(s.Now - c.LastActivity) + " ago", c.Requests.ToString("N0"),
        }));
        FillList(_functions, s.FunctionCodes.OrderByDescending(kv => kv.Value).Select(kv => new[] { kv.Key, kv.Value.ToString("N0") }));
        FillList(_events, s.RecentEvents.OrderByDescending(e => e.Time).Select(e => new[] { e.Time.ToLocalTime().ToString("HH:mm:ss"), e.Level, e.Message }));

        _prev = s;
        _prevAt = now;
    }

    /// <summary>Rewrites a list only when its contents changed, so it doesn't flicker or lose the scroll position.</summary>
    private static void FillList(ListView lv, IEnumerable<string[]> rows)
    {
        var data = rows.ToList();
        bool same = lv.Items.Count == data.Count;
        for (int i = 0; same && i < data.Count; i++)
            for (int j = 0; same && j < data[i].Length; j++)
                same = lv.Items[i].SubItems.Count > j && lv.Items[i].SubItems[j].Text == data[i][j];
        if (same) return;

        lv.BeginUpdate();
        int top = lv.TopItem?.Index ?? 0;
        lv.Items.Clear();
        foreach (var r in data)
        {
            var item = new ListViewItem(r);
            if (r.Length > 1 && r[1] is "WARN" or "ERROR") item.ForeColor = r[1] == "ERROR" ? Color.Firebrick : Color.DarkGoldenrod;
            lv.Items.Add(item);
        }
        if (top > 0 && top < lv.Items.Count) lv.TopItem = lv.Items[top];
        lv.EndUpdate();
    }

    private sealed class GraphPanel : Panel
    {
        private double[] _req = Array.Empty<double>();
        private double[] _err = Array.Empty<double>();

        public GraphPanel() { DoubleBuffered = true; BackColor = Color.White; ResizeRedraw = true; }

        public void SetData(double[] req, double[] err) { _req = req; _err = err; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(40, 6, Width - 50, Height - 26);
            if (r.Width < 20 || r.Height < 20) return;

            double max = Math.Max(1, Math.Max(_req.DefaultIfEmpty(0).Max(), _err.DefaultIfEmpty(0).Max()));
            max = Math.Ceiling(max);

            using var gridPen = new Pen(Color.FromArgb(230, 230, 230));
            using var font = new Font("Segoe UI", 8f);
            for (int i = 0; i <= 4; i++)
            {
                float y = r.Bottom - r.Height * i / 4f;
                g.DrawLine(gridPen, r.Left, y, r.Right, y);
                g.DrawString((max * i / 4).ToString("0.#"), font, Brushes.Gray, 2, y - 7);
            }
            g.DrawString("-120 s", font, Brushes.Gray, r.Left, r.Bottom + 3);
            g.DrawString("now", font, Brushes.Gray, r.Right - 22, r.Bottom + 3);

            Draw(g, r, _req, max, Color.FromArgb(0x2B, 0x6C, 0xC4), true);
            Draw(g, r, _err, max, Color.FromArgb(0xD0, 0x3B, 0x3B), false);
        }

        private static void Draw(Graphics g, Rectangle r, double[] data, double max, Color c, bool fill)
        {
            if (data.Length < 2) return;
            const int slots = 120;
            var pts = new PointF[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                float x = r.Right - r.Width * (data.Length - 1 - i) / (float)(slots - 1);
                float y = r.Bottom - (float)(data[i] / max) * r.Height;
                pts[i] = new PointF(x, y);
            }
            if (fill)
            {
                var poly = pts.Append(new PointF(pts[^1].X, r.Bottom)).Append(new PointF(pts[0].X, r.Bottom)).ToArray();
                using var b = new SolidBrush(Color.FromArgb(40, c));
                g.FillPolygon(b, poly);
            }
            using var pen = new Pen(c, 1.8f);
            g.DrawLines(pen, pts);
        }
    }
}
