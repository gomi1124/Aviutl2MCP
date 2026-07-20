using System.Text.Json;

namespace AviUtl2MCP.BridgeClient.Handshake;

public sealed record ProtocolRange(
    ushort MinMajor,
    ushort MinMinor,
    ushort MaxMajor,
    ushort MaxMinor);

public sealed record NegotiatedProtocol(ushort Major, ushort Minor);

public sealed record HandshakeLimits(int JsonBytes, int BinaryBytes, int InFlight);

public sealed record ClientHello(
    Guid ClientInstanceId,
    int ClientProcessId,
    Guid TargetInstanceId,
    ProtocolRange Protocol,
    string ClientVersion,
    HandshakeLimits Limits);

public sealed record BridgeVersions(string Bridge, string Sdk, string Aviutl);

public sealed record HandshakeError(string Code, string Message);

public sealed record ServerHello(
    bool Accepted,
    Guid? InstanceId = null,
    Guid? ServerEpoch = null,
    int? AviutlProcessId = null,
    long? AviutlProcessCreationTime = null,
    NegotiatedProtocol? Protocol = null,
    BridgeVersions? Versions = null,
    HandshakeLimits? Limits = null,
    JsonElement? Capabilities = null,
    HandshakeError? Error = null,
    ProtocolRange? ClientRange = null,
    ProtocolRange? ServerRange = null);

public sealed record BridgeSessionInfo(
    Guid InstanceId,
    Guid ServerEpoch,
    int AviutlProcessId,
    long AviutlProcessCreationTime,
    NegotiatedProtocol Protocol,
    BridgeVersions Versions,
    HandshakeLimits Limits,
    JsonElement Capabilities);
