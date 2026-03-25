using System.Reflection;
using Backend.Net;
using Microsoft.Extensions.Options;

namespace Backend.Udp;

public class ApiUdpAnnouncer : IDisposable
{
    private readonly UdpDiscovery.Net.UdpDiscovery _udpBroadcaster;
    private CancellationTokenSource? _broadcastCts;
    private Task? _broadcastTask;

    public ApiUdpAnnouncer(IOptions<DiscoveryOptions> options)
    {
        string serviceName = Assembly.GetEntryAssembly()?.GetName().Name ?? "API";
        _udpBroadcaster = new UdpDiscovery.Net.UdpDiscovery(
            serviceName: serviceName,
            discoveryPort: (ushort)options.Value.UdpPort
        );
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_broadcastTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _broadcastCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _broadcastTask = Task.Run(
            () => _udpBroadcaster.StartBroadcasting(_broadcastCts.Token),
            _broadcastCts.Token
        );
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_broadcastCts is not null)
        {
            try
            {
                _broadcastCts.Cancel();
            }
            catch (ObjectDisposedException) { }

            if (_broadcastTask is not null)
            {
                try
                {
                    await _broadcastTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) { }
            }
            _broadcastCts.Dispose();
            _broadcastCts = null;
        }
    }

    public void Dispose()
    {
        _broadcastCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
