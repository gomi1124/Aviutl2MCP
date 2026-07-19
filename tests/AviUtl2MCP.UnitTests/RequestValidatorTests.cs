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
}
