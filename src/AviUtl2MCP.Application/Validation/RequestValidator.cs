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

    public static void ValidateReadInput(CommonInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        switch (input)
        {
            case GetTimelineInput timeline:
                ValidatePageInput(timeline);
                ValidateOptionalSceneLayerFrameRanges(
                    timeline.SceneId,
                    timeline.LayerStart,
                    timeline.LayerEnd,
                    timeline.StartFrame,
                    timeline.EndFrame);
                if (!Enum.IsDefined(timeline.Detail))
                {
                    throw new ArgumentOutOfRangeException(nameof(input), "Timeline detail is invalid.");
                }
                break;
            case FindObjectsInput find:
                ValidatePageInput(find);
                ValidateOptionalSceneLayerFrameRanges(
                    find.SceneId,
                    find.LayerStart,
                    find.LayerEnd,
                    find.StartFrame,
                    find.EndFrame);
                ValidateOptionalString(find.NameContains, nameof(find.NameContains), 4096);
                ValidateOptionalString(find.EffectName, nameof(find.EffectName), 4096);
                ValidateOptionalString(find.MediaPath, nameof(find.MediaPath), MAX_PATH_LENGTH);
                break;
            case GetObjectInput getObject:
                ValidateCommonInput(getObject);
                ValidateLocator(getObject.Locator);
                break;
            case ListEffectsInput effects:
                ValidatePageInput(effects);
                ValidateOptionalString(effects.NameContains, nameof(effects.NameContains), 4096);
                if (effects.Category.HasValue && !Enum.IsDefined(effects.Category.Value))
                {
                    throw new ArgumentOutOfRangeException(nameof(input), "Effect category is invalid.");
                }
                break;
            case ListEffectItemsInput items:
                ValidateCommonInput(items);
                ArgumentNullException.ThrowIfNull(items.Effect);
                ValidateString(items.Effect.Name, nameof(items.Effect.Name), 4096);
                break;
            default:
                ValidateCommonInput(input);
                break;
        }
    }

    public static void ValidateMutationInput(MutationInput input)
    {
        ValidateCommonInput(input);
        _ = new Revision(input.ExpectedRevision.Value);
    }

    public static void ValidateSaveProjectInput(SaveProjectInput input)
    {
        ValidateCommonInput(input);
        _ = new Revision(input.ExpectedRevision.Value);
    }

    public static void ValidateEditInput(MutationInput input)
    {
        ValidateMutationInput(input);
        switch (input)
        {
            case CreateObjectInput create:
                ArgumentNullException.ThrowIfNull(create.Effect);
                ValidateString(create.Effect.Name, nameof(create.Effect), 4096);
                ValidatePlacement(create.Placement);
                ValidateOptionalObjectName(create.Name, nameof(create.Name));
                if (create.Items is not null)
                {
                    ValidateCollectionCount(create.Items, nameof(create.Items), 0, 1000);
                    foreach (EffectItemAssignment item in create.Items)
                    {
                        ValidateString(item.Name, nameof(item.Name), 4096);
                        ValidateEffectItemValue(item.Value);
                    }
                }
                break;
            case CreateMediaObjectInput media:
                ValidatePlacement(media.Placement);
                ValidateOptionalObjectName(media.Name, nameof(media.Name));
                if (!Path.IsPathFullyQualified(media.MediaPath))
                {
                    throw new ArgumentException("Media path must be absolute.", nameof(input));
                }
                string mediaPath = NormalizePath(media.MediaPath);
                if (!File.Exists(mediaPath)
                    || (File.GetAttributes(mediaPath) & FileAttributes.Directory) != 0)
                {
                    throw new ArgumentException("Media path must identify an existing regular file.", nameof(input));
                }
                break;
            case CreateAliasObjectInput alias:
                ValidatePlacement(alias.Placement);
                ValidateOptionalObjectName(alias.Name, nameof(alias.Name));
                if (alias.Alias.Contains('\0') || Encoding.UTF8.GetByteCount(alias.Alias) > 1024 * 1024
                    || !alias.Alias.Contains("[Object]", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Alias must contain Object data within the 1 MiB limit.", nameof(input));
                }
                break;
            case MoveObjectInput move:
                ValidateLocator(move.Locator);
                ArgumentNullException.ThrowIfNull(move.Placement);
                ArgumentOutOfRangeException.ThrowIfNegative(move.Placement.SceneId);
                ArgumentOutOfRangeException.ThrowIfLessThan(move.Placement.Layer, 1);
                ArgumentOutOfRangeException.ThrowIfLessThan(move.Placement.StartFrame, 1);
                break;
            case DeleteObjectInput delete:
                ValidateLocator(delete.Locator);
                break;
            case SetObjectNameInput setName:
                ValidateLocator(setName.Locator);
                ValidateOptionalObjectName(setName.Name, nameof(setName.Name), isRequired: true);
                break;
            case SetEffectItemInput setItem:
                ValidateLocator(setItem.Locator);
                ArgumentNullException.ThrowIfNull(setItem.Effect);
                ValidateString(setItem.Effect.Name, nameof(setItem.Effect.Name), 4096);
                ArgumentOutOfRangeException.ThrowIfNegative(setItem.Effect.Occurrence);
                ValidateString(setItem.ItemName, nameof(setItem.ItemName), 4096);
                ValidateEffectItemValue(setItem.Value);
                break;
            case SetEffectStateInput setState:
                ValidateLocator(setState.Locator);
                ArgumentNullException.ThrowIfNull(setState.Effect);
                ValidateString(setState.Effect.Name, nameof(setState.Effect.Name), 4096);
                ArgumentOutOfRangeException.ThrowIfNegative(setState.Effect.Occurrence);
                if (!setState.IsEnabled.HasValue && !setState.IsLocked.HasValue)
                {
                    throw new ArgumentException("At least one effect state property is required.", nameof(input));
                }
                break;
            case SetLayerInput setLayer:
                if (setLayer.SceneId.HasValue)
                {
                    ArgumentOutOfRangeException.ThrowIfNegative(setLayer.SceneId.Value);
                }
                ArgumentOutOfRangeException.ThrowIfLessThan(setLayer.Layer, 1);
                ValidateOptionalObjectName(setLayer.Name, nameof(setLayer.Name));
                if (setLayer.Name is null && !setLayer.IsVisible.HasValue && !setLayer.IsLocked.HasValue)
                {
                    throw new ArgumentException("At least one layer property is required.", nameof(input));
                }
                break;
            case ExecuteBatchInput batch:
                ValidateBatchInput(batch);
                break;
        }
    }

    public static void ValidateBatchInput(ExecuteBatchInput input)
    {
        ValidateMutationInput(input);
        ValidateCollectionCount(input.Operations, nameof(input.Operations), 1, 100);
        HashSet<string> operationIds = new(StringComparer.Ordinal);
        foreach (BatchOperation operation in input.Operations)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ValidateString(operation.ClientOperationId, nameof(operation.ClientOperationId), 128, 128);
            if (!operationIds.Add(operation.ClientOperationId))
            {
                throw new ArgumentException(
                    "Batch client operation IDs must be unique.",
                    nameof(input));
            }
            ValidateEditInput(CreateBatchValidationInput(input, operation));
        }
    }

    public static void ValidatePsdMutationInput(MutationInput input)
    {
        ValidateMutationInput(input);
        switch (input)
        {
            case PsdCreateInput create:
                ValidatePlacement(create.Placement);
                ValidateExistingFile(create.PsdPath, nameof(create.PsdPath), ".psd", ".psb");
                ValidateOptionalObjectName(create.Name, nameof(create.Name));
                break;
            case PsdSetupInput setup:
                if (setup.SceneId.HasValue)
                {
                    ArgumentOutOfRangeException.ThrowIfNegative(setup.SceneId.Value);
                }
                if (setup.PreferredLayer.HasValue)
                {
                    ArgumentOutOfRangeException.ThrowIfLessThan(setup.PreferredLayer.Value, 1);
                }
                if (setup.PreferredFrame.HasValue)
                {
                    ArgumentOutOfRangeException.ThrowIfLessThan(setup.PreferredFrame.Value, 1);
                }
                break;
            case PsdSetCharacterInput character:
                ValidateLocator(character.Locator);
                ValidatePsdCharacterId(character.CharacterId);
                break;
            case PsdSetLayerStateInput layerState:
                ValidateLocator(layerState.Locator);
                ValidatePsdLayerState(layerState.LayerState);
                break;
            case PsdCreateVoiceInput voice:
                ValidatePlacement(voice.Placement);
                ValidatePsdCharacterId(voice.CharacterId);
                if (voice.PsdLocator is not null)
                {
                    ValidateLocator(voice.PsdLocator);
                }
                string audioPath = ValidateExistingFile(
                    voice.AudioPath,
                    nameof(voice.AudioPath),
                    ".wav");
                string textPath = voice.TextPath is null
                    ? Path.ChangeExtension(audioPath, ".txt")
                    : ValidateExistingFile(voice.TextPath, nameof(voice.TextPath), ".txt");
                if (voice.TextPath is null)
                {
                    textPath = ValidateExistingFile(textPath, nameof(voice.TextPath), ".txt");
                }
                ValidateCompanionBasename(audioPath, textPath, nameof(voice.TextPath));
                FileInfo textFile = new(textPath);
                if (textFile.Length > MAX_TOOL_STRING_UTF8_BYTES)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(input),
                        "Voice text exceeds the 64 KiB contract limit.");
                }
                if (voice.LabPath is not null)
                {
                    string labPath = ValidateExistingFile(
                        voice.LabPath,
                        nameof(voice.LabPath),
                        ".lab");
                    ValidateCompanionBasename(audioPath, labPath, nameof(voice.LabPath));
                }
                break;
        }
    }

    public static void ValidatePsdValidateInput(PsdValidateInput input)
    {
        ValidateCommonInput(input);
        if (!Enum.IsDefined(input.Scope))
        {
            throw new ArgumentOutOfRangeException(nameof(input));
        }
        if (input.Scope == PsdValidationScope.SingleObject && input.Locator is null)
        {
            throw new ArgumentException(
                "locator is required when PSD validation scope is object.",
                nameof(input));
        }
        if (input.Locator is not null)
        {
            ValidateLocator(input.Locator);
        }
        if (input.Checks is not null)
        {
            ValidateCollectionCount(input.Checks, nameof(input.Checks), 1, 5);
            if (input.Checks.Any(check => !Enum.IsDefined(check))
                || input.Checks.Distinct().Count() != input.Checks.Count)
            {
                throw new ArgumentException(
                    "PSD validation checks must be known and unique.",
                    nameof(input));
            }
        }
    }

    public static void ValidateCursorInput(SetCursorInput input)
    {
        ValidateCommonInput(input);
        if (input.SceneId.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(input.SceneId.Value);
        }
        if (input.Frame.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(input.Frame.Value, 1);
        }
        if (input.DisplayFrame.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(input.DisplayFrame.Value, 1);
        }
        if (input.Selection is not null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(input.Selection.StartFrame, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(
                input.Selection.EndFrame,
                input.Selection.StartFrame);
        }
        if (!input.Frame.HasValue && !input.DisplayFrame.HasValue && input.Selection is null)
        {
            throw new ArgumentException("At least one cursor property is required.", nameof(input));
        }
        if (input.ExpectedViewRevision is not null)
        {
            _ = new Revision(input.ExpectedViewRevision.Value.Value);
        }
    }

    public static void ValidatePreviewInput(RenderPreviewInput input)
    {
        ValidateCommonInput(input);
        if (input.SceneId.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(input.SceneId.Value);
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Frame, 1);
        if (input.MaxWidth.HasValue != input.MaxHeight.HasValue)
        {
            throw new ArgumentException(
                "maxWidth and maxHeight must be specified together.",
                nameof(input));
        }
        if (input.MaxWidth.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(input.MaxWidth.Value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(input.MaxWidth.Value, 4096);
            ArgumentOutOfRangeException.ThrowIfLessThan(input.MaxHeight!.Value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(input.MaxHeight.Value, 4096);
        }
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
        if (locator.Name.Contains('\0') || locator.Name.Length > 4096
            || Encoding.UTF8.GetByteCount(locator.Name) > MAX_TOOL_STRING_UTF8_BYTES)
        {
            throw new ArgumentOutOfRangeException(nameof(locator), "Locator name exceeds the contract limit.");
        }
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

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch (PathTooLongException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(path),
                path.Length,
                "Path is too long.");
        }
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

    private static void ValidateOptionalSceneLayerFrameRanges(
        int? sceneId,
        int? layerStart,
        int? layerEnd,
        int? startFrame,
        int? endFrame)
    {
        if (sceneId.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sceneId.Value);
        }
        if (layerStart.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(layerStart.Value, 1);
        }
        if (layerEnd.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(layerEnd.Value, 1);
        }
        if (layerStart.HasValue && layerEnd.HasValue && layerStart > layerEnd)
        {
            throw new ArgumentException("Layer start must not exceed layer end.");
        }
        if (startFrame.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(startFrame.Value, 1);
        }
        if (endFrame.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(endFrame.Value, 1);
        }
        if (startFrame.HasValue && endFrame.HasValue && startFrame > endFrame)
        {
            throw new ArgumentException("Start frame must not exceed end frame.");
        }
    }

    private static void ValidateOptionalString(string? value, string parameterName, int maxCharacters)
    {
        if (value is not null)
        {
            ValidateString(value, parameterName, maxCharacters);
        }
    }

    private static void ValidateOptionalObjectName(
        string? value,
        string parameterName,
        bool isRequired = false)
    {
        if (value is null)
        {
            if (isRequired)
            {
                throw new ArgumentNullException(parameterName);
            }
            return;
        }
        if (value.Contains('\0') || value.Length > 4096
            || Encoding.UTF8.GetByteCount(value) > MAX_TOOL_STRING_UTF8_BYTES)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Object name exceeds the contract limit.");
        }
    }

    private static string ValidateExistingFile(
        string path,
        string parameterName,
        params string[] allowedExtensions)
    {
        string normalized = NormalizePath(path);
        if (!allowedExtensions.Contains(
                Path.GetExtension(normalized),
                StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "File extension is not supported for this PSD operation.",
                parameterName);
        }
        if (!File.Exists(normalized)
            || (File.GetAttributes(normalized) & FileAttributes.Directory) != 0)
        {
            throw new ArgumentException(
                "Path must identify an existing regular file.",
                parameterName);
        }
        return normalized;
    }

    private static void ValidateCompanionBasename(
        string audioPath,
        string companionPath,
        string parameterName)
    {
        bool hasSameDirectory = string.Equals(
            Path.GetDirectoryName(audioPath),
            Path.GetDirectoryName(companionPath),
            StringComparison.OrdinalIgnoreCase);
        bool hasSameStem = string.Equals(
            Path.GetFileNameWithoutExtension(audioPath),
            Path.GetFileNameWithoutExtension(companionPath),
            StringComparison.OrdinalIgnoreCase);
        if (!hasSameDirectory || !hasSameStem)
        {
            throw new ArgumentException(
                "Voice companion files must have the same directory and basename.",
                parameterName);
        }
    }

    private static void ValidatePsdCharacterId(string characterId)
    {
        if (string.IsNullOrEmpty(characterId)
            || characterId.Contains('\0')
            || characterId.Contains('\r')
            || characterId.Contains('\n')
            || characterId.EnumerateRunes().Count() > 256)
        {
            throw new ArgumentException(
                "PSD character ID must be a single line of 1 to 256 Unicode characters.",
                nameof(characterId));
        }
    }

    private static void ValidatePsdLayerState(string layerState)
    {
        if (string.IsNullOrEmpty(layerState)
            || layerState.Contains('\0')
            || layerState.Contains('\r')
            || layerState.Contains('\n')
            || Encoding.UTF8.GetByteCount(layerState) > MAX_TOOL_STRING_UTF8_BYTES
            || (layerState != "L.0"
                && !layerState.Contains("v0.", StringComparison.Ordinal)
                && !layerState.Contains("v1.", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "PSD layer state is not a supported canonical value.",
                nameof(layerState));
        }
    }

    private static MutationInput CreateBatchValidationInput(
        ExecuteBatchInput input,
        BatchOperation operation) => operation switch
    {
        BatchCreateObject value => new CreateObjectInput
        {
            InstanceId = input.InstanceId,
            TimeoutMs = input.TimeoutMs,
            ExpectedRevision = input.ExpectedRevision,
            DryRun = input.DryRun,
            Effect = value.Args.Effect,
            Placement = value.Args.Placement,
            Name = value.Args.Name,
            Items = value.Args.Items,
        },
        BatchCreateMediaObject value => new CreateMediaObjectInput
        {
            InstanceId = input.InstanceId,
            TimeoutMs = input.TimeoutMs,
            ExpectedRevision = input.ExpectedRevision,
            DryRun = input.DryRun,
            MediaPath = value.Args.MediaPath,
            Placement = value.Args.Placement,
            Name = value.Args.Name,
        },
        BatchCreateAliasObject value => new CreateAliasObjectInput
        {
            InstanceId = input.InstanceId,
            TimeoutMs = input.TimeoutMs,
            ExpectedRevision = input.ExpectedRevision,
            DryRun = input.DryRun,
            Alias = value.Args.Alias,
            Placement = value.Args.Placement,
            Name = value.Args.Name,
        },
        BatchMoveObject value => new MoveObjectInput
        {
            InstanceId = input.InstanceId,
            TimeoutMs = input.TimeoutMs,
            ExpectedRevision = input.ExpectedRevision,
            DryRun = input.DryRun,
            Locator = value.Args.Locator,
            Placement = value.Args.Placement,
        },
        BatchDeleteObject value => new DeleteObjectInput
        {
            InstanceId = input.InstanceId,
            TimeoutMs = input.TimeoutMs,
            ExpectedRevision = input.ExpectedRevision,
            DryRun = input.DryRun,
            Locator = value.Args.Locator,
        },
        BatchSetObjectName value => new SetObjectNameInput
        {
            InstanceId = input.InstanceId,
            TimeoutMs = input.TimeoutMs,
            ExpectedRevision = input.ExpectedRevision,
            DryRun = input.DryRun,
            Locator = value.Args.Locator,
            Name = value.Args.Name,
        },
        BatchSetEffectItem value => new SetEffectItemInput
        {
            InstanceId = input.InstanceId,
            TimeoutMs = input.TimeoutMs,
            ExpectedRevision = input.ExpectedRevision,
            DryRun = input.DryRun,
            Locator = value.Args.Locator,
            Effect = value.Args.Effect,
            ItemName = value.Args.ItemName,
            Value = value.Args.Value,
        },
        BatchSetEffectState value => new SetEffectStateInput
        {
            InstanceId = input.InstanceId,
            TimeoutMs = input.TimeoutMs,
            ExpectedRevision = input.ExpectedRevision,
            DryRun = input.DryRun,
            Locator = value.Args.Locator,
            Effect = value.Args.Effect,
            IsEnabled = value.Args.IsEnabled,
            IsLocked = value.Args.IsLocked,
        },
        BatchSetLayer value => new SetLayerInput
        {
            InstanceId = input.InstanceId,
            TimeoutMs = input.TimeoutMs,
            ExpectedRevision = input.ExpectedRevision,
            DryRun = input.DryRun,
            SceneId = value.Args.SceneId,
            Layer = value.Args.Layer,
            Name = value.Args.Name,
            IsVisible = value.Args.IsVisible,
            IsLocked = value.Args.IsLocked,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(operation), "Batch operation is unsupported."),
    };

    private static void ValidateEffectItemValue(System.Text.Json.JsonElement value)
    {
        if (value.ValueKind is System.Text.Json.JsonValueKind.True
            or System.Text.Json.JsonValueKind.False)
        {
            return;
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Number
            && value.TryGetDouble(out double number)
            && double.IsFinite(number))
        {
            return;
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            string text = value.GetString()!;
            if (!text.Contains('\0') && Encoding.UTF8.GetByteCount(text) <= MAX_TOOL_STRING_UTF8_BYTES)
            {
                return;
            }
        }
        throw new ArgumentException("Effect item value must be a bounded boolean, number, or string.", nameof(value));
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
