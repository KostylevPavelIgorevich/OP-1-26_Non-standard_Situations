using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Backend.Net;

/// <summary>Сервис UDP discovery: хост рассылает beacon, клиент ищет хост или переходит в <see cref="NetDiscoveryState.ClientLocalOnly"/>.</summary>
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

    /// <summary>Создаёт сервис с настройками из <see cref="DiscoveryOptions"/>.</summary>
    public NetDiscoveryService(IOptions<DiscoveryOptions> options, ILogger<NetDiscoveryService> log)
    {
        _opt = options.Value;
        _log = log;
    }

    /// <summary>Возвращает снимок состояния для HTTP API.</summary>
    public NetStatusDto GetStatus()
    {
        lock (_gate)
        {
            return new NetStatusDto(
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

    /// <summary>Запускает режим хоста: периодическая отправка UDP beacon.</summary>
    public void StartHost()
    {
        StartDiscovery(
            NetDiscoveryState.HostBeaconing,
            HostLoopAsync,
            () => _log.LogInformation("Net: host mode, beacon every {Ms} ms", _opt.BeaconIntervalMs));
    }

    /// <summary>Запускает режим клиента: ожидание beacon до таймаута, иначе <see cref="NetDiscoveryState.ClientLocalOnly"/>.</summary>
    public void StartClient()
    {
        StartDiscovery(
            NetDiscoveryState.ClientDiscovering,
            ClientDiscoverAsync,
            () => _log.LogInformation("Net: client discovery, timeout {Ms} ms", _opt.DiscoveryTimeoutMs));
    }

    /// <summary>Останавливает фоновую задачу и сбрасывает состояние в <see cref="NetDiscoveryState.Idle"/>.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopUnsafe();
            _state = NetDiscoveryState.Idle;
            ClearRemotePeer();
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>Общий сценарий старта: отмена предыдущего цикла, новое состояние, <see cref="Task.Run"/> фоновой задачи.</summary>
    private void StartDiscovery(
        NetDiscoveryState initialState,
        Func<CancellationToken, Task> loopTaskFactory,
        Action logAction)
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

    /// <summary>Обнуляет адрес и порт удалённого хоста (без смены <see cref="_state"/>).</summary>
    private void ClearRemotePeer()
    {
        _remoteHostIp = null;
        _remoteTcpPort = null;
    }

    /// <summary>Формирует <c>http://ip:port</c> для найденного хоста или <c>null</c>.</summary>
    private string? BuildRemoteBaseUrl()
    {
        if (string.IsNullOrEmpty(_remoteHostIp) || _remoteTcpPort is null or <= 0)
            return null;
        return $"http://{_remoteHostIp}:{_remoteTcpPort}";
    }

    /// <summary>Отменяет CTS, ждёт завершения фоновой задачи, освобождает ресурсы (вызывать под lock или когда гонки нет).</summary>
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

    /// <summary>Beacon для поля <see cref="BeaconMessage.Role"/> = host.</summary>
    private BeaconMessage CreateHostBeaconMessage() =>
        new BeaconMessage
        {
            App = _opt.AppId,
            Role = "host",
            Tcp = _opt.LanPort,
            V = _opt.ProtocolVersion,
        };

    /// <summary>Отправка payload в broadcast и на loopback (локальный тест на одной машине).</summary>
    private async Task SendBeaconUdpAsync(UdpClient udp, byte[] payload, CancellationToken token)
    {
        await udp
            .SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, _opt.UdpPort))
            .WaitAsync(token)
            .ConfigureAwait(false);
        await udp
            .SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, _opt.UdpPort))
            .WaitAsync(token)
            .ConfigureAwait(false);
    }

    /// <summary>Цикл хоста: beacon → пауза; при ошибке отправки — лог и пауза, затем повтор.</summary>
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

    /// <summary>Проверяет beacon на соответствие <see cref="DiscoveryOptions"/> и роли host.</summary>
    private bool IsValidHostBeacon(BeaconMessage? msg) =>
        msg is not null
        && msg.V == _opt.ProtocolVersion
        && string.Equals(msg.App, _opt.AppId, StringComparison.Ordinal)
        && string.Equals(msg.Role, "host", StringComparison.OrdinalIgnoreCase)
        && msg.Tcp > 0;

    /// <summary>Цикл клиента: приём UDP до таймаута; успех → <see cref="NetDiscoveryState.ClientConnected"/>; иначе → <see cref="NetDiscoveryState.ClientLocalOnly"/>.</summary>
    private async Task ClientDiscoverAsync(CancellationToken token)
    {
        using UdpClient udp = new UdpClient(_opt.UdpPort);
        using CancellationTokenSource timeout = new CancellationTokenSource(_opt.DiscoveryTimeoutMs);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

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

    /// <summary>Первый подходящий IPv4 LAN (не loopback, не APIPA) для отображения.</summary>
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

    /// <summary>Пригоден ли адрес для отображения как «основной» LAN IPv4.</summary>
    private static bool IsUsableLanIPv4(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetwork
        && !IPAddress.IsLoopback(a)
        && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal);
}
