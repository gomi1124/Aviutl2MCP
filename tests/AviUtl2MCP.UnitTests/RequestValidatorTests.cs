using System.Text.Json;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Validation;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class RequestValidatorTests
{
    [TestMethod]
    public void ValidateCommonInputRejectsTimeoutOutsideContract()
    {
        // Arrange
        GetStatusInput input = new() { TimeoutMs = 99 };

        // Act
        Action action = () => RequestValidator.ValidateCommonInput(input);

        // Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(action);
    }

    [TestMethod]
    public void ValidateStringUsesUtf8ByteLimit()
    {
        // Arrange
        const string value = "日本語";

        // Act
        Action action = () => RequestValidator.ValidateString(value, nameof(value), 3, 8);

        // Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(action);
    }

    [TestMethod]
    public void ValidateCollectionCountRejectsOversizedBatch()
    {
        // Arrange
        IReadOnlyList<int> values = [1, 2, 3];

        // Act
        Action action = () => RequestValidator.ValidateCollectionCount(values, nameof(values), 1, 2);

        // Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(action);
    }

    [TestMethod]
    public void ValidatePlacementAcceptsOneBasedDuration()
    {
        // Arrange
        Placement placement = new(0, 1, 10, DurationFrames: 5);

        // Act
        Action action = () => RequestValidator.ValidatePlacement(placement);

        // Assert
        action();
    }

    [TestMethod]
    public void ValidatePlacementRejectsAmbiguousRange()
    {
        // Arrange
        Placement placement = new(0, 1, 10, EndFrame: 20, DurationFrames: 11);

        // Act
        Action action = () => RequestValidator.ValidatePlacement(placement);

        // Assert
        Assert.ThrowsExactly<ArgumentException>(action);
    }

    [TestMethod]
    public void ValidateLocatorRejectsUppercaseHash()
    {
        // Arrange
        ObjectLocator locator = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            1,
            30,
            "object",
            new string('A', 64),
            new string('0', 64));

        // Act
        Action action = () => RequestValidator.ValidateLocator(locator);

        // Assert
        Assert.ThrowsExactly<ArgumentException>(action);
    }

    [TestMethod]
    public void ValidateReadInputRejectsInvertedTimelineRange()
    {
        // Arrange
        GetTimelineInput input = new() { LayerStart = 3, LayerEnd = 2 };

        // Act
        Action action = () => RequestValidator.ValidateReadInput(input);

        // Assert
        Assert.ThrowsExactly<ArgumentException>(action);
    }

    [TestMethod]
    public void ValidateReadInputRejectsEmptyEffectSelector()
    {
        // Arrange
        ListEffectItemsInput input = new() { Effect = new EffectDefinitionSelector(string.Empty) };

        // Act
        Action action = () => RequestValidator.ValidateReadInput(input);

        // Assert
        Assert.ThrowsExactly<ArgumentException>(action);
    }

    [TestMethod]
    public void ValidateEditInputRejectsAliasWithoutObjectSection()
    {
        // Arrange
        CreateAliasObjectInput input = new()
        {
            ExpectedRevision = new Revision("r1"),
            Alias = "[Settings]\r\nvalue=1\r\n",
            Placement = new Placement(0, 1, 1, DurationFrames: 1),
        };

        // Act
        Action action = () => RequestValidator.ValidateEditInput(input);

        // Assert
        Assert.ThrowsExactly<ArgumentException>(action);
    }

    [TestMethod]
    public void ValidateEditInputRejectsRelativeMediaPath()
    {
        // Arrange
        CreateMediaObjectInput input = new()
        {
            ExpectedRevision = new Revision("r1"),
            MediaPath = "voice.wav",
            Placement = new Placement(0, 1, 1, DurationFrames: 1),
        };

        // Act
        Action action = () => RequestValidator.ValidateEditInput(input);

        // Assert
        Assert.ThrowsExactly<ArgumentException>(action);
    }

    [TestMethod]
    public void ValidateEditInputAllowsClearingObjectName()
    {
        // Arrange
        SetObjectNameInput input = new()
        {
            ExpectedRevision = new Revision("r1"),
            Locator = CreateValidLocator(),
            Name = string.Empty,
        };

        // Act
        Action action = () => RequestValidator.ValidateEditInput(input);

        // Assert
        action();
    }

    [TestMethod]
    public void ValidateEditInputRejectsEffectStateWithoutProperties()
    {
        // Arrange
        SetEffectStateInput input = new()
        {
            ExpectedRevision = new Revision("r1"),
            Locator = CreateValidLocator(),
            Effect = new EffectInstanceSelector("Text"),
        };

        // Act
        Action action = () => RequestValidator.ValidateEditInput(input);

        // Assert
        Assert.ThrowsExactly<ArgumentException>(action);
    }

    [TestMethod]
    public void ValidateCursorInputRejectsInvertedSelection()
    {
        // Arrange
        SetCursorInput input = new() { Selection = new Selection(10, 9) };

        // Act
        Action action = () => RequestValidator.ValidateCursorInput(input);

        // Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(action);
    }

    [TestMethod]
    public void ValidateEditInputAcceptsSupportedEffectItemValue()
    {
        // Arrange
        using JsonDocument document = JsonDocument.Parse("42");
        SetEffectItemInput input = new()
        {
            ExpectedRevision = new Revision("r1"),
            Locator = CreateValidLocator(),
            Effect = new EffectInstanceSelector("Audio File"),
            ItemName = "Volume",
            Value = document.RootElement.Clone(),
        };

        // Act
        Action action = () => RequestValidator.ValidateEditInput(input);

        // Assert
        action();
    }

    [TestMethod]
    public void ValidateBatchInputRejectsDuplicateOperationIds()
    {
        // Arrange
        ExecuteBatchInput input = new()
        {
            ExpectedRevision = new Revision("r1"),
            Operations =
            [
                new BatchDeleteObject("duplicate", new DeleteObjectArgs(CreateValidLocator())),
                new BatchSetObjectName(
                    "duplicate",
                    new SetObjectNameArgs(CreateValidLocator(), "renamed")),
            ],
        };

        // Act
        Action action = () => RequestValidator.ValidateBatchInput(input);

        // Assert
        Assert.ThrowsExactly<ArgumentException>(action);
    }

    [TestMethod]
    public void ValidateBatchInputAcceptsSupportedDiscriminators()
    {
        // Arrange
        ObjectLocator locator = CreateValidLocator();
        using JsonDocument document = JsonDocument.Parse("42");
        ExecuteBatchInput input = new()
        {
            ExpectedRevision = new Revision("r1"),
            DryRun = true,
            Operations =
            [
                new BatchCreateObject(
                    "create",
                    new CreateObjectArgs(
                        new EffectDefinitionSelector("Text"),
                        new Placement(0, 2, 31, DurationFrames: 30))),
                new BatchMoveObject(
                    "move",
                    new MoveObjectArgs(locator, new MovePlacement(0, 3, 61))),
                new BatchSetEffectItem(
                    "item",
                    new SetEffectItemArgs(
                        locator,
                        new EffectInstanceSelector("Audio File"),
                        "Volume",
                        document.RootElement.Clone())),
                new BatchSetLayer(
                    "layer",
                    new SetLayerArgs(2) { IsVisible = false }),
            ],
        };

        // Act
        Action action = () => RequestValidator.ValidateBatchInput(input);

        // Assert
        action();
    }

    [TestMethod]
    public void ValidatePreviewInputAcceptsPairedDimensions()
    {
        // Arrange
        RenderPreviewInput input = new()
        {
            Frame = 1,
            MaxWidth = 1920,
            MaxHeight = 1080,
        };

        // Act
        Action action = () => RequestValidator.ValidatePreviewInput(input);

        // Assert
        action();
    }

    [TestMethod]
    public void ValidatePreviewInputRejectsUnpairedDimensions()
    {
        // Arrange
        RenderPreviewInput input = new() { Frame = 1, MaxWidth = 1920 };

        // Act
        Action action = () => RequestValidator.ValidatePreviewInput(input);

        // Assert
        Assert.ThrowsExactly<ArgumentException>(action);
    }

    [TestMethod]
    public void ValidatePreviewInputRejectsInvalidFrameAndDimensions()
    {
        // Arrange
        RenderPreviewInput invalidFrame = new() { Frame = 0 };
        RenderPreviewInput invalidDimensions = new()
        {
            Frame = 1,
            MaxWidth = 4097,
            MaxHeight = 1080,
        };

        // Act
        Action frameAction = () => RequestValidator.ValidatePreviewInput(invalidFrame);
        Action dimensionAction = () => RequestValidator.ValidatePreviewInput(invalidDimensions);

        // Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(frameAction);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(dimensionAction);
    }

    [TestMethod]
    public void ValidatePsdMutationInputAcceptsSameBasenameVoiceCompanions()
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), $"AviUtl2MCP-{Guid.CreateVersion7():D}");
        Directory.CreateDirectory(directory);
        try
        {
            string audioPath = Path.Combine(directory, "alice.wav");
            File.WriteAllBytes(audioPath, [1]);
            File.WriteAllText(Path.Combine(directory, "alice.txt"), "hello");
            File.WriteAllText(Path.Combine(directory, "alice.lab"), "0 1000000 a");
            PsdCreateVoiceInput input = new()
            {
                ExpectedRevision = new Revision("r1"),
                AudioPath = audioPath,
                CharacterId = "alice",
                Placement = new Placement(0, 1, 1, DurationFrames: 30),
            };

            // Act
            Action action = () => RequestValidator.ValidatePsdMutationInput(input);

            // Assert
            action();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ValidatePsdMutationInputRejectsMismatchedTextBasename()
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), $"AviUtl2MCP-{Guid.CreateVersion7():D}");
        Directory.CreateDirectory(directory);
        try
        {
            string audioPath = Path.Combine(directory, "alice.wav");
            string textPath = Path.Combine(directory, "bob.txt");
            File.WriteAllBytes(audioPath, [1]);
            File.WriteAllText(textPath, "hello");
            PsdCreateVoiceInput input = new()
            {
                ExpectedRevision = new Revision("r1"),
                AudioPath = audioPath,
                TextPath = textPath,
                CharacterId = "alice",
                Placement = new Placement(0, 1, 1, DurationFrames: 30),
            };

            // Act
            Action action = () => RequestValidator.ValidatePsdMutationInput(input);

            // Assert
            Assert.ThrowsExactly<ArgumentException>(action);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ValidatePsdValidateInputEnforcesScopeLocatorContract()
    {
        // Arrange
        PsdValidateInput objectScope = new();
        PsdValidateInput sceneScope = new()
        {
            Scope = PsdValidationScope.Scene,
            Checks = [PsdValidationCheck.Setup, PsdValidationCheck.Subtitle],
        };

        // Act
        Action objectAction = () => RequestValidator.ValidatePsdValidateInput(objectScope);
        Action sceneAction = () => RequestValidator.ValidatePsdValidateInput(sceneScope);

        // Assert
        Assert.ThrowsExactly<ArgumentException>(objectAction);
        sceneAction();
    }

    private static ObjectLocator CreateValidLocator() => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        0,
        1,
        1,
        30,
        "object",
        new string('a', 64),
        new string('b', 64));
}
