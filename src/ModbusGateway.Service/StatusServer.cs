using System.Net;
using System.Text;
using System.Text.Json;
using ModbusGateway.Core;

namespace ModbusGateway.Service;

/// <summary>Read-only JSON status on http://127.0.0.1:{port}/status for the tray app.</summary>
public sealed class StatusServer
{
    private readonly int _port;
    private readonly Func<GatewayStatus> _snapshot;
    private readonly GatewayLog _log;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public StatusServer(int port, Func<GatewayStatus> snapshot, GatewayLog log)
    {
        _port = port; _snapshot = snapshot; _log = log;
    }

    public async Task Run(CancellationToken ct)
    {
        using var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{_port}/");
        http.Prefixes.Add($"http://localhost:{_port}/");
        try { http.Start(); }
        catch (Exception ex) { _log.Error($"Status endpoint on port {_port} failed: {ex.Message}"); return; }
        using var reg = ct.Register(() => { try { http.Stop(); } catch { } });

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await http.GetContextAsync(); }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.Warn("Status endpoint: " + ex.Message); continue; }

            try
            {
                var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
                byte[] body; string type = "application/json";
                if (path is "" or "/status") body = JsonSerializer.SerializeToUtf8Bytes(_snapshot(), Json);
                else { ctx.Response.StatusCode = 404; body = Encoding.UTF8.GetBytes("{\"error\":\"not found\"}"); }
                ctx.Response.ContentType = type;
                ctx.Response.Headers["Cache-Control"] = "no-store";
                await ctx.Response.OutputStream.WriteAsync(body, ct);
            }
            catch { /* client went away */ }
            finally { try { ctx.Response.Close(); } catch { } }
        }
    }
}
