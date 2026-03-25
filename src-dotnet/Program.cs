using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Net;
using Microsoft.Extensions.Options;

static string ResolveLocalHttpUrl(string[] args)
{
    foreach (var key in new[] { "BACKEND_HTTP_BASE_URL", "LOCAL_HTTP_BASE" })
    {
        var v = Environment.GetEnvironmentVariable(key)?.Trim();
        if (
            !string.IsNullOrEmpty(v)
            && Uri.TryCreate(v, UriKind.Absolute, out var u)
            && u.Scheme == Uri.UriSchemeHttp
        )
            return $"{u.Scheme}://{u.Authority}";
    }

    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--urls" && Uri.TryCreate(args[i + 1], UriKind.Absolute, out var au))
            return $"{au.Scheme}://{au.Authority}";
    }

    return "http://127.0.0.1:5000";
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DiscoveryOptions>(
    builder.Configuration.GetSection(DiscoveryOptions.SectionName)
);
builder.Services.AddSingleton<NetDiscoveryService>();
builder.Services.AddHostedService<NetDiscoveryHostedService>();
builder
    .Services.AddHttpClient("hostProxy")
    .ConfigurePrimaryHttpMessageHandler(static () =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        }
    );

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var localHttpUrl = ResolveLocalHttpUrl(args);
var lanPort = builder.Configuration.GetValue("Net:LanPort", 17891);
builder.WebHost.UseUrls(localHttpUrl, $"http://0.0.0.0:{lanPort}");

builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();
app.UseCors();
app.UseMiddleware<ClientHostProxyMiddleware>();

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

app.MapGet(
    "/api/net/role",
    (IOptions<DiscoveryOptions> opt) =>
        Results.Json(new { role = NetRoleApi.Format(opt.Value.ParsedRole) })
);

app.Run();
