using System.Text.RegularExpressions;
using System.Text;
using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Validation;

public static partial class RequestValidator
{
    private const int MAX_PATH_LENGTH = 32767;
    private const int SHA256_HEX_LENGTH = 64;
    private const int MAX_TOOL_STRING_UTF8_BYTES = 64 * 1024;

    public static void ValidateCommonInput(CommonInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.InstanceId == Guid.Empty)
        {
            throw new ArgumentException("Instance ID must not be empty.", nameof(input));
        }

        if (input.TimeoutMs.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(input.TimeoutMs.Value, 100);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(input.TimeoutMs.Value, 120_000);
        }
    }

    public static void ValidatePageInput(PageInput input)
    {
        ValidateCommonInput(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.Limit, 1000);
        if (input.Cursor is not null)
        {
            ValidateString(input.Cursor, nameof(input.Cursor), 4096, 4096);
        }
    }

    public static void ValidateMutationInput(MutationInput input)
    {
        ValidateCommonInput(input);
        _ = new Revision(input.ExpectedRevision.Value);
    }

    public static void ValidateLocator(ObjectLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);

        if (locator.InstanceId == Guid.Empty)
        {
            throw new ArgumentException("Instance ID must not be empty.", nameof(locator));
        }

        if (locator.ProjectGeneration == Guid.Empty)
        {
            throw new ArgumentException("Project generation must not be empty.", nameof(locator));
        }

        ValidateSceneLayerFrames(locator.SceneId, locator.Layer, locator.StartFrame, locator.EndFrame);
        ValidateSha256(locator.AliasSha256, nameof(locator.AliasSha256));
        ValidateSha256(locator.EffectSignatureSha256, nameof(locator.EffectSignatureSha256));
    }

    public static void ValidatePlacement(Placement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        bool hasEndFrame = placement.EndFrame.HasValue;
        bool hasDuration = placement.DurationFrames.HasValue;
        if (hasEndFrame == hasDuration)
        {
            throw new ArgumentException("Exactly one of endFrame or durationFrames must be specified.", nameof(placement));
        }

        int endFrame = hasEndFrame
            ? placement.EndFrame!.Value
            : checked(placement.StartFrame + placement.DurationFrames!.Value - 1);
        ValidateSceneLayerFrames(placement.SceneId, placement.Layer, placement.StartFrame, endFrame);
    }

    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.Contains('\0'))
        {
            throw new ArgumentException("Path must not contain NUL.", nameof(path));
        }

        string normalizedPath = Path.GetFullPath(path);
        if (normalizedPath.Length > MAX_PATH_LENGTH)
        {
            throw new ArgumentOutOfRangeException(nameof(path), normalizedPath.Length, "Normalized path is too long.");
        }

        return normalizedPath;
    }

    public static void ValidateString(
        string value,
        string parameterName,
        int maxCharacters,
        int maxUtf8Bytes = MAX_TOOL_STRING_UTF8_BYTES)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxUtf8Bytes, 1);
        if (value.Contains('\0'))
        {
            throw new ArgumentException("String must not contain NUL.", parameterName);
        }

        if (value.Length > maxCharacters || Encoding.UTF8.GetByteCount(value) > maxUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, "String exceeds the contract limit.");
        }
    }

    public static void ValidateCollectionCount<T>(
        IReadOnlyCollection<T> values,
        string parameterName,
        int minimum,
        int maximum)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        ArgumentOutOfRangeException.ThrowIfNegative(minimum);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, minimum);
        if (values.Count < minimum || values.Count > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, values.Count, "Collection count is outside the contract limit.");
        }
    }

    private static void ValidateSceneLayerFrames(int sceneId, int layer, int startFrame, int endFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sceneId);
        ArgumentOutOfRangeException.ThrowIfLessThan(layer, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(startFrame, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(endFrame, startFrame);
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != SHA256_HEX_LENGTH || !LowerHexRegex().IsMatch(value))
        {
            throw new ArgumentException("SHA-256 must be 64 lowercase hexadecimal characters.", parameterName);
        }
    }

    [GeneratedRegex("^[0-9a-f]+$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHexRegex();
}
