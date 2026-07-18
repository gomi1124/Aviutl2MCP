using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Validation;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class RequestValidatorTests
{
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
}
