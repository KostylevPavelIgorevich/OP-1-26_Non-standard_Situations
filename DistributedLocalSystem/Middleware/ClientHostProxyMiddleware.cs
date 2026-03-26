using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DistributedLocalSystem.Core.Discovery;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace DistributedLocalSystem.Core.Middleware;

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
        if (context.Request.Path.StartsWithSegments("/api/net/status"))
        {
            NetStatusDto status = net.GetStatus();
            context.Response.ContentType = "application/json";
            JsonSerializerOptions jsonOptions = context
                .RequestServices.GetRequiredService<IOptions<JsonOptions>>()
                .Value.SerializerOptions;

            await JsonSerializer
                .SerializeAsync(context.Response.Body, status, jsonOptions, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api/net/role"))
        {
            NetStatusDto status = net.GetStatus();
            context.Response.ContentType = "application/json";
            JsonSerializerOptions jsonOptions = context
                .RequestServices.GetRequiredService<IOptions<JsonOptions>>()
                .Value.SerializerOptions;

            await JsonSerializer
                .SerializeAsync(
                    context.Response.Body,
                    new { role = status.ConfiguredRole },
                    jsonOptions,
                    context.RequestAborted
                )
                .ConfigureAwait(false);
            return;
        }

        if (ShouldBypass(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!net.TryGetHostProxyBaseUrl(out string? remoteBase) || string.IsNullOrEmpty(remoteBase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            await ForwardAsync(context, remoteBase, net).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Proxy to host {Base} failed", remoteBase);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context
                .Response.WriteAsync("Bad gateway: host unreachable.", context.RequestAborted)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Пути, которые всегда обрабатывает локальный процесс (клиентский UI / управление).</summary>
    internal static bool ShouldBypass(PathString path)
    {
        if (path.StartsWithSegments("/health"))
            return true;
        return false;
    }

    private static Uri BuildTargetUri(HttpRequest request, string remoteBase)
    {
        string relative = (request.Path + request.QueryString).ToString();
        if (string.IsNullOrEmpty(relative))
            relative = "/";
        string baseWithSlash = remoteBase.EndsWith('/') ? remoteBase : remoteBase + "/";
        return new Uri(new Uri(baseWithSlash, UriKind.Absolute), relative);
    }

    private async Task ForwardAsync(HttpContext context, string remoteBase, NetDiscoveryService net)
    {
        Uri target = BuildTargetUri(context.Request, remoteBase);
        using HttpRequestMessage requestMessage = new(
            new HttpMethod(context.Request.Method),
            target
        );

        bool hasBody = RequestMayHaveBody(context.Request);
        foreach (KeyValuePair<string, StringValues> header in context.Request.Headers)
        {
            if (HopByHopRequestHeaders.Contains(header.Key))
                continue;
            if (!header.Key.StartsWith(':'))
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        if (hasBody)
        {
            StreamContent streamContent = new(context.Request.Body);
            string? contentType = context.Request.ContentType;
            if (!string.IsNullOrEmpty(contentType))
                streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            requestMessage.Content = streamContent;
        }

        HttpClient client = _httpClientFactory.CreateClient("hostProxy");

        HttpResponseMessage upstream;
        try
        {
            upstream = await client
                .SendAsync(
                    requestMessage,
                    HttpCompletionOption.ResponseHeadersRead,
                    context.RequestAborted
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            await OnProxyTransportFailureAsync(context, remoteBase, net, ex).ConfigureAwait(false);
            throw;
        }
        catch (HttpRequestException ex)
        {
            await OnProxyTransportFailureAsync(context, remoteBase, net, ex).ConfigureAwait(false);
            throw;
        }
        catch (IOException ex)
        {
            await OnProxyTransportFailureAsync(context, remoteBase, net, ex).ConfigureAwait(false);
            throw;
        }

        using (upstream)
        {
            context.Response.StatusCode = (int)upstream.StatusCode;

            CopySafeResponseHeaders(upstream.Headers, context.Response.Headers);

            if (upstream.Content is { } body)
            {
                CopySafeResponseHeaders(body.Headers, context.Response.Headers);
                await body.CopyToAsync(context.Response.Body, context.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// После таймаута 5 с или обрыва соединения — проверка <c>/health</c> у удалённого хоста; при отсутствии ответа — перезапуск UDP discovery.
    /// </summary>
    private async Task OnProxyTransportFailureAsync(
        HttpContext context,
        string remoteBase,
        NetDiscoveryService net,
        Exception inner
    )
    {
        if (context.RequestAborted.IsCancellationRequested)
            return;

        bool healthOk = await TryRemoteHealthAsync(remoteBase, context.RequestAborted)
            .ConfigureAwait(false);
        if (!healthOk)
        {
            _log.LogWarning(
                inner,
                "Net: proxy failed and remote /health unreachable; restarting UDP"
            );
            net.RestartClientDiscoveryAfterRemoteHostFailure();
        }
        else
        {
            _log.LogWarning(inner, "Net: proxy failed but remote /health OK");
        }
    }

    private async Task<bool> TryRemoteHealthAsync(
        string remoteBase,
        CancellationToken cancellationToken
    )
    {
        HttpClient client = _httpClientFactory.CreateClient("hostProxy");
        Uri healthUri = BuildHealthUri(remoteBase);
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, healthUri);
            using HttpResponseMessage r = await client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static Uri BuildHealthUri(string remoteBase)
    {
        string trimmed = remoteBase.TrimEnd('/');
        return new Uri($"{trimmed}/health");
    }

    private static void CopySafeResponseHeaders(HttpHeaders from, IHeaderDictionary to)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in from)
        {
            if (HopByHopResponseHeaders.Contains(header.Key))
                continue;
            try
            {
                to.Append(header.Key, header.Value.ToArray());
            }
            catch { }
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
