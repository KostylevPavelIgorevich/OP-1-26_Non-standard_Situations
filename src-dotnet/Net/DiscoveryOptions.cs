namespace Backend.Net;

public sealed class DiscoveryOptions
{
    public const string SectionName = "Net";

    /// <summary>Режим из appsettings: none | host | client (регистр не важен).</summary>
    public string Role { get; set; } = "none";

    public string AppId { get; set; } = "op1-26";
    public int UdpPort { get; set; } = 49152;
    public int LanPort { get; set; } = 17891;
    public int BeaconIntervalMs { get; set; } = 2000;
    public int DiscoveryTimeoutMs { get; set; } = 5000;
    public int ProtocolVersion { get; set; } = 1;

    public static NetConfiguredRole ParseRole(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "host" => NetConfiguredRole.Host,
            "client" => NetConfiguredRole.Client,
            _ => NetConfiguredRole.None,
        };

    public NetConfiguredRole ParsedRole => ParseRole(Role);
}

public enum NetConfiguredRole
{
    None,
    Host,
    Client,
}

public static class NetRoleApi
{
    public static string Format(NetConfiguredRole r) =>
        r switch
        {
            NetConfiguredRole.Host => "host",
            NetConfiguredRole.Client => "client",
            _ => "none",
        };
}
