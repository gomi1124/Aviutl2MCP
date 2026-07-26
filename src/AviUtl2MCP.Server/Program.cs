using AviUtl2MCP.Application.Diagnostics;
using AviUtl2MCP.Application.Edits;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.Application.Paging;
using AviUtl2MCP.Application.Previews;
using AviUtl2MCP.Application.Psd;
using AviUtl2MCP.Application.Queries;
using AviUtl2MCP.Application.Requests;
using AviUtl2MCP.Application.Serialization;
using AviUtl2MCP.BridgeClient.Connections;
using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Gateways;
using AviUtl2MCP.Server;
using AviUtl2MCP.Server.Diagnostics;
using AviUtl2MCP.Server.Logging;
using AviUtl2MCP.Server.Prompts;
using AviUtl2MCP.Server.Resources;
using AviUtl2MCP.Server.Schema;
using AviUtl2MCP.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Trace);
builder.Logging.AddProvider(JsonLineLoggerProvider.CreateDefault());

ServerRuntimeIdentity runtimeIdentity = new();
builder.Services.AddSingleton(runtimeIdentity);
builder.Services.AddSingleton<RequestContextFactory>();
builder.Services.AddSingleton<IInstanceSelector, InstanceSelector>();
builder.Services.AddSingleton(_ => new InstanceDescriptorWatcher(
    ServerPathResolver.GetInstanceDescriptorDirectory()));
builder.Services.AddSingleton<IBridgeConnectionFactory>(services =>
    new BridgeConnectionFactory(
        services.GetRequiredService<ServerRuntimeIdentity>().ClientInstanceId,
        services.GetRequiredService<ServerRuntimeIdentity>().ServerVersion));
builder.Services.AddSingleton<BridgeConnectionRegistry>();
builder.Services.AddSingleton<ServerInstanceResolver>();
builder.Services.AddSingleton<IInstanceResolver>(services =>
    services.GetRequiredService<ServerInstanceResolver>());
builder.Services.AddSingleton<IBridgeDiagnosticsGateway, BridgeDiagnosticsGateway>();
builder.Services.AddSingleton<IAviUtlQueryGateway, BridgeQueryGateway>();
builder.Services.AddSingleton<IAviUtlEditGateway, BridgeEditGateway>();
builder.Services.AddSingleton<IAviUtlPreviewGateway, BridgePreviewGateway>();
builder.Services.AddSingleton<IAviUtlPsdGateway, BridgePsdGateway>();
builder.Services.AddSingleton<AviUtlEditService>();
builder.Services.AddSingleton<AviUtlPreviewService>();
builder.Services.AddSingleton<PsdService>();
builder.Services.AddSingleton(services => new PagingCursorCodec(
    services.GetRequiredService<ServerRuntimeIdentity>().CursorSigningKey.Span));
builder.Services.AddSingleton(services => new AviUtlQueryService(
    services.GetRequiredService<IInstanceResolver>(),
    services.GetRequiredService<IAviUtlQueryGateway>(),
    services.GetRequiredService<IBridgeDiagnosticsGateway>(),
    services.GetRequiredService<PagingCursorCodec>(),
    services.GetRequiredService<ServerRuntimeIdentity>().ServerEpoch));

builder.Services.AddSingleton(_ => new ServerJsonLogSource(
    JsonLineLoggerProvider.GetDefaultLogFilePath()));
builder.Services.AddSingleton(_ => new AviUtlLogSource(
    ServerPathResolver.GetAviUtlLogDirectory()));
builder.Services.AddSingleton<BridgeLogSource>();
builder.Services.AddSingleton<ILogSource>(services =>
    services.GetRequiredService<ServerJsonLogSource>());
builder.Services.AddSingleton<ILogSource>(services =>
    services.GetRequiredService<BridgeLogSource>());
builder.Services.AddSingleton<ILogSource>(services =>
    services.GetRequiredService<AviUtlLogSource>());
builder.Services.AddSingleton(services => new LogCursorCodec(
    services.GetRequiredService<ServerRuntimeIdentity>().CursorSigningKey.Span));
builder.Services.AddSingleton<LogQueryService>();
builder.Services.AddSingleton<IDiagnosticSmokeProbe, AviUtlDiagnosticSmokeProbe>();
builder.Services.AddSingleton<DiagnosticContextFactory>();
builder.Services.AddSingleton<LatestDiagnosticsStore>();
builder.Services.AddSingleton(services => new DiagnosticsService(
    services.GetRequiredService<DiagnosticContextFactory>(),
    DiagnosticsService.CreateDefaultRules()));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsUsingSchema<DiagnosticsToolSet>(
        ContractJsonSerializer.CreateSerializerOptions(),
        ToolSchemaOptions.Create())
    .WithToolsUsingSchema<ReadToolSet>(
        ContractJsonSerializer.CreateSerializerOptions(),
        ToolSchemaOptions.Create())
    .WithToolsUsingSchema<EditToolSet>(
        ContractJsonSerializer.CreateSerializerOptions(),
        ToolSchemaOptions.Create())
    .WithToolsUsingSchema<PsdToolSet>(
        ContractJsonSerializer.CreateSerializerOptions(),
        ToolSchemaOptions.Create())
    .WithPrompts<AviUtlPromptProvider>(ContractJsonSerializer.CreateSerializerOptions())
    .WithResources<AviUtlResourceSet>();

await builder.Build().RunAsync().ConfigureAwait(false);

namespace AviUtl2MCP.Server
{
    public sealed class ServerMarker;
}
