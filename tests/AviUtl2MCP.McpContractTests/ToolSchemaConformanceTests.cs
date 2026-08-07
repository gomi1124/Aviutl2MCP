using System.Text.Json;
using System.Text.Json.Nodes;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Serialization;
using Json.Schema;

namespace AviUtl2MCP.McpContractTests;

[TestClass]
public sealed class ToolSchemaConformanceTests
{
    private const string LOCATOR_JSON = """
        {"instanceId":"019f0000-0000-7000-8000-000000000001","projectGeneration":"019f0000-0000-7000-8000-000000000002","sceneId":0,"layer":1,"startFrame":1,"endFrame":30,"name":"object","aliasSha256":"0000000000000000000000000000000000000000000000000000000000000000","effectSignatureSha256":"1111111111111111111111111111111111111111111111111111111111111111"}
        """;
    private const string PLACEMENT_JSON = """{"sceneId":0,"layer":1,"startFrame":1,"durationFrames":30}""";
    private const string ERROR_OUTPUT_JSON = """
        {"ok":false,"correlationId":"019f0000-0000-7000-8000-000000000003","warnings":[],"error":{"code":"test_error","message":"fixture","canRetry":false,"details":{}}}
        """;

    [TestMethod]
    public void VerifyAllToolInputsAndOutputsConformToCatalog()
    {
        // Arrange
        string catalogPath = Path.Combine(FindRepositoryRoot(), "schemas", "mcp", "v1", "catalog.json");
        JsonObject catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
        Dictionary<string, InputFixture> fixtures = CreateInputFixtures();
        JsonArray tools = catalog["x-tools"]!.AsArray();

        // Act and Assert
        Assert.HasCount(33, fixtures);
        Assert.HasCount(33, tools);
        foreach (JsonNode? toolNode in tools)
        {
            JsonObject tool = toolNode!.AsObject();
            string toolName = tool["name"]!.GetValue<string>();
            InputFixture fixture = fixtures[toolName];
            string inputReference = tool["inputSchema"]!["$ref"]!.GetValue<string>();
            string outputReference = tool["outputSchema"]!["$ref"]!.GetValue<string>();

            object inputDto = ContractJsonSerializer.DeserializeContract(fixture.Json, fixture.DtoType);
            string serializedInput = ContractJsonSerializer.SerializeContract(inputDto, fixture.DtoType);
            AssertSchemaValidity(catalog, inputReference, serializedInput, true, toolName);
            AssertRejectsUnknownProperty(catalog, inputReference, serializedInput, toolName);

            Type outputType = typeof(ToolEnvelope<>).MakeGenericType(typeof(JsonNode));
            object outputDto = ContractJsonSerializer.DeserializeContract(ERROR_OUTPUT_JSON, outputType);
            string serializedOutput = ContractJsonSerializer.SerializeContract(outputDto, outputType);
            AssertSchemaValidity(catalog, outputReference, serializedOutput, true, toolName);
            AssertRejectsUnknownProperty(catalog, outputReference, serializedOutput, toolName);
        }
    }

