using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Backend.Net;

/// <summary>UDP discovery: хост шлёт beacon, клиент ищет хост или остаётся локально.</summary>
public sealed class NetDiscoveryService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly DiscoveryOptions _opt;
    private readonly ILogger<NetDiscoveryService> _log;
    private readonly object _gate = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    private NetDiscoveryState _state = NetDiscoveryState.Idle;
    private string? _remoteHostIp;
    private int? _remoteTcpPort;
    private string? _thisHostIp;

    public NetDiscoveryService(IOptions<DiscoveryOptions> options, ILogger<NetDiscoveryService> log)
    {
        _opt = options.Value;
        _log = log;
    }

    public NetStatusDto GetStatus()
    {
        lock (_gate)
        {
            return new NetStatusDto(
                State: _state.ToApiString(),
                ThisHostIp: _thisHostIp,
                RemoteHostIp: _remoteHostIp,
                RemoteTcpPort: _remoteTcpPort,
                RemoteHostBaseUrl: BuildRemoteBaseUrl(),
                LanPort: _opt.LanPort,
                UdpPort: _opt.UdpPort,
                AppId: _opt.AppId);
        }
    }

    private string? BuildRemoteBaseUrl()
    {
        if (string.IsNullOrEmpty(_remoteHostIp) || _remoteTcpPort is null or <= 0)
            return null;
        return $"http://{_remoteHostIp}:{_remoteTcpPort}";
    }

    /// <summary>Режим хоста: периодический UDP beacon.</summary>
    public void StartHost()
    {
        lock (_gate)
        {
            StopUnsafe();
            _state = NetDiscoveryState.HostBeaconing;
            _remoteHostIp = null;
            _remoteTcpPort = null;
            _thisHostIp = GetPrimaryLanIPv4();
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            _runTask = Task.Run(() => HostLoopAsync(token), token);
            _log.LogInformation("Net: host mode, beacon every {Ms} ms", _opt.BeaconIntervalMs);
        }
    }

    /// <summary>Режим клиента: поиск хоста по UDP, иначе local_only.</summary>
    public void StartClient()
    {
        lock (_gate)
        {
            StopUnsafe();
            _state = NetDiscoveryState.ClientDiscovering;
            _remoteHostIp = null;
            _remoteTcpPort = null;
            _thisHostIp = GetPrimaryLanIPv4();
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            _runTask = Task.Run(() => ClientDiscoverAsync(token), token);
            _log.LogInformation("Net: client discovery, timeout {Ms} ms", _opt.DiscoveryTimeoutMs);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopUnsafe();
            _state = NetDiscoveryState.Idle;
            _remoteHostIp = null;
            _remoteTcpPort = null;
        }
    }

    private void StopUnsafe()
    {
        try
        {
            _runCts?.Cancel();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Net: Cancel on shutdown");
        }

        try
        {
            _runTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Net: background task wait on shutdown");
        }

        _runCts?.Dispose();
        _runCts = null;
        _runTask = null;
    }

    private async Task HostLoopAsync(CancellationToken token)
    {
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;

        var beacon = new BeaconMessage
        {
            App = _opt.AppId,
            Role = "host",
            Tcp = _opt.LanPort,
            V = _opt.ProtocolVersion,
        };

        while (!token.IsCancellationRequested)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(beacon, JsonOpts);
                var dest = new IPEndPoint(IPAddress.Broadcast, _opt.UdpPort);
                await udp.SendAsync(bytes, bytes.Length, dest).WaitAsync(token).ConfigureAwait(false);
                // На одной машине broadcast часто не доходит до своего же слушателя — дублируем на loopback.
                var loopback = new IPEndPoint(IPAddress.Loopback, _opt.UdpPort);
                await udp.SendAsync(bytes, bytes.Length, loopback).WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Net: host beacon send failed");
            }

            try
            {
                await Task.Delay(_opt.BeaconIntervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ClientDiscoverAsync(CancellationToken token)
    {
        using var udp = new UdpClient(_opt.UdpPort);
        using var timeout = new CancellationTokenSource(_opt.DiscoveryTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        try
        {
            while (!linked.IsCancellationRequested)
            {
                UdpReceiveResult incoming;
                try
                {
                    incoming = await udp.ReceiveAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                BeaconMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<BeaconMessage>(incoming.Buffer, JsonOpts);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Net: bad beacon packet");
                    continue;
                }

                if (msg is null || msg.V != _opt.ProtocolVersion)
                    continue;
                if (!string.Equals(msg.App, _opt.AppId, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(msg.Role, "host", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (msg.Tcp <= 0)
                    continue;

                var hostIp = incoming.RemoteEndPoint.Address.ToString();
                lock (_gate)
                {
                    _state = NetDiscoveryState.ClientConnected;
                    _remoteHostIp = hostIp;
                    _remoteTcpPort = msg.Tcp;
                }

                _log.LogInformation("Net: host found at {Host}:{Tcp}", hostIp, msg.Tcp);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Net: client discovery error");
        }

        lock (_gate)
        {
            if (_state == NetDiscoveryState.ClientDiscovering)
            {
                _state = NetDiscoveryState.ClientLocalOnly;
                _log.LogInformation("Net: no host, graceful degradation → clientLocalOnly");
            }
        }
    }

    private string? GetPrimaryLanIPv4()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var a in host.AddressList)
            {
                if (a.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(a))
                    continue;
                if (a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    continue;
                return a.ToString();
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Net: could not resolve primary LAN IPv4");
        }

        return null;
    }

    public void Dispose() => Stop();
}
