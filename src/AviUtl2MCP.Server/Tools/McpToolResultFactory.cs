using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Serialization;
using ModelContextProtocol.Protocol;

namespace AviUtl2MCP.Server.Tools;

internal static class McpToolResultFactory
{
    public static CallToolResult Create<TData>(ToolEnvelope<TData> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        string json = ContractJsonSerializer.SerializeContract(envelope);
        using JsonDocument document = JsonDocument.Parse(json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }],
            StructuredContent = document.RootElement.Clone(),
            IsError = !envelope.Ok,
        };
    }
}
