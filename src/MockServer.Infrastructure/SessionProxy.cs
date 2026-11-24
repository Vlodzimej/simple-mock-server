using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using MockServer.Core;
using Yarp.ReverseProxy.Forwarder;

namespace MockServer.Infrastructure;

public sealed class SessionProxy(IHttpForwarder forwarder, MockInstance instance) : IDisposable
{
    private readonly HttpMessageInvoker client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5)
    });

    public async Task Send(HttpContext context, MockSession session, string path, CancellationToken token)
    {
        if (!session.ProxyEnabled || session.UpstreamBaseUrl.Length == 0)
            throw new SessionException("Правило не найдено; прокси выключен или upstreamBaseUrl не настроен", 502);
        if (UpstreamAddress.Validate(session.UpstreamBaseUrl, context) is { } error)
            throw new SessionException(error, 502);
        var hops = context.Request.Headers[MockInstance.Header].ToString();
        context.Request.Headers[MockInstance.Header] = string.IsNullOrEmpty(hops) ? instance.Id : $"{hops},{instance.Id}";
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        string suffix;
        if (!string.IsNullOrEmpty(rawTarget) && rawTarget.StartsWith('/'))
        {
            var queryIndex = rawTarget.IndexOf('?');
            var rawPath = queryIndex < 0 ? rawTarget : rawTarget[..queryIndex];
            var sessionStart = rawPath.IndexOf('/', 1);
            var suffixStart = sessionStart < 0 ? -1 : rawPath.IndexOf('/', sessionStart + 1);
            suffix = (suffixStart < 0 ? "/" : rawPath[suffixStart..]) + context.Request.QueryString.Value;
        }
        else suffix = path + context.Request.QueryString.Value;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await forwarder.SendAsync(context, session.UpstreamBaseUrl.TrimEnd('/'), client,
                new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(30) }, new SessionTransformer(suffix), deadline.Token);
            if (result != ForwarderError.None)
            {
                var timedOut = deadline.IsCancellationRequested && !token.IsCancellationRequested;
                context.Items["SessionProxyError"] = timedOut ? "Истекло время ожидания backend" : "Ошибка соединения с backend";
                if (context.Response.HasStarted) context.Abort();
                else throw new SessionException((string)context.Items["SessionProxyError"]!, timedOut ? 504 : 502);
            }
        }
        finally
        {
            if (hops.Length == 0) context.Request.Headers.Remove(MockInstance.Header);
            else context.Request.Headers[MockInstance.Header] = hops;
        }
    }

    private sealed class SessionTransformer(string suffix) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await HttpTransformer.Default.TransformRequestAsync(context, proxyRequest, destinationPrefix, cancellationToken);
            // Сохранение исходного escaping пути после удаления префикса сессии.
            proxyRequest.RequestUri = new Uri(destinationPrefix.TrimEnd('/') + suffix,
                new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        }
    }

    public void Dispose() => client.Dispose();
}
