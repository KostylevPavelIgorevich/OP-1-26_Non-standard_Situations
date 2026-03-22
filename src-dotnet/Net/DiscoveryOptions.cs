namespace Backend.Net;

public sealed class DiscoveryOptions
{
    public const string SectionName = "Net";

    public string AppId { get; set; } = "op1-26";
    public int UdpPort { get; set; } = 49152;
    public int LanPort { get; set; } = 17891;
    public int BeaconIntervalMs { get; set; } = 2000;
    public int DiscoveryTimeoutMs { get; set; } = 5000;
    public int ProtocolVersion { get; set; } = 1;
}
