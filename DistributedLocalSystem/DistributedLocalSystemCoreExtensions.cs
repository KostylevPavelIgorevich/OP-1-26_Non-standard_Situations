using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DistributedLocalSystem.Core.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DistributedLocalSystem.Core;

public static class DistributedLocalSystemCoreExtensions
{
    public static IServiceCollection AddDistributedLocalSystemCore(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.Configure<DiscoveryOptions>(
            configuration.GetSection(DiscoveryOptions.SectionName)
        );

        services.AddSingleton<NetDiscoveryService>();
        services.AddHostedService<NetDiscoveryHostedService>();

        services
            .AddHttpClient("hostProxy")
            .ConfigurePrimaryHttpMessageHandler(static () =>
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.None,
                }
            );

        // Чтобы UI получал enum'ы строками (camelCase), как раньше в Backend/Program.cs
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            );
        });

        return services;
    }

    public static IApplicationBuilder UseDistributedLocalSystemCoreProxy(
        this IApplicationBuilder app
    )
    {
        return app.UseMiddleware<ClientHostProxyMiddleware>();
    }
}
