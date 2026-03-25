using System.Net.Http.Headers;

namespace Backend.Net;

/// <summary>
/// В режиме клиента с найденным хостом пересылает HTTP на LAN-хост, кроме служебных путей (health, net API).
/// </summary>
public sealed class ClientHostProxyMiddleware
{
    private static readonly HashSet<string> HopByHopRequestHeaders = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host",
        "Content-Length",
        "Content-Type",
    };

    private static readonly HashSet<string> HopByHopResponseHeaders = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "Transfer-Encoding",
        "Connection",
    };

    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClientHostProxyMiddleware> _log;

    public ClientHostProxyMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpClientFactory,
        ILogger<ClientHostProxyMiddleware> log
    )
    {
        _next = next;
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context, NetDiscoveryService net)
    {
        if (ShouldBypass(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!net.TryGetHostProxyBaseUrl(out string? remoteBase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            await ForwardAsync(context, remoteBase).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Proxy to host {Base} failed", remoteBase);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync("Bad gateway: host unreachable.", context.RequestAborted)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Пути, которые всегда обрабатывает локальный процесс (клиентский UI / управление).</summary>
    internal static bool ShouldBypass(PathString path)
    {
        if (path.StartsWithSegments("/health"))
            return true;
        if (path.StartsWithSegments("/api/net/start"))
            return true;
        if (path.StartsWithSegments("/api/net/stop"))
            return true;
        if (path.StartsWithSegments("/api/net/status"))
            return true;
        return false;
    }

    private static Uri BuildTargetUri(HttpRequest request, string remoteBase)
    {
        var relative = (request.Path + request.QueryString).ToString();
        if (string.IsNullOrEmpty(relative))
            relative = "/";
        var baseWithSlash = remoteBase.EndsWith('/') ? remoteBase : remoteBase + "/";
        return new Uri(new Uri(baseWithSlash, UriKind.Absolute), relative);
    }

    private async Task ForwardAsync(HttpContext context, string remoteBase)
    {
        var target = BuildTargetUri(context.Request, remoteBase);
        using var requestMessage = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            target
        );

        var hasBody = RequestMayHaveBody(context.Request);
        foreach (var header in context.Request.Headers)
        {
            if (HopByHopRequestHeaders.Contains(header.Key))
                continue;
            if (!header.Key.StartsWith(':'))
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        if (hasBody)
        {
            var streamContent = new StreamContent(context.Request.Body);
            var contentType = context.Request.ContentType;
            if (!string.IsNullOrEmpty(contentType))
                streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            requestMessage.Content = streamContent;
        }

        var client = _httpClientFactory.CreateClient("hostProxy");
        using HttpResponseMessage upstream = await client
            .SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted
            )
            .ConfigureAwait(false);

        context.Response.StatusCode = (int)upstream.StatusCode;

        CopySafeResponseHeaders(upstream.Headers, context.Response.Headers);

        if (upstream.Content is { } body)
        {
            CopySafeResponseHeaders(body.Headers, context.Response.Headers);
            await body.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static void CopySafeResponseHeaders(
        HttpHeaders from,
        IHeaderDictionary to
    )
    {
        foreach (var header in from)
        {
            if (HopByHopResponseHeaders.Contains(header.Key))
                continue;
            try
            {
                to.Append(header.Key, header.Value.ToArray());
            }
            catch
            {
                /* запрещённые для ответа Kestrel имена — пропускаем */
            }
        }
    }

    private static bool RequestMayHaveBody(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            return false;
        if (request.ContentLength is > 0)
            return true;
        return HttpMethods.IsPost(request.Method)
            || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method)
            || string.Equals(request.Method, "DELETE", StringComparison.OrdinalIgnoreCase);
    }
}
