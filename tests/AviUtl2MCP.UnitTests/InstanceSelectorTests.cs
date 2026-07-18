using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Instances;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class InstanceSelectorTests
{
    [TestMethod]
    public async Task SelectInstanceAsyncPrefersTopLevelId()
    {
        // Arrange
        InstanceDescriptor first = CreateDescriptor(Guid.NewGuid());
        InstanceDescriptor second = CreateDescriptor(Guid.NewGuid());
        InstanceSelectionRequest request = new(second.InstanceId, [], first.InstanceId, [first, second]);

        // Act
        ApplicationResult<InstanceDescriptor> result = await new InstanceSelector()
            .SelectInstanceAsync(request, CancellationToken.None);

        // Assert
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(second.InstanceId, result.Value!.InstanceId);
    }

    [TestMethod]
    public async Task SelectInstanceAsyncRejectsAmbiguousCandidates()
    {
        // Arrange
        InstanceSelectionRequest request = new(
            null,
            [],
            null,
            [CreateDescriptor(Guid.NewGuid()), CreateDescriptor(Guid.NewGuid())]);

        // Act
        ApplicationResult<InstanceDescriptor> result = await new InstanceSelector()
            .SelectInstanceAsync(request, CancellationToken.None);

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("instance_ambiguous", result.Error!.Code);
    }

    [TestMethod]
    public async Task SelectInstanceAsyncRejectsMixedLocatorInstances()
    {
        // Arrange
        ObjectLocator first = CreateLocator(Guid.NewGuid());
        ObjectLocator second = CreateLocator(Guid.NewGuid());
        InstanceSelectionRequest request = new(null, [first, second], null, []);

        // Act
        ApplicationResult<InstanceDescriptor> result = await new InstanceSelector()
            .SelectInstanceAsync(request, CancellationToken.None);

        // Assert
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("invalid_argument", result.Error!.Code);
    }

    private static InstanceDescriptor CreateDescriptor(Guid instanceId)
    {
        return new InstanceDescriptor(instanceId, 1234, DateTimeOffset.UtcNow, "0.1.0", true);
    }

    private static ObjectLocator CreateLocator(Guid instanceId)
    {
        return new ObjectLocator(
            instanceId,
            Guid.NewGuid(),
            0,
            1,
            1,
            30,
            "object",
            new string('0', 64),
            new string('1', 64));
    }
}
