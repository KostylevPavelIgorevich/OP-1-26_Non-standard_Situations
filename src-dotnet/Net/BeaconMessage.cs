using System.Text.Json.Serialization;

namespace Backend.Net;

/// <summary>UDP beacon (JSON, UTF-8).</summary>
public sealed class BeaconMessage
{
    [JsonPropertyName("app")]
    public string App { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("tcp")]
    public int Tcp { get; set; }

    [JsonPropertyName("v")]
    public int V { get; set; }
}
