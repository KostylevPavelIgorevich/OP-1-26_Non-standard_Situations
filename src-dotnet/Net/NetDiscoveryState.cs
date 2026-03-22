namespace Backend.Net;

/// <summary>Единое состояние UDP discovery.</summary>
public enum NetDiscoveryState
{
    /// <summary>Нет активного режима или после Stop.</summary>
    Idle,

    /// <summary>Хост: шлёт beacon.</summary>
    HostBeaconing,

    /// <summary>Клиент: ищет хост по UDP.</summary>
    ClientDiscovering,

    /// <summary>Клиент: найден хост, есть remoteHostBaseUrl.</summary>
    ClientConnected,

    /// <summary>Клиент: хост не найден за таймаут (graceful degradation).</summary>
    ClientLocalOnly,
}

public static class NetDiscoveryStateExtensions
{
    /// <summary>Строка для JSON API (camelCase).</summary>
    public static string ToApiString(this NetDiscoveryState s) =>
        s switch
        {
            NetDiscoveryState.Idle => "idle",
            NetDiscoveryState.HostBeaconing => "hostBeaconing",
            NetDiscoveryState.ClientDiscovering => "clientDiscovering",
            NetDiscoveryState.ClientConnected => "clientConnected",
            NetDiscoveryState.ClientLocalOnly => "clientLocalOnly",
            _ => "idle",
        };
}
