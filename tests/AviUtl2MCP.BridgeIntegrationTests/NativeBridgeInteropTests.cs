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
            Assert.AreEqual(IpcFrameOption.ErrorResponse, response.Header.Flags);
            StringAssert.Contains(Encoding.UTF8.GetString(response.JsonBytes.Span), "operation_not_supported");
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
