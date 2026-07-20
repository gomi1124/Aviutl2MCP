using System.Text.Json;
using System.Text.Json.Nodes;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Serialization;

namespace AviUtl2MCP.McpContractTests;

[TestClass]
public sealed class ContractRoundTripTests
{
    private static readonly string[] expectedBatchDiscriminators =
    [
        "createObject",
        "createMediaObject",
        "createAliasObject",
        "moveObject",
        "deleteObject",
        "setObjectName",
        "setEffectItem",
        "setEffectState",
        "setLayer",
    ];

    [TestMethod]
    public void RoundTripQueryDataPreservesRequiredNulls()
    {
        // Arrange
        StatusData data = new(
            ConnectionState.Ready,
            [new ComponentStatus("bridge", "ready", null)],
            ProjectState.Unsaved,
            EditState.Edit,
            null,
            [new AviUtlInstance(Guid.NewGuid(), 1234, "0.1.0", "ready")]);

        // Act
        string json = AssertRoundTrip(data);
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        // Assert
        Assert.IsTrue(root.ContainsKey("selectedInstance"));
        Assert.IsNull(root["selectedInstance"]);
    }

    [TestMethod]
    public void RoundTripBatchPreservesAllDiscriminators()
    {
        // Arrange
        Placement placement = new(0, 1, 1, DurationFrames: 30);
        IReadOnlyList<BatchOperation> operations =
        [
            new BatchCreateObject("1", new CreateObjectArgs(new EffectDefinitionSelector("effect"), placement)),
            new BatchCreateMediaObject("2", new CreateMediaObjectArgs("C:\\media.wav", placement)),
            new BatchCreateAliasObject("3", new CreateAliasObjectArgs("[Object]", placement)),
            new BatchMoveObject("4", new MoveObjectArgs(CreateLocator(), new MovePlacement(0, 2, 31))),
            new BatchDeleteObject("5", new DeleteObjectArgs(CreateLocator())),
            new BatchSetObjectName("6", new SetObjectNameArgs(CreateLocator(), "renamed")),
            new BatchSetEffectItem("7", new SetEffectItemArgs(CreateLocator(), new EffectInstanceSelector("effect"), "opacity", JsonSerializer.SerializeToElement(100))),
            new BatchSetEffectState("8", new SetEffectStateArgs(CreateLocator(), new EffectInstanceSelector("effect")) { IsEnabled = true }),
            new BatchSetLayer("9", new SetLayerArgs(1) { Name = "voice" }),
        ];
        ExecuteBatchInput input = new()
        {
            ExpectedRevision = new Revision("r1"),
            Operations = operations,
        };

        // Act
        string json = AssertRoundTrip(input);
        JsonArray serializedOperations = JsonNode.Parse(json)!["operations"]!.AsArray();
        string[] discriminators = serializedOperations
            .Select(operation => operation!["op"]!.GetValue<string>())
            .ToArray();

        // Assert
        CollectionAssert.AreEqual(expectedBatchDiscriminators, discriminators);
    }

    [TestMethod]
    public void RoundTripDiagnosticAndPsdDataPreservesClosedShapes()
    {
        // Arrange
        DiagnoseData diagnose = new(
            DiagnosticOverallStatus.Degraded,
            [new DiagnosticCheck("bridge", DiagnosticCheckStatus.Warning, ["not connected"], "editing unavailable", "start AviUtl2", true)],
            [new DiagnosticComponent("PSDToolKit2", DiagnosticComponentStatus.Detected, null, ["plugin folder"])],
            []);
        PsdVoiceData voice = new()
        {
            VoiceObjects = [],
            SubtitleObjects = [],
            CompanionFiles = new PsdCompanionFiles("C:\\voice.wav", "C:\\voice.txt", null),
            AppliedChanges = [],
        };
        PreviewData preview = new("image/png", 1280, 720, 1, new string('0', 64), 1024);

        // Act
        string diagnoseJson = AssertRoundTrip(diagnose);
        string voiceJson = AssertRoundTrip(voice);
        string previewJson = AssertRoundTrip(preview);

        // Assert
        Assert.IsTrue(JsonNode.Parse(diagnoseJson)!["components"]![0]!.AsObject().ContainsKey("version"));
        Assert.IsTrue(JsonNode.Parse(voiceJson)!["companionFiles"]!.AsObject().ContainsKey("labPath"));
        Assert.AreEqual("image/png", JsonNode.Parse(previewJson)!["mimeType"]!.GetValue<string>());
    }

    [TestMethod]
    public void DeserializeContractRejectsUnknownProperties()
    {
        // Arrange
        const string json = """{"unexpected":true}""";

        // Act
        Action action = () => ContractJsonSerializer.DeserializeContract<GetStatusInput>(json);

        // Assert
        Assert.ThrowsExactly<JsonException>(action);
    }

    private static string AssertRoundTrip<T>(T value)
    {
        string json = ContractJsonSerializer.SerializeContract(value);
        T roundTripped = ContractJsonSerializer.DeserializeContract<T>(json);
        string roundTrippedJson = ContractJsonSerializer.SerializeContract(roundTripped);
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(roundTrippedJson)));
        return roundTrippedJson;
    }

    private static ObjectLocator CreateLocator()
    {
        return new ObjectLocator(
            Guid.Parse("019f0000-0000-7000-8000-000000000001"),
            Guid.Parse("019f0000-0000-7000-8000-000000000002"),
            0,
            1,
            1,
            30,
            "object",
            new string('0', 64),
            new string('1', 64));
    }
}
