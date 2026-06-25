using System.Net;

namespace Tau.Ai;

public static class NodeHttpProxyResolver
{
    public const string UnsupportedProxyProtocolMessage =
        "Unsupported proxy protocol. SOCKS and PAC proxy URLs are not supported; use an HTTP or HTTPS proxy URL.";

    private static readonly IReadOnlyDictionary<string, int> DefaultProxyPorts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ftp"] = 21,
            ["gopher"] = 70,
            ["http"] = 80,
            ["https"] = 443,
            ["ws"] = 80,
            ["wss"] = 443,
        };

    public static Uri? ResolveHttpProxyUriForTarget(
        string targetUrl,
        IReadOnlyDictionary<string, string>? env = null)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsedTarget) ||
            string.IsNullOrWhiteSpace(parsedTarget.Scheme) ||
            string.IsNullOrWhiteSpace(parsedTarget.Host))
        {
            return null;
        }

        return ResolveHttpProxyUriForTarget(parsedTarget, env);
    }

    public static Uri? ResolveHttpProxyUriForTarget(
        Uri targetUrl,
        IReadOnlyDictionary<string, string>? env = null)
    {
        ArgumentNullException.ThrowIfNull(targetUrl);

        if (string.IsNullOrWhiteSpace(targetUrl.Scheme) || string.IsNullOrWhiteSpace(targetUrl.Host))
        {
            return null;
        }

        var protocol = targetUrl.Scheme.TrimEnd(':').ToLowerInvariant();
        var port = targetUrl.IsDefaultPort
            ? DefaultProxyPorts.GetValueOrDefault(protocol)
            : targetUrl.Port;
        if (!ShouldProxyHostname(targetUrl.Host, port, env))
        {
            return null;
        }

        var proxy = GetProxyEnv($"{protocol}_proxy", env) ?? GetProxyEnv("all_proxy", env);
        if (string.IsNullOrWhiteSpace(proxy))
        {
            return null;
        }

        if (!proxy.Contains("://", StringComparison.Ordinal))
        {
            proxy = $"{protocol}://{proxy}";
        }

        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri))
        {
            throw new InvalidOperationException($"Invalid proxy URL {JsonString(proxy)}.");
        }

        if (proxyUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{UnsupportedProxyProtocolMessage} Got {proxyUri.Scheme}:");
        }

        return proxyUri;
    }

    public static IWebProxy? CreateEnvironmentProxy(IReadOnlyDictionary<string, string>? env = null)
    {
        return HasAnyProxyEnvironment(env) ? new NodeHttpEnvironmentProxy(env) : null;
    }

    private static bool HasAnyProxyEnvironment(IReadOnlyDictionary<string, string>? env)
    {
        return GetProxyEnv("http_proxy", env) is not null ||
            GetProxyEnv("https_proxy", env) is not null ||
            GetProxyEnv("all_proxy", env) is not null;
    }

    private static string? GetProxyEnv(string key, IReadOnlyDictionary<string, string>? env)
    {
        var lower = key.ToLowerInvariant();
        var upper = key.ToUpperInvariant();
        return ProviderEnvironment.GetValue(lower, env) ?? ProviderEnvironment.GetValue(upper, env);
    }

    private static bool ShouldProxyHostname(
        string hostname,
        int port,
        IReadOnlyDictionary<string, string>? env)
    {
        var noProxy = GetProxyEnv("no_proxy", env)?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(noProxy))
        {
            return true;
        }

        if (noProxy == "*")
        {
            return false;
        }

        foreach (var entry in noProxy.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (MatchesNoProxyEntry(hostname.ToLowerInvariant(), port, entry))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesNoProxyEntry(string hostname, int port, string entry)
    {
        var proxyHostname = entry;
        var proxyPort = 0;
        var colonIndex = entry.LastIndexOf(':');
        if (colonIndex > 0 &&
            colonIndex < entry.Length - 1 &&
            int.TryParse(entry[(colonIndex + 1)..], out var parsedPort))
        {
            proxyHostname = entry[..colonIndex];
            proxyPort = parsedPort;
        }

        if (proxyPort != 0 && proxyPort != port)
        {
            return false;
        }

        if (proxyHostname.Length == 0)
        {
            return false;
        }

        if (proxyHostname[0] is not ('.' or '*'))
        {
            return string.Equals(hostname, proxyHostname, StringComparison.OrdinalIgnoreCase);
        }

        if (proxyHostname.StartsWith('*'))
        {
            proxyHostname = proxyHostname[1..];
        }

        return hostname.EndsWith(proxyHostname, StringComparison.OrdinalIgnoreCase);
    }

    private static string JsonString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private sealed class NodeHttpEnvironmentProxy(IReadOnlyDictionary<string, string>? env) : IWebProxy
    {
        public ICredentials? Credentials { get; set; } = ProxyUriCredentials.Instance;

        public Uri GetProxy(Uri destination)
        {
            return ResolveHttpProxyUriForTarget(destination, env) ?? destination;
        }

        public bool IsBypassed(Uri host)
        {
            return ResolveHttpProxyUriForTarget(host, env) is null;
        }
    }

    private sealed class ProxyUriCredentials : ICredentials
    {
        public static readonly ProxyUriCredentials Instance = new();

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            if (string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                return null;
            }

            var separator = uri.UserInfo.IndexOf(':');
            var userName = separator < 0
                ? Uri.UnescapeDataString(uri.UserInfo)
                : Uri.UnescapeDataString(uri.UserInfo[..separator]);
            var password = separator < 0
                ? string.Empty
                : Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
            return new NetworkCredential(userName, password);
        }
    }
}

public static class TauHttpClientFactory
{
    public static HttpClient Create(IReadOnlyDictionary<string, string>? env = null)
    {
        return new HttpClient(CreateHandler(env), disposeHandler: true);
    }

    public static HttpMessageHandler CreateHandler(IReadOnlyDictionary<string, string>? env = null)
    {
        var proxy = NodeHttpProxyResolver.CreateEnvironmentProxy(env);
        if (proxy is null)
        {
            return new HttpClientHandler();
        }

        return new HttpClientHandler
        {
            Proxy = proxy,
            UseProxy = true
        };
    }
}
