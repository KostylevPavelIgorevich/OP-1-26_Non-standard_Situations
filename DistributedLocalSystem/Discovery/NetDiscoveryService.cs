using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DistributedLocalSystem.Core.Udp;
using Microsoft.Extensions.Options;
using UdpDiscovery.Net;

namespace DistributedLocalSystem.Core.Discovery;

/// <summary>UDP discovery: хост — beacon, клиент — поиск хоста или <see cref="NetDiscoveryState.ClientLocalOnly"/>.</summary>
public sealed class NetDiscoveryService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly DiscoveryOptions _opt;
    private readonly IOptions<DiscoveryOptions> _options;
    private readonly ILogger<NetDiscoveryService> _log;
    private readonly object _gate = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    private ApiUdpAnnouncer? _hostAnnouncer;
    private UdpDiscoveryService? _clientDiscovery;

    private NetDiscoveryState _state = NetDiscoveryState.Idle;
    private string? _remoteHostIp;
    private int? _remoteTcpPort;
    private string? _thisHostIp;

    /// <summary>Настройки из <see cref="IOptions{T}"/>.</summary>
    public NetDiscoveryService(IOptions<DiscoveryOptions> options, ILogger<NetDiscoveryService> log)
    {
        _opt = options.Value;
        _options = options;
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
        lock (_gate)
        {
            StopUnsafe();

            _state = NetDiscoveryState.HostBeaconing;
            ClearRemotePeer();
            _thisHostIp = GetPrimaryLanIPv4();

            _runCts = new CancellationTokenSource();
            CancellationToken token = _runCts.Token;

            _hostAnnouncer = new ApiUdpAnnouncer(_options);
            _hostAnnouncer.StartAsync(token).GetAwaiter().GetResult();

            _runTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Ожидаемо: остановка роли.
                    }
                },
                token
            );

            _log.LogInformation("Net: host mode, UDP announcement started ({AppId})", _opt.AppId);
        }
    }

    /// <summary>Режим клиента: поиск до таймаута, иначе <see cref="NetDiscoveryState.ClientLocalOnly"/>.</summary>
    public void StartClient()
    {
        lock (_gate)
        {
            StopUnsafe();

            _state = NetDiscoveryState.ClientDiscovering;
            ClearRemotePeer();
            _thisHostIp = GetPrimaryLanIPv4();

            _runCts = new CancellationTokenSource();
            CancellationToken token = _runCts.Token;

            UdpDiscoveryService discovery = new(_options);
            _clientDiscovery = discovery;

            TaskCompletionSource<DiscoveredServer> tcs = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            discovery.ServerDiscovered += server => tcs.TrySetResult(server);

            _runTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await discovery.StartAsync(token).ConfigureAwait(false);

                        // Слушаем до первого сообщения от хоста.
                        DiscoveredServer server = await tcs
                            .Task.WaitAsync(token)
                            .ConfigureAwait(false);

                        lock (_gate)
                        {
                            _state = NetDiscoveryState.ClientConnected;
                            _remoteHostIp = server.IpAddress.ToString();
                            // В текущей архитектуре проксирование идет на LAN порт (config), а не на порт из UDP.
                            _remoteTcpPort = _opt.LanPort;
                        }

                        _log.LogInformation(
                            "Net: host found at {Host}:{Tcp} ({AppId})",
                            server.IpAddress,
                            _opt.LanPort,
                            _opt.AppId
                        );

                        // После первого сообщения слушание прекращаем.
                        await discovery.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Ожидаемо: остановка приложения/роли.
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Net: client discovery error");
                    }
                },
                token
            );

            _log.LogInformation(
                "Net: client mode, listening UDP until host found ({AppId})",
                _opt.AppId
            );
        }
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

    private string? BuildRemoteBaseUrl()
    {
        if (string.IsNullOrEmpty(_remoteHostIp) || _remoteTcpPort is null or <= 0)
            return null;
        return $"http://{_remoteHostIp}:{_remoteTcpPort}";
    }

    /// <summary>
    /// Удалённый хост не ответил на проверку <c>/health</c> после сбоя прокси — сброс связи и снова UDP discovery (только режим client).
    /// </summary>
    public void RestartClientDiscoveryAfterRemoteHostFailure()
    {
        if (_opt.ParsedRole != NetConfiguredRole.Client)
            return;

        _log.LogWarning(
            "Net: remote host considered dead; restarting UDP discovery ({AppId})",
            _opt.AppId
        );
        StartClient();
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

    private void ClearRemotePeer()
    {
        _remoteHostIp = null;
        _remoteTcpPort = null;
    }

    private void StopUnsafe()
    {
        using CancellationTokenSource stopCts = new(TimeSpan.FromSeconds(2));
        CancellationToken stopToken = stopCts.Token;

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

        try
        {
            _hostAnnouncer?.StopAsync(stopToken).GetAwaiter().GetResult();
        }
        catch
        {
            // ignore: best-effort shutdown
        }
        finally
        {
            _hostAnnouncer?.Dispose();
            _hostAnnouncer = null;
        }

        try
        {
            _clientDiscovery?.StopAsync(stopToken).GetAwaiter().GetResult();
        }
        catch
        {
            // ignore: best-effort shutdown
        }
        finally
        {
            _clientDiscovery?.Dispose();
            _clientDiscovery = null;
        }

        _runCts?.Dispose();
        _runCts = null;
        _runTask = null;
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

    public void Dispose() => Stop();
}
