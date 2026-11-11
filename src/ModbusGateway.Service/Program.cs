using ModbusGateway.Core;
using ModbusGateway.Service;

// Runs as the "ModbusGateway" Windows service, or in a console window when started by hand.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddWindowsService(o => o.ServiceName = "ModbusGateway");

var options = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
builder.Services.AddSingleton(options);
builder.Services.AddHostedService<GatewayWorker>();

builder.Build().Run();
