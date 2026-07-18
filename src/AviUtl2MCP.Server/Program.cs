using AviUtl2MCP.Server;
using AviUtl2MCP.Server.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Trace);
builder.Logging.AddProvider(JsonLineLoggerProvider.CreateDefault());
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport();

await builder.Build().RunAsync().ConfigureAwait(false);

namespace AviUtl2MCP.Server
{
    public sealed class ServerMarker;
}
