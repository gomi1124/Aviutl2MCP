using System.Text;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.BridgeClient.Connections;
using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Gateways;

namespace AviUtl2MCP.RealAviUtlTests;

[TestClass]
[DoNotParallelize]
public sealed class RealOpenSceneTests
{
    private static readonly UTF8Encoding UTF8_NO_BOM = new(false, true);

    [TestMethod]
    [TestCategory("RealAviUtl2")]
    [TestProperty("TestId", "real.open-scene")]
    [TestProperty("TestId", "real.fixture-process-guard")]
    [Timeout(120_000)]
    public async Task RealAviUtlOpensSceneByIdAndNameWithoutChangingContentRevision()
    {
        if (!RealAviUtlHarness.IsEnabled)
        {
            Assert.Inconclusive(
                "Set AVIUTL2_MCP_REAL_TEST=1 to run the isolated real AviUtl2 test.");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        await using RealAviUtlHarness harness = await RealAviUtlHarness.StartAsync(
            timeout.Token,
            PrepareSceneFixture);
        harness.RecordAcceptanceTestIds("real.open-scene", "real.fixture-process-guard");
        try
        {
            InstanceDescriptorWatcher watcher = new(harness.InstanceDirectory);
            BridgeConnectionFactory connectionFactory = new(Guid.NewGuid(), "0.1.0-real-test");
            await using BridgeConnectionRegistry registry = new(watcher, connectionFactory);
            BridgeQueryGateway query = new(registry);
            BridgeEditGateway edit = new(registry);

            GatewayResponse<ProjectData> initial = await WaitForProjectAsync(
                query,
                harness.InstanceId,
                timeout.Token);
            Assert.IsTrue(initial.Ok, initial.Error?.Message);
            Assert.AreEqual(0, initial.Data!.CurrentSceneId);
            Assert.AreEqual("Root", initial.Data.Scenes.Single().Name);
            Assert.IsNotNull(initial.Revision);
            Assert.IsNotNull(initial.ViewRevision);
            Revision contentRevision = initial.Revision.Value;
            Revision initialViewRevision = initial.ViewRevision.Value;
            harness.RecordRevision(contentRevision.Value);

            GatewayResponse<OpenSceneData> openedById = await edit.OpenSceneAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new OpenSceneInput
                    {
                        SceneId = 7,
                        ExpectedViewRevision = initialViewRevision,
                    }),
                timeout.Token);
            Assert.IsTrue(openedById.Ok, openedById.Error?.Message);
            Assert.AreEqual(7, openedById.Data!.SceneId);
            Assert.AreEqual("MCP Scene Seven", openedById.Data.Name);
            Assert.AreEqual(contentRevision, openedById.Revision);
            Assert.IsNotNull(openedById.ViewRevision);
            Assert.AreNotEqual(initialViewRevision, openedById.ViewRevision.Value);

            GatewayResponse<ProjectData> sceneSeven = await query.GetProjectAsync(
                CreateGatewayRequest(harness.InstanceId, new GetProjectInput()),
                timeout.Token);
            Assert.IsTrue(sceneSeven.Ok, sceneSeven.Error?.Message);
            Assert.AreEqual(7, sceneSeven.Data!.CurrentSceneId);
            Assert.AreEqual("MCP Scene Seven", sceneSeven.Data.Scenes.Single().Name);

            Revision sceneSevenViewRevision = openedById.ViewRevision.Value;
            GatewayResponse<OpenSceneData> openedByName = await edit.OpenSceneAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new OpenSceneInput
                    {
                        SceneName = "MCP Scene Two",
                        ExpectedViewRevision = sceneSevenViewRevision,
                    }),
                timeout.Token);
            Assert.IsTrue(openedByName.Ok, openedByName.Error?.Message);
            Assert.AreEqual(2, openedByName.Data!.SceneId);
            Assert.AreEqual("MCP Scene Two", openedByName.Data.Name);
            Assert.AreEqual(contentRevision, openedByName.Revision);
            Assert.IsNotNull(openedByName.ViewRevision);
            Assert.AreNotEqual(sceneSevenViewRevision, openedByName.ViewRevision.Value);

            Revision sceneTwoViewRevision = openedByName.ViewRevision.Value;
            GatewayResponse<OpenSceneData> idempotent = await edit.OpenSceneAsync(
                CreateGatewayRequest(
                    harness.InstanceId,
                    new OpenSceneInput
                    {
                        SceneId = 2,
                        ExpectedViewRevision = sceneTwoViewRevision,
                    }),
                timeout.Token);
            Assert.IsTrue(idempotent.Ok, idempotent.Error?.Message);
            Assert.AreEqual(sceneTwoViewRevision, idempotent.ViewRevision);
            Assert.AreEqual(contentRevision, idempotent.Revision);
            harness.RecordRevision(contentRevision.Value);
        }
        catch (Exception exception)
        {
            harness.RecordFailure(exception);
            throw;
        }
    }

    private static void PrepareSceneFixture(string fixtureProjectPath)
    {
        string sourceDataPath = Environment.GetEnvironmentVariable(
            "AVIUTL2_MCP_REAL_DATA_PATH")
            ?? throw new InvalidOperationException(
                "AVIUTL2_MCP_REAL_DATA_PATH is required for the scene fixture.");
        string sourceLayoutPath = Path.Combine(sourceDataPath, "aviutl2.ini");
        string runtimeDirectory = Directory.GetParent(
            Path.GetDirectoryName(fixtureProjectPath)!)!.FullName;
        string isolatedLayoutPath = Path.Combine(
            runtimeDirectory,
            "aviutl2",
            "data",
            "aviutl2.ini");
        File.Copy(sourceLayoutPath, isolatedLayoutPath, overwrite: false);

        string sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "OpenSceneFixture.aup2");
        string content = File.ReadAllText(sourcePath, UTF8_NO_BOM);
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int fileLineIndex = Array.FindIndex(
            lines,
            line => line.StartsWith("file=", StringComparison.Ordinal));
        if (fileLineIndex < 0)
        {
            throw new InvalidDataException("The scene fixture omitted its file field.");
        }
        lines[fileLineIndex] = $"file={fixtureProjectPath}";
        File.WriteAllText(fixtureProjectPath, string.Join("\r\n", lines), UTF8_NO_BOM);
    }

    private static async Task<GatewayResponse<ProjectData>> WaitForProjectAsync(
        BridgeQueryGateway query,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        GatewayResponse<ProjectData>? lastResponse = null;
        do
        {
            lastResponse = await query.GetProjectAsync(
                CreateGatewayRequest(instanceId, new GetProjectInput()),
                cancellationToken);
            if (lastResponse.Ok || lastResponse.Error?.Code != "project_not_open")
            {
                return lastResponse;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);
        return lastResponse!;
    }

    private static GatewayRequest<T> CreateGatewayRequest<T>(Guid instanceId, T parameters) =>
        new(
            instanceId,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddSeconds(60),
            60_000,
            null,
            false,
            parameters);
}
