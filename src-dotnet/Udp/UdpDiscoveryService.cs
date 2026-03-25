using Backend.Net;
using Microsoft.Extensions.Options;
using UdpDiscovery.Net;

namespace Backend.Udp;

public sealed class UdpDiscoveryService : IHostedService, IDisposable
{
    public static Action<DiscoveredServer>? ServerDiscovered;
    public static Action<DiscoveredServer>? ServerLost;
    public static string? LastDiscoveredServerIp = string.Empty;

    private readonly ServerDiscoveryService _serverDiscoveryService;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _discoveryTask;

    public UdpDiscoveryService(IOptions<DiscoveryOptions> options)
    {
        _serverDiscoveryService = new ServerDiscoveryService(
            discoveryPort: (ushort)options.Value.UdpPort
        );
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_discoveryTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        _discoveryTask = RunDiscoveryAsync();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cancellationTokenSource is not null)
        {
            _cancellationTokenSource.Cancel();

            if (_discoveryTask is not null)
            {
                await _discoveryTask.WaitAsync(cancellationToken);
            }

            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task RunDiscoveryAsync()
    {
        _serverDiscoveryService.OnServerDiscovered += (server) =>
        {
            LastDiscoveredServerIp = server.IpAddress.ToString();
            ServerDiscovered?.Invoke(server);
        };

        _serverDiscoveryService.OnServerDisappeared += (server) =>
        {
            ServerLost?.Invoke(server);
        };

        await _serverDiscoveryService.StartDiscovery();
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Dispose();
    }
}
