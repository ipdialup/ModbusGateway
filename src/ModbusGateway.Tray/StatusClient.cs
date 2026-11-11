using System.Net.Http.Json;
using System.Text.Json;
using ModbusGateway.Core;

namespace ModbusGateway.Tray;

public sealed class StatusClient : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public StatusClient(int port)
    {
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(2) };
    }

    /// <summary>Null when the service can't be reached.</summary>
    public async Task<GatewayStatus?> Get()
    {
        try { return await _http.GetFromJsonAsync<GatewayStatus>("status", Json); }
        catch { return null; }
    }

    public void Dispose() => _http.Dispose();
}
