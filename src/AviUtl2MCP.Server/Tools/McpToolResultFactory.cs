using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Serialization;
using ModelContextProtocol.Protocol;

namespace AviUtl2MCP.Server.Tools;

internal static class McpToolResultFactory
{
    public static CallToolResult Create<TData>(ToolEnvelope<TData> envelope)
        => Create(envelope, ReadOnlyMemory<byte>.Empty, null);

    public static CallToolResult Create<TData>(
        ToolEnvelope<TData> envelope,
        ReadOnlyMemory<byte> imageBytes,
        string? imageMimeType)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        string json = ContractJsonSerializer.SerializeContract(envelope);
        using JsonDocument document = JsonDocument.Parse(json);
        List<ContentBlock> content = [new TextContentBlock { Text = json }];
        if (envelope.Ok && !imageBytes.IsEmpty)
        {
            content.Add(ImageContentBlock.FromBytes(
                imageBytes,
                imageMimeType ?? "application/octet-stream"));
        }
        return new CallToolResult
        {
            Content = content,
            StructuredContent = document.RootElement.Clone(),
            IsError = !envelope.Ok,
        };
    }
}
