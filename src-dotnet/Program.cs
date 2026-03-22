using System.Text.Json.Serialization;
using Backend.Net;

static int ParseLocalPort(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--urls")
            return new Uri(args[i + 1]).Port;
    }

    return 5000;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DiscoveryOptions>(builder.Configuration.GetSection(DiscoveryOptions.SectionName));
builder.Services.AddSingleton<NetDiscoveryService>();

var localPort = ParseLocalPort(args);
var lanPort = builder.Configuration.GetValue("Net:LanPort", 17891);
builder.WebHost.UseUrls($"http://127.0.0.1:{localPort}", $"http://0.0.0.0:{lanPort}");

builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();
app.UseCors();

app.MapGet(
    "/greet",
    (string? name) =>
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? "Anonymous" : name.Trim();
        return Results.Text($"Hello, {safeName}! You've been greeted from C#!");
    }
);

app.MapGet("/health", () => Results.Text("OK"));

app.MapGet("/api/net/status", (NetDiscoveryService net) => Results.Json(net.GetStatus()));

app.MapPost(
    "/api/net/start",
    (StartNetRequest req, NetDiscoveryService net) =>
    {
        var mode = req.Mode?.Trim().ToLowerInvariant();
        return mode switch
        {
            "host" => Results.Json(net.StartHostAndGetStatus()),
            "client" => Results.Json(net.StartClientAndGetStatus()),
            _ => Results.BadRequest(new { error = "mode must be \"host\" or \"client\"" }),
        };
    });

app.MapPost(
    "/api/net/stop",
    (NetDiscoveryService net) =>
    {
        net.Stop();
        return Results.Json(net.GetStatus());
    });

app.Run();

internal sealed record StartNetRequest([property: JsonPropertyName("mode")] string? Mode);

internal static class NetDiscoveryServiceExtensions
{
    /// <summary>Сразу отдать статус; клиентский поиск идёт в фоне — опрашивайте GET /api/net/status.</summary>
    public static NetStatusDto StartClientAndGetStatus(this NetDiscoveryService net)
    {
        net.StartClient();
        return net.GetStatus();
    }

    public static NetStatusDto StartHostAndGetStatus(this NetDiscoveryService net)
    {
        net.StartHost();
        return net.GetStatus();
    }
}
