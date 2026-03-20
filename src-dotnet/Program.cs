var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet(
    "/greet",
    (string? name) =>
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? "Anonymous" : name.Trim();
        return Results.Text($"Hello, {safeName}! You've been greeted from C#!");
    }
);

app.MapGet("/health", () => Results.Text("OK"));
app.Run();
