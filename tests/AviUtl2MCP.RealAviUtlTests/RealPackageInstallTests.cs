using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Gateways;
using AviUtl2MCP.BridgeClient.Connections;
using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Gateways;

namespace AviUtl2MCP.RealAviUtlTests;

[TestClass]
public sealed class RealPackageInstallTests
{
    private const string PACKAGE_PATH_VARIABLE =
        "AVIUTL2_MCP_REAL_BRIDGE_PACKAGE_PATH";

    [TestMethod]
    [TestCategory("RealAviUtl2")]
    [TestProperty("TestId", "real.package-install")]
    [Timeout(180_000)]
    public async Task RealPackagedBridgeStartsAndReportsVersion()
    {
        if (!RealAviUtlHarness.IsEnabled)
        {
            Assert.Inconclusive(
                "Set AVIUTL2_MCP_REAL_TEST=1 to run the isolated real AviUtl2 test.");
        }
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(PACKAGE_PATH_VARIABLE)))
        {
            Assert.Inconclusive(
                $"Set {PACKAGE_PATH_VARIABLE} to test an .au2pkg.zip artifact.");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await using RealAviUtlHarness harness = await RealAviUtlHarness.StartAsync(
            timeout.Token);
        try
        {
            InstanceDescriptorWatcher watcher = new(GetDescriptorDirectory());
            BridgeConnectionFactory connectionFactory = new(
                Guid.NewGuid(),
                "0.1.0-real-package-test");
            await using BridgeConnectionRegistry registry = new(watcher, connectionFactory);
            BridgeDiagnosticsGateway diagnostics = new(registry);

            GatewayResponse<StatusData> status = await diagnostics.GetStatusAsync(
                CreateRequest(harness.InstanceId, new GetStatusInput()),
                timeout.Token);
            Assert.IsTrue(status.Ok, status.Error?.Message);
            Assert.AreEqual(ConnectionState.Ready, status.Data!.ConnectionState);
            Assert.AreEqual(harness.InstanceId, status.Data.SelectedInstance);
            AviUtlInstance instance = status.Data.Instances.Single(candidate =>
                candidate.InstanceId == harness.InstanceId);
            Assert.IsFalse(string.IsNullOrWhiteSpace(instance.BridgeVersion));

            GatewayResponse<CapabilitiesData> capabilities =
                await diagnostics.GetCapabilitiesAsync(
                    CreateRequest(harness.InstanceId, new GetCapabilitiesInput()),
                    timeout.Token);
            Assert.IsTrue(capabilities.Ok, capabilities.Error?.Message);
            Assert.IsFalse(string.IsNullOrWhiteSpace(capabilities.Data!.Versions.Bridge));
            Assert.AreEqual(instance.BridgeVersion, capabilities.Data.Versions.Bridge);
        }
        catch (Exception exception)
        {
            harness.RecordFailure(exception);
            throw;
        }
    }

    private static GatewayRequest<T> CreateRequest<T>(Guid instanceId, T parameters) =>
        new(
            instanceId,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddSeconds(60),
            60_000,
            ExpectedRevision: null,
            DryRun: false,
            parameters);

    private static string GetDescriptorDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AviUtl2MCP",
        "v1",
        "instances");
}