    private static Dictionary<string, InputFixture> CreateInputFixtures()
    {
        return new Dictionary<string, InputFixture>(StringComparer.Ordinal)
        {
            ["aviutl_get_status"] = new(typeof(GetStatusInput), "{}"),
            ["aviutl_get_capabilities"] = new(typeof(GetCapabilitiesInput), "{}"),
            ["aviutl_get_project"] = new(typeof(GetProjectInput), "{}"),
            ["aviutl_save_project"] = new(typeof(SaveProjectInput), """{"expectedRevision":"r1"}"""),
            ["aviutl_get_timeline"] = new(typeof(GetTimelineInput), "{}"),
            ["aviutl_find_objects"] = new(typeof(FindObjectsInput), "{}"),
            ["aviutl_get_object"] = CreateFixture(typeof(GetObjectInput), """{"locator":__LOCATOR__}"""),
            ["aviutl_list_effects"] = new(typeof(ListEffectsInput), "{}"),
            ["aviutl_list_effect_items"] = new(typeof(ListEffectItemsInput), """{"effect":{"name":"standard"}}"""),
            ["aviutl_create_object"] = CreateFixture(typeof(CreateObjectInput), """{"expectedRevision":"r1","effect":{"name":"standard"},"placement":__PLACEMENT__}"""),
            ["aviutl_create_media_object"] = CreateFixture(typeof(CreateMediaObjectInput), """{"expectedRevision":"r1","mediaPath":"C:\\media.wav","placement":__PLACEMENT__}"""),
            ["aviutl_create_alias_object"] = CreateFixture(typeof(CreateAliasObjectInput), """{"expectedRevision":"r1","alias":"[Object]","placement":__PLACEMENT__}"""),
            ["aviutl_move_object"] = CreateFixture(typeof(MoveObjectInput), """{"expectedRevision":"r1","locator":__LOCATOR__,"placement":{"sceneId":0,"layer":2,"startFrame":31}}"""),
            ["aviutl_delete_object"] = CreateFixture(typeof(DeleteObjectInput), """{"expectedRevision":"r1","locator":__LOCATOR__}"""),
            ["aviutl_set_object_name"] = CreateFixture(typeof(SetObjectNameInput), """{"expectedRevision":"r1","locator":__LOCATOR__,"name":"renamed"}"""),
            ["aviutl_create_object_section"] = CreateFixture(typeof(CreateObjectSectionInput), """{"expectedRevision":"r1","locator":__LOCATOR__,"frame":15}"""),
            ["aviutl_delete_object_section"] = CreateFixture(typeof(DeleteObjectSectionInput), """{"expectedRevision":"r1","locator":__LOCATOR__,"section":1}"""),
            ["aviutl_move_object_section"] = CreateFixture(typeof(MoveObjectSectionInput), """{"expectedRevision":"r1","locator":__LOCATOR__,"section":1,"frame":16}"""),
            ["aviutl_set_effect_item"] = CreateFixture(typeof(SetEffectItemInput), """{"expectedRevision":"r1","locator":__LOCATOR__,"effect":{"name":"standard"},"itemName":"opacity","value":100}"""),
            ["aviutl_set_effect_state"] = CreateFixture(typeof(SetEffectStateInput), """{"expectedRevision":"r1","locator":__LOCATOR__,"effect":{"name":"standard"},"isEnabled":true}"""),
            ["aviutl_set_layer"] = new(typeof(SetLayerInput), """{"expectedRevision":"r1","layer":1,"name":"voice"}"""),
            ["aviutl_open_scene"] = new(typeof(OpenSceneInput), """{"sceneId":0}"""),
            ["aviutl_set_cursor"] = new(typeof(SetCursorInput), """{"frame":1}"""),
            ["aviutl_execute_batch"] = CreateFixture(typeof(ExecuteBatchInput), """{"expectedRevision":"r1","operations":[{"op":"createObject","clientOperationId":"op-1","args":{"effect":{"name":"standard"},"placement":__PLACEMENT__}}]}"""),
            ["aviutl_render_preview"] = new(typeof(RenderPreviewInput), """{"frame":1}"""),
            ["aviutl_get_logs"] = new(typeof(GetLogsInput), "{}"),
            ["aviutl_diagnose"] = new(typeof(DiagnoseInput), "{}"),
            ["aviutl_psd_create"] = CreateFixture(typeof(PsdCreateInput), """{"expectedRevision":"r1","psdPath":"C:\\character.psd","placement":__PLACEMENT__}"""),
            ["aviutl_psd_setup"] = new(typeof(PsdSetupInput), """{"expectedRevision":"r1"}"""),
            ["aviutl_psd_set_character"] = CreateFixture(typeof(PsdSetCharacterInput), """{"expectedRevision":"r1","locator":__LOCATOR__,"characterId":"alice"}"""),
            ["aviutl_psd_set_layer_state"] = CreateFixture(typeof(PsdSetLayerStateInput), """{"expectedRevision":"r1","locator":__LOCATOR__,"layerState":"eyebrow=open"}"""),
            ["aviutl_psd_create_voice"] = CreateFixture(typeof(PsdCreateVoiceInput), """{"expectedRevision":"r1","audioPath":"C:\\voice.wav","characterId":"alice","placement":__PLACEMENT__}"""),
            ["aviutl_psd_validate"] = CreateFixture(typeof(PsdValidateInput), """{"locator":__LOCATOR__}"""),
        };
    }

    private static InputFixture CreateFixture(Type dtoType, string template)
    {
        string json = template
            .Replace("__LOCATOR__", LOCATOR_JSON, StringComparison.Ordinal)
            .Replace("__PLACEMENT__", PLACEMENT_JSON, StringComparison.Ordinal);
        return new InputFixture(dtoType, json);
    }

    private static void AssertRejectsUnknownProperty(
        JsonObject catalog,
        string schemaReference,
        string json,
        string toolName)
    {
        JsonObject instance = JsonNode.Parse(json)!.AsObject();
        instance["unexpectedProperty"] = true;
        AssertSchemaValidity(catalog, schemaReference, instance.ToJsonString(), false, toolName);
    }

    private static void AssertSchemaValidity(
        JsonObject catalog,
        string schemaReference,
        string json,
        bool expectedIsValid,
        string toolName)
    {
        JsonObject schemaRoot = catalog.DeepClone().AsObject();
        schemaRoot.Remove("$id");
        schemaRoot["$ref"] = schemaReference;
        using JsonDocument schemaDocument = JsonDocument.Parse(schemaRoot.ToJsonString());
        using JsonDocument instanceDocument = JsonDocument.Parse(json);
        JsonSchema schema = JsonSchema.Build(schemaDocument.RootElement);

        EvaluationResults results = schema.Evaluate(instanceDocument.RootElement);

        Assert.AreEqual(expectedIsValid, results.IsValid, $"Schema mismatch for {toolName} ({schemaReference}). JSON: {json}");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AviUtl2MCP.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the AviUtl2MCP repository root.");
    }

    private sealed record InputFixture(Type DtoType, string Json);
}
