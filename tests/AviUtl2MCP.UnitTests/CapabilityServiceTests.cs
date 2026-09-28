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
        Assert.HasCount(34, data.Operations);
        Assert.HasCount(34, data.Operations.Select(operation => operation.Name).Distinct(StringComparer.Ordinal).ToArray());
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

    [TestMethod]
    public void GetCapabilitiesRequiresNamedEditableProjectForOpeningScene()
    {
        // Arrange
        CapabilityEnvironment environment = CreateEnvironment(
            hasGcmzDrops: true,
            isProjectSaved: false,
            aviutlVersion: "2.1.7a");

        // Act
        CapabilityOperation openScene = CapabilityService.GetCapabilities(environment)
            .Operations.Single(operation => operation.Name == "aviutl_open_scene");

        // Assert
        Assert.IsFalse(openScene.Available);
        Assert.AreEqual("project_path_required", openScene.Reason);
    }

    [TestMethod]
    [DataRow("2011000", true)]
    [DataRow("2011001", true)]
    [DataRow("2010900", false)]
    [DataRow("2.1.10", true)]
    [DataRow("2.1.10a", true)]
    [DataRow("2.2.0", true)]
    [DataRow("2.1.9", false)]
    [DataRow("2.1.7a", false)]
    [DataRow("unknown", false)]
    [DataRow(null, false)]
    public void GetCapabilitiesGatesSceneCreationByHostVersion(string? aviutlVersion, bool isAvailable)
    {
        // Arrange
        CapabilityEnvironment environment = CreateEnvironment(hasGcmzDrops: true, aviutlVersion: aviutlVersion);

        // Act
        CapabilityOperation create = CapabilityService.GetCapabilities(environment)
            .Operations.Single(operation => operation.Name == "aviutl_create_scene");

        // Assert
        Assert.AreEqual(isAvailable, create.Available);
        Assert.AreEqual(isAvailable ? null : "version_not_supported", create.Reason);
    }

    [TestMethod]
    public void GetCapabilitiesUsesNativeSceneApiForUnsavedProjects()
    {
        // Arrange
        CapabilityEnvironment environment = CreateEnvironment(hasGcmzDrops: true, isProjectSaved: false);

        // Act
        CapabilitiesData data = CapabilityService.GetCapabilities(environment);
        CapabilityOperation create = data.Operations.Single(operation => operation.Name == "aviutl_create_scene");
        CapabilityOperation open = data.Operations.Single(operation => operation.Name == "aviutl_open_scene");

        // Assert
        Assert.IsTrue(create.Available);
        Assert.IsNull(create.Reason);
        Assert.IsTrue(open.Available);
        Assert.IsNull(open.Reason);
    }

    [TestMethod]
    [DataRow(false, true, true, "bridge_not_connected")]
    [DataRow(true, false, true, "project_not_open")]
    [DataRow(true, true, false, "edit_not_available")]
    public void GetCapabilitiesRequiresEditableProjectForSceneCreation(
        bool isBridgeReady,
        bool isProjectOpen,
        bool canEdit,
        string reason)
    {
        // Arrange
        CapabilityEnvironment environment = CreateEnvironment(hasGcmzDrops: true) with
        {
            IsBridgeReady = isBridgeReady,
            IsProjectOpen = isProjectOpen,
            CanEdit = canEdit,
        };

        // Act
        CapabilityOperation create = CapabilityService.GetCapabilities(environment)
            .Operations.Single(operation => operation.Name == "aviutl_create_scene");

        // Assert
        Assert.IsFalse(create.Available);
        Assert.AreEqual(reason, create.Reason);
    }

    private static CapabilityEnvironment CreateEnvironment(
        bool hasGcmzDrops,
        bool canEdit = true,
        bool isProjectSaved = true,
        string? aviutlVersion = "2011000")
    {
        CapabilityVersions versions = new(
            "0.1.0",
            "1.0.0",
            "1.0",
            "0.1.0",
            aviutlVersion,
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
