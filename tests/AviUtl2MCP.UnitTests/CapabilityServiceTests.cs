using AviUtl2MCP.Application.Capabilities;
using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class CapabilityServiceTests
{
    [TestMethod]
    public void GetCapabilitiesReturnsAllVersionOneOperations()
    {
        // Arrange
        CapabilityEnvironment environment = CreateEnvironment(hasGcmzDrops: true);

        // Act
        CapabilitiesData data = CapabilityService.GetCapabilities(environment);

        // Assert
        Assert.HasCount(32, data.Operations);
        Assert.HasCount(32, data.Operations.Select(operation => operation.Name).Distinct(StringComparer.Ordinal).ToArray());
        Assert.IsTrue(data.Operations.All(operation => operation.Available));
        Assert.AreEqual(100, data.Limits.BatchOperations);
    }

    [TestMethod]
    [TestProperty("TestId", "app.psd-capability-isolation")]
    public void GetCapabilitiesIsolatesGcmzDropsFailure()
    {
        // Arrange
        CapabilityEnvironment environment = CreateEnvironment(hasGcmzDrops: false);

        // Act
        CapabilitiesData data = CapabilityService.GetCapabilities(environment);
        CapabilityOperation basicEdit = data.Operations.Single(operation => operation.Name == "aviutl_create_object");
        CapabilityOperation voice = data.Operations.Single(operation => operation.Name == "aviutl_psd_create_voice");

        // Assert
        Assert.IsTrue(basicEdit.Available);
        Assert.IsFalse(voice.Available);
        Assert.AreEqual("gcmzdrops_not_available", voice.Reason);
    }

    [TestMethod]
    public void GetCapabilitiesKeepsPsdValidationReadOnly()
    {
        // Arrange
        CapabilityEnvironment environment = CreateEnvironment(hasGcmzDrops: true, canEdit: false);

        // Act
        CapabilitiesData data = CapabilityService.GetCapabilities(environment);
        CapabilityOperation validation = data.Operations.Single(operation => operation.Name == "aviutl_psd_validate");
        CapabilityOperation setup = data.Operations.Single(operation => operation.Name == "aviutl_psd_setup");

        // Assert
        Assert.IsTrue(validation.Available);
        Assert.IsFalse(setup.Available);
        Assert.AreEqual("edit_not_available", setup.Reason);
    }

    [TestMethod]
    public void GetCapabilitiesRequiresNamedProjectForSave()
    {
        // Arrange
        CapabilityEnvironment environment = CreateEnvironment(
            hasGcmzDrops: true,
            isProjectSaved: false);

        // Act
        CapabilityOperation save = CapabilityService.GetCapabilities(environment)
            .Operations.Single(operation => operation.Name == "aviutl_save_project");

        // Assert
        Assert.IsFalse(save.Available);
        Assert.AreEqual("project_path_required", save.Reason);
    }

    private static CapabilityEnvironment CreateEnvironment(
        bool hasGcmzDrops,
        bool canEdit = true,
        bool isProjectSaved = true)
    {
        CapabilityVersions versions = new(
            "0.1.0",
            "1.0.0",
            "1.0",
            "0.1.0",
            "2.1.0",
            "2.1.0",
            "2.0.0",
            hasGcmzDrops ? "3.0.0" : null);
        return new CapabilityEnvironment(
            true,
            true,
            isProjectSaved,
            canEdit,
            true,
            hasGcmzDrops,
            versions);
    }
}
