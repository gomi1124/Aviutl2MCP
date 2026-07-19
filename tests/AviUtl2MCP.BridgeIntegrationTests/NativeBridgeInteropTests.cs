using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AviUtl2MCP.BridgeClient.Discovery;
using AviUtl2MCP.BridgeClient.Handshake;
using AviUtl2MCP.BridgeClient.Protocol;
using AviUtl2MCP.BridgeClient.Transport;

namespace AviUtl2MCP.BridgeIntegrationTests;

[TestClass]
public sealed class NativeBridgeInteropTests
{
    private const string NATIVE_BRIDGE_PATH_VARIABLE = "AVIUTL2_MCP_NATIVE_BRIDGE_PATH";
    private const uint TEST_HOST_VERSION = 2003300;
    private static readonly string[] BRIDGE_LOG_SOURCES = ["bridge"];

    [TestMethod]
    public async Task NativePluginExportsDescriptorAndCompletesCSharpHandshake()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(NATIVE_BRIDGE_PATH_VARIABLE);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            Assert.Inconclusive($"Set {NATIVE_BRIDGE_PATH_VARIABLE} to opt in to the native bridge interop test.");
        }

        string bridgePath = Path.GetFullPath(configuredPath);
        Assert.IsTrue(File.Exists(bridgePath), $"Native bridge was not found at {bridgePath}.");

        nint module = NativeLibrary.Load(bridgePath);
        Assert.IsTrue(
            NativeLibrary.TryGetExport(module, "InitializeLogger", out _),
            "Native bridge did not export InitializeLogger.");
        Assert.IsTrue(
            NativeLibrary.TryGetExport(module, "RegisterPlugin", out _),
            "Native bridge did not export the required RegisterPlugin entry point.");
        InitializePlugin initialize = Marshal.GetDelegateForFunctionPointer<InitializePlugin>(
            NativeLibrary.GetExport(module, "InitializePlugin"));
        UninitializePlugin uninitialize = Marshal.GetDelegateForFunctionPointer<UninitializePlugin>(
            NativeLibrary.GetExport(module, "UninitializePlugin"));
        bool isInitialized = false;
        string? descriptorPath = null;
        try
        {
            isInitialized = initialize(TEST_HOST_VERSION);
            Assert.IsTrue(isInitialized, "InitializePlugin rejected the test host.");

            InstanceDescriptorWatcher watcher = new(pollInterval: TimeSpan.FromMilliseconds(20));
            BridgeInstanceDescriptor descriptor = await WaitForCurrentProcessDescriptorAsync(watcher);
            descriptorPath = Path.Combine(watcher.DirectoryPath, $"{descriptor.InstanceId:D}.json");

            await using NamedPipeBridgeTransport transport = new();
            BridgeHandshakeClient handshake = new(transport, Guid.CreateVersion7(), "0.1.0-test");
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            BridgeSessionInfo session = await handshake.HandshakeAsync(
                descriptor.PipeName,
                descriptor.InstanceId,
                timeout.Token);

            Assert.AreEqual(descriptor.InstanceId, session.InstanceId);
            Assert.AreEqual(Environment.ProcessId, session.AviutlProcessId);
            Assert.AreEqual(descriptor.ProcessCreationTime, session.AviutlProcessCreationTime);
            Assert.AreNotEqual(Guid.Empty, session.ServerEpoch);

            Guid requestId = Guid.CreateVersion7();
            string requestJson = JsonSerializer.Serialize(new
            {
                method = "status.get",
                correlationId = requestId,
                timeoutMs = 5000,
                dryRun = false,
                @params = new { },
            });
            IpcEncodedFrame request = IpcFrameCodec.EncodeFrame(
                IpcMessageKind.Request,
                IpcFrameOption.None,
                requestId,
                Encoding.UTF8.GetBytes(requestJson),
                []);
            await transport.WriteAsync(request.Bytes, timeout.Token);
            IpcFrame response = await IpcFrameCodec.DecodeFrameAsync(transport, timeout.Token);
            Assert.AreEqual(requestId, response.Header.RequestId);
            Assert.AreEqual(IpcFrameOption.None, response.Header.Flags);
            using (JsonDocument statusDocument = JsonDocument.Parse(response.JsonBytes))
            {
                JsonElement status = statusDocument.RootElement;
                Assert.IsTrue(status.GetProperty("ok").GetBoolean());
                JsonElement result = status.GetProperty("result");
                Assert.AreEqual("ready", result.GetProperty("connectionState").GetString());
                Assert.AreEqual("unknown", result.GetProperty("projectState").GetString());
                Assert.AreEqual(descriptor.InstanceId, result.GetProperty("selectedInstance").GetGuid());
                Assert.IsTrue(result.GetProperty("components").EnumerateArray().Any(component =>
                    component.GetProperty("name").GetString() == "sdk"
                    && component.GetProperty("status").GetString() == "unavailable"));
            }

            Guid capabilitiesRequestId = Guid.CreateVersion7();
            string capabilitiesRequestJson = JsonSerializer.Serialize(new
            {
                method = "capabilities.get",
                correlationId = capabilitiesRequestId,
                timeoutMs = 5000,
                dryRun = false,
                @params = new { },
            });
            IpcEncodedFrame capabilitiesRequest = IpcFrameCodec.EncodeFrame(
                IpcMessageKind.Request,
                IpcFrameOption.None,
                capabilitiesRequestId,
                Encoding.UTF8.GetBytes(capabilitiesRequestJson),
                []);
            await transport.WriteAsync(capabilitiesRequest.Bytes, timeout.Token);
            IpcFrame capabilitiesResponse = await IpcFrameCodec.DecodeFrameAsync(transport, timeout.Token);
            Assert.AreEqual(capabilitiesRequestId, capabilitiesResponse.Header.RequestId);
            Assert.AreEqual(IpcFrameOption.None, capabilitiesResponse.Header.Flags);
            using (JsonDocument capabilitiesDocument = JsonDocument.Parse(capabilitiesResponse.JsonBytes))
            {
                JsonElement capabilities = capabilitiesDocument.RootElement.GetProperty("result");
                Assert.AreEqual(28, capabilities.GetProperty("operations").GetArrayLength());
                Assert.IsTrue(capabilities.GetProperty("versions").GetProperty("sdk").ValueKind
                    == JsonValueKind.Null);
            }

            Guid timelineRequestId = Guid.CreateVersion7();
            string timelineRequestJson = JsonSerializer.Serialize(new
            {
                method = "timeline.get",
                correlationId = timelineRequestId,
                timeoutMs = 5000,
                dryRun = false,
                @params = new { limit = 1 },
            });
            IpcEncodedFrame timelineRequest = IpcFrameCodec.EncodeFrame(
                IpcMessageKind.Request,
                IpcFrameOption.None,
                timelineRequestId,
                Encoding.UTF8.GetBytes(timelineRequestJson),
                []);
            await transport.WriteAsync(timelineRequest.Bytes, timeout.Token);
            IpcFrame timelineResponse = await IpcFrameCodec.DecodeFrameAsync(transport, timeout.Token);
            Assert.AreEqual(timelineRequestId, timelineResponse.Header.RequestId);
            Assert.AreEqual(IpcFrameOption.ErrorResponse, timelineResponse.Header.Flags);
            using (JsonDocument timelineDocument = JsonDocument.Parse(timelineResponse.JsonBytes))
            {
                JsonElement timeline = timelineDocument.RootElement;
                Assert.IsFalse(timeline.GetProperty("ok").GetBoolean());
                Assert.AreEqual("sdk_not_available", timeline.GetProperty("error").GetProperty("code").GetString());
            }

            Guid logRequestId = Guid.CreateVersion7();
            string logRequestJson = JsonSerializer.Serialize(new
            {
                method = "logs.get",
                correlationId = logRequestId,
                timeoutMs = 5000,
                dryRun = false,
                @params = new
                {
                    sources = BRIDGE_LOG_SOURCES,
                    correlationId = requestId,
                    limit = 10,
                },
            });
            IpcEncodedFrame logRequest = IpcFrameCodec.EncodeFrame(
                IpcMessageKind.Request,
                IpcFrameOption.None,
                logRequestId,
                Encoding.UTF8.GetBytes(logRequestJson),
                []);
            await transport.WriteAsync(logRequest.Bytes, timeout.Token);
            IpcFrame logResponse = await IpcFrameCodec.DecodeFrameAsync(transport, timeout.Token);

            Assert.AreEqual(logRequestId, logResponse.Header.RequestId);
            Assert.AreEqual(IpcFrameOption.None, logResponse.Header.Flags);
            using JsonDocument logDocument = JsonDocument.Parse(logResponse.JsonBytes);
            Assert.IsTrue(logDocument.RootElement.GetProperty("ok").GetBoolean());
            JsonElement entries = logDocument.RootElement.GetProperty("result").GetProperty("entries");
            Assert.IsTrue(entries.GetArrayLength() >= 1, "The correlated bridge log was not returned.");
            Assert.IsTrue(entries.EnumerateArray().Any(entry =>
                entry.GetProperty("eventId").GetString() == "request.completed"));
        }
        finally
        {
            if (isInitialized)
            {
                uninitialize();
            }
            NativeLibrary.Free(module);
        }

        Assert.IsNotNull(descriptorPath);
        Assert.IsFalse(File.Exists(descriptorPath), "UninitializePlugin left a stale instance descriptor.");
    }

    private static async Task<BridgeInstanceDescriptor> WaitForCurrentProcessDescriptorAsync(
        InstanceDescriptorWatcher watcher)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            BridgeInstanceDescriptor? descriptor = watcher.ReadDescriptors().Instances
                .SingleOrDefault(candidate => candidate.ProcessId == Environment.ProcessId);
            if (descriptor is not null)
            {
                return descriptor;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Native bridge did not publish a valid descriptor for the test process.");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool InitializePlugin(uint version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UninitializePlugin();
}
