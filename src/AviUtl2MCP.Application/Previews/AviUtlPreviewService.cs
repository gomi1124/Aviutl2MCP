using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.Application.Instances;
using AviUtl2MCP.Application.Requests;

namespace AviUtl2MCP.Application.Previews;

public sealed record PreviewExecutionResult(
    ApplicationResult<PreviewData> Result,
    Guid? InstanceId,
    Revision? Revision,
    Revision? ViewRevision,
    ReadOnlyMemory<byte> PngBytes);

public sealed class AviUtlPreviewService(
    IInstanceResolver instanceResolver,
    IAviUtlPreviewGateway previewGateway)
{
    private const int MAXIMUM_PNG_BYTES = 16 * 1024 * 1024;
    private static readonly byte[] PNG_SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private readonly IInstanceResolver _instanceResolver = instanceResolver
        ?? throw new ArgumentNullException(nameof(instanceResolver));
    private readonly IAviUtlPreviewGateway _previewGateway = previewGateway
        ?? throw new ArgumentNullException(nameof(previewGateway));

    public async ValueTask<PreviewExecutionResult> RenderPreviewAsync(
        RenderPreviewInput input,
        RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            ApplicationResult<InstanceDescriptor> selection = await _instanceResolver.ResolveAsync(
                input.InstanceId,
                [],
                context.CancellationToken).ConfigureAwait(false);
            if (!selection.IsSuccess)
            {
                return new PreviewExecutionResult(
                    ApplicationResult.Failure<PreviewData>(
                        selection.Error!,
                        warnings: selection.Warnings),
                    null,
                    null,
                    null,
                    ReadOnlyMemory<byte>.Empty);
            }
            InstanceDescriptor instance = selection.Value!;
            GatewayResponse<PreviewData> response = await _previewGateway.RenderPreviewAsync(
                new GatewayRequest<RenderPreviewInput>(
                    instance.InstanceId,
                    context.CorrelationId,
                    context.Deadline,
                    context.TimeoutMs,
                    null,
                    false,
                    input),
                context.CancellationToken).ConfigureAwait(false);
            if (!response.Ok)
            {
                GatewayError? gatewayError = response.Error;
                ApplicationError error = gatewayError is null
                    ? ApplicationErrors.CreateError(
                        "bridge_protocol_error",
                        "The Bridge returned a failed preview response without error details.",
                        true)
                    : ApplicationErrors.CreateError(
                        gatewayError.Code,
                        gatewayError.Message,
                        gatewayError.CanRetry,
                        ConvertDetails(gatewayError.Details));
                return new PreviewExecutionResult(
                    ApplicationResult.Failure<PreviewData>(error, warnings: response.Warnings),
                    response.InstanceId,
                    response.Revision,
                    response.ViewRevision,
                    ReadOnlyMemory<byte>.Empty);
            }
            if (response.Data is null)
            {
                return CreateProtocolFailure(response, "Preview metadata was omitted.");
            }
            string? validationError = ValidatePng(input, response.Data, response.Binary.Span);
            if (validationError is not null)
            {
                return CreateProtocolFailure(response, validationError);
            }
            return new PreviewExecutionResult(
                ApplicationResult.Success(response.Data, response.Warnings),
                response.InstanceId,
                response.Revision,
                response.ViewRevision,
                response.Binary);
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or IOException
            or InvalidDataException
            or JsonException
            or TimeoutException)
        {
            ApplicationError error = exception is OperationCanceledException or TimeoutException
                ? ApplicationErrors.CreateError("operation_timeout", "The preview request timed out.", true)
                : ApplicationErrors.CreateError(
                    exception is InvalidDataException or JsonException
                        ? "bridge_protocol_error"
                        : "bridge_not_connected",
                    exception.Message,
                    true);
            return new PreviewExecutionResult(
                ApplicationResult.Failure<PreviewData>(error),
                null,
                null,
                null,
                ReadOnlyMemory<byte>.Empty);
        }
    }

    private static PreviewExecutionResult CreateProtocolFailure(
        GatewayResponse<PreviewData> response,
        string message) => new(
            ApplicationResult.Failure<PreviewData>(ApplicationErrors.CreateError(
                "preview_invalid_png",
                message,
                true)),
            response.InstanceId,
            response.Revision,
            response.ViewRevision,
            ReadOnlyMemory<byte>.Empty);

    private static string? ValidatePng(
        RenderPreviewInput input,
        PreviewData data,
        ReadOnlySpan<byte> png)
    {
        int maximumWidth = input.MaxWidth ?? 1920;
        int maximumHeight = input.MaxHeight ?? 1080;
        if (data.MimeType != "image/png" || data.Frame != input.Frame
            || data.Width < 1 || data.Width > maximumWidth
            || data.Height < 1 || data.Height > maximumHeight
            || data.ByteLength != png.Length || png.Length > MAXIMUM_PNG_BYTES
            || png.Length < 26 || !png[..PNG_SIGNATURE.Length].SequenceEqual(PNG_SIGNATURE))
        {
            return "Preview metadata, dimensions, byte length, or PNG signature is invalid.";
        }
        uint pngWidth = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        uint pngHeight = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        byte expectedColorType = input.IncludeAlpha ? (byte)6 : (byte)2;
        if (pngWidth != (uint)data.Width || pngHeight != (uint)data.Height
            || png[25] != expectedColorType)
        {
            return "Preview IHDR does not match its metadata or alpha contract.";
        }
        string actualSha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        if (!string.Equals(actualSha256, data.Sha256, StringComparison.Ordinal))
        {
            return "Preview SHA-256 does not match its binary payload.";
        }
        return null;
    }

    private static Dictionary<string, JsonNode?> ConvertDetails(JsonElement details)
    {
        if (details.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, JsonNode?>();
        }
        return details.EnumerateObject().ToDictionary(
            property => property.Name,
            property => JsonNode.Parse(property.Value.GetRawText()),
            StringComparer.Ordinal);
    }
}
