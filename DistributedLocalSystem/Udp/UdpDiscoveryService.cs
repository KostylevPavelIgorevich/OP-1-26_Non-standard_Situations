using DistributedLocalSystem.Core.Discovery;
using Microsoft.Extensions.Options;
using UdpDiscovery.Net;

namespace DistributedLocalSystem.Core.Udp;

public sealed class UdpDiscoveryService : IHostedService, IDisposable
{
    private readonly string _serviceName;

    public event Action<DiscoveredServer>? ServerDiscovered;
    public event Action<DiscoveredServer>? ServerLost;

    public string? LastDiscoveredServerIp { get; private set; } = string.Empty;

    private readonly ServerDiscoveryService _serverDiscoveryService;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _discoveryTask;

    public UdpDiscoveryService(IOptions<DiscoveryOptions> options)
    {
        _serviceName = options.Value.AppId;
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

            // В UdpDiscovery.Net остановка discovery делается через ClearDiscoveredServers().
            // Иначе Task из StartDiscovery() может не завершиться.
            try
            {
                _serverDiscoveryService.ClearDiscoveredServers();
            }
            catch
            {
                // best-effort
            }

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
            if (!string.Equals(server.Name, _serviceName, StringComparison.Ordinal))
                return;

            LastDiscoveredServerIp = server.IpAddress.ToString();
            ServerDiscovered?.Invoke(server);
        };

        _serverDiscoveryService.OnServerDisappeared += (server) =>
        {
            if (!string.Equals(server.Name, _serviceName, StringComparison.Ordinal))
                return;

            ServerLost?.Invoke(server);
        };

        await _serverDiscoveryService.StartDiscovery();
    }

    public void Dispose()
    {
        try
        {
            _serverDiscoveryService.ClearDiscoveredServers();
        }
        catch
        {
            // best-effort
        }

        try
        {
            _serverDiscoveryService.Dispose();
        }
        catch
        {
            // best-effort
        }

        _cancellationTokenSource?.Dispose();
    }
}
