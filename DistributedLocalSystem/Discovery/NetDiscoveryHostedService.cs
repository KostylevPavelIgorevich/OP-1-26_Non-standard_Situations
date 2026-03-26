using Microsoft.Extensions.Options;

namespace DistributedLocalSystem.Core.Discovery;

/// <summary>Старт/стоп discovery по <see cref="DiscoveryOptions.Role"/> из конфигурации.</summary>
public sealed class NetDiscoveryHostedService : IHostedService
{
    private readonly NetDiscoveryService _net;
    private readonly DiscoveryOptions _opt;
    private readonly ILogger<NetDiscoveryHostedService> _log;

    public NetDiscoveryHostedService(
        NetDiscoveryService net,
        IOptions<DiscoveryOptions> options,
        ILogger<NetDiscoveryHostedService> log
    )
    {
        _net = net;
        _opt = options.Value;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        switch (_opt.ParsedRole)
        {
            case NetConfiguredRole.Host:
                _net.StartHost();
                _log.LogInformation("Net: configured role host — UDP beacon started");
                break;
            case NetConfiguredRole.Client:
                _net.StartClient();
                _log.LogInformation("Net: configured role client — discovery started");
                break;
            default:
                _log.LogInformation("Net: configured role none — discovery idle");
                break;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _net.Stop();
        return Task.CompletedTask;
    }
}
