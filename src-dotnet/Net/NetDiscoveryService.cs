using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Backend.Net;

/// <summary>UDP discovery: хост — beacon, клиент — поиск хоста или <see cref="NetDiscoveryState.ClientLocalOnly"/>.</summary>
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

    /// <summary>Настройки из <see cref="IOptions{T}"/>.</summary>
    public NetDiscoveryService(IOptions<DiscoveryOptions> options, ILogger<NetDiscoveryService> log)
    {
        _opt = options.Value;
        _log = log;
    }

    /// <summary>Снимок для <c>GET /api/net/status</c>.</summary>
    public NetStatusDto GetStatus()
    {
        lock (_gate)
        {
            return new NetStatusDto(
                ConfiguredRole: NetRoleApi.Format(_opt.ParsedRole),
                State: _state,
                ThisHostIp: _thisHostIp,
                RemoteHostIp: _remoteHostIp,
                RemoteTcpPort: _remoteTcpPort,
                RemoteHostBaseUrl: BuildRemoteBaseUrl(),
                LanPort: _opt.LanPort,
                UdpPort: _opt.UdpPort,
                AppId: _opt.AppId
            );
        }
    }

    /// <summary>Режим хоста: периодический UDP beacon.</summary>
    public void StartHost()
    {
        StartDiscovery(
            NetDiscoveryState.HostBeaconing,
            HostLoopAsync,
            () => _log.LogInformation("Net: host mode, beacon every {Ms} ms", _opt.BeaconIntervalMs)
        );
    }

    /// <summary>Режим клиента: поиск до таймаута, иначе <see cref="NetDiscoveryState.ClientLocalOnly"/>.</summary>
    public void StartClient()
    {
        StartDiscovery(
            NetDiscoveryState.ClientDiscovering,
            ClientDiscoverAsync,
            () =>
                _log.LogInformation(
                    "Net: client discovery, timeout {Ms} ms",
                    _opt.DiscoveryTimeoutMs
                )
        );
    }

    /// <summary>Стоп фоновой задачи, состояние <see cref="NetDiscoveryState.Idle"/>.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopUnsafe();
            _state = NetDiscoveryState.Idle;
            ClearRemotePeer();
        }
    }

    private void StartDiscovery(
        NetDiscoveryState initialState,
        Func<CancellationToken, Task> loopTaskFactory,
        Action logAction
    )
    {
        lock (_gate)
        {
            StopUnsafe();
            _state = initialState;
            ClearRemotePeer();
            _thisHostIp = GetPrimaryLanIPv4();
            _runCts = new CancellationTokenSource();
            CancellationToken token = _runCts.Token;
            _runTask = Task.Run(() => loopTaskFactory(token), token);
            logAction();
        }
    }

    private void ClearRemotePeer()
    {
        _remoteHostIp = null;
        _remoteTcpPort = null;
    }

    private string? BuildRemoteBaseUrl()
    {
        if (string.IsNullOrEmpty(_remoteHostIp) || _remoteTcpPort is null or <= 0)
            return null;
        return $"http://{_remoteHostIp}:{_remoteTcpPort}";
    }

    /// <summary>
    /// Базовый URL LAN-хоста для проксирования HTTP (только <see cref="NetDiscoveryState.ClientConnected"/>).
    /// </summary>
    public bool TryGetHostProxyBaseUrl([NotNullWhen(true)] out string? baseUrl)
    {
        lock (_gate)
        {
            if (_state != NetDiscoveryState.ClientConnected)
            {
                baseUrl = null;
                return false;
            }

            baseUrl = BuildRemoteBaseUrl();
            return !string.IsNullOrEmpty(baseUrl);
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

    private BeaconMessage CreateHostBeaconMessage() =>
        new BeaconMessage
        {
            App = _opt.AppId,
            Role = "host",
            Tcp = _opt.LanPort,
            V = _opt.ProtocolVersion,
        };

    private async Task SendBeaconUdpAsync(UdpClient udp, byte[] payload, CancellationToken token)
    {
        await udp.SendAsync(
                payload,
                payload.Length,
                new IPEndPoint(IPAddress.Broadcast, _opt.UdpPort)
            )
            .WaitAsync(token)
            .ConfigureAwait(false);
        await udp.SendAsync(
                payload,
                payload.Length,
                new IPEndPoint(IPAddress.Loopback, _opt.UdpPort)
            )
            .WaitAsync(token)
            .ConfigureAwait(false);
    }

    private async Task HostLoopAsync(CancellationToken token)
    {
        using UdpClient udp = new UdpClient();
        udp.EnableBroadcast = true;
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(CreateHostBeaconMessage(), JsonOpts);

        while (!token.IsCancellationRequested)
        {
            try
            {
                await SendBeaconUdpAsync(udp, payload, token).ConfigureAwait(false);
                await Task.Delay(_opt.BeaconIntervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Net: host beacon send failed");
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
    }

    private bool IsValidHostBeacon(BeaconMessage? msg) =>
        msg is not null
        && msg.V == _opt.ProtocolVersion
        && string.Equals(msg.App, _opt.AppId, StringComparison.Ordinal)
        && string.Equals(msg.Role, "host", StringComparison.OrdinalIgnoreCase)
        && msg.Tcp > 0;

    private async Task ClientDiscoverAsync(CancellationToken token)
    {
        using UdpClient udp = new UdpClient(_opt.UdpPort);
        using CancellationTokenSource timeout = new CancellationTokenSource(
            _opt.DiscoveryTimeoutMs
        );
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            token,
            timeout.Token
        );

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

                if (!IsValidHostBeacon(msg))
                    continue;

                string hostIp = incoming.RemoteEndPoint.Address.ToString();
                lock (_gate)
                {
                    _state = NetDiscoveryState.ClientConnected;
                    _remoteHostIp = hostIp;
                    _remoteTcpPort = msg!.Tcp;
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
            IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (IPAddress a in host.AddressList)
            {
                if (!IsUsableLanIPv4(a))
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

    private static bool IsUsableLanIPv4(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetwork
        && !IPAddress.IsLoopback(a)
        && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal);

    /// <inheritdoc />
    public void Dispose() => Stop();
}
