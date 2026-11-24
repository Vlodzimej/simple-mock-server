using System.Net;
using System.Net.NetworkInformation;
using Microsoft.AspNetCore.Http;

namespace MockServer.Infrastructure;

public static class UpstreamAddress
{
    public static string? Validate(string? value, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            return "Нужен HTTP(S) URL без логина, query и fragment";
        var localPort = context.Connection.LocalPort > 0 ? context.Connection.LocalPort : context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
        if (url.Port != localPort) return null;
        if (url.Host.Equals(context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)) return "Upstream указывает на этот сервер";
        var host = url.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "Upstream указывает на этот сервер";
        if (IPAddress.TryParse(host, out var address) && (IPAddress.IsLoopback(address) || NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(network => network.GetIPProperties().UnicastAddresses).Any(item => item.Address.Equals(address))))
            return "Upstream указывает на этот сервер";
        return null;
    }
}
