using System.Net;
using DistributedLocalSystem.Core;

static string ResolveLocalHttpUrl(string[] args)
{
    foreach (string key in new[] { "BACKEND_HTTP_BASE_URL", "LOCAL_HTTP_BASE" })
    {
        string? v = Environment.GetEnvironmentVariable(key)?.Trim();
        if (
            !string.IsNullOrEmpty(v)
            && Uri.TryCreate(v, UriKind.Absolute, out Uri? u)
            && u.Scheme == Uri.UriSchemeHttp
        )
            return $"{u.Scheme}://{u.Authority}";
    }

    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--urls" && Uri.TryCreate(args[i + 1], UriKind.Absolute, out Uri? au))
            return $"{au.Scheme}://{au.Authority}";
    }

    return "http://127.0.0.1:5000";
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddDistributedLocalSystemCore(builder.Configuration);
builder.Services.AddControllers();

string localHttpUrl = ResolveLocalHttpUrl(args);
int lanPort = builder.Configuration.GetValue("Net:LanPort", 17891);
builder.WebHost.UseUrls(localHttpUrl, $"http://0.0.0.0:{lanPort}");

builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

WebApplication app = builder.Build();
app.UseCors();
app.UseDistributedLocalSystemCoreProxy();

app.MapControllers();

app.MapGet(
    "/greet",
    (string? name) =>
    {
        string safeName = string.IsNullOrWhiteSpace(name) ? "Anonymous" : name.Trim();
        return Results.Text($"Hello, {safeName}! You've been greeted from C#!");
    }
);

app.MapGet("/health", () => Results.Text("OK"));

app.Run();
