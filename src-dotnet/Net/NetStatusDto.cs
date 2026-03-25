namespace Backend.Net;

public sealed record NetStatusDto(
    string ConfiguredRole,
    NetDiscoveryState State,
    string? ThisHostIp,
    string? RemoteHostIp,
    int? RemoteTcpPort,
    string? RemoteHostBaseUrl,
    int LanPort,
    int UdpPort,
    string AppId
);
