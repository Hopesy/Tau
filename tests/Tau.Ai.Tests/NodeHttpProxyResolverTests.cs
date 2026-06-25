using System.Net;

namespace Tau.Ai.Tests;

public sealed class NodeHttpProxyResolverTests
{
    [Fact]
    public void ResolveHttpProxyUriForTarget_UsesProtocolProxyWithCaseInsensitiveEnv()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTPS_PROXY"] = "https://proxy.example.test:8443"
        };

        var proxy = NodeHttpProxyResolver.ResolveHttpProxyUriForTarget("https://api.openai.com/v1/responses", env);

        Assert.Equal("https://proxy.example.test:8443/", proxy?.ToString());
    }

    [Fact]
    public void ResolveHttpProxyUriForTarget_AddsSchemeWhenProxyOmitsOne()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http_proxy"] = "proxy.example.test:8080"
        };

        var proxy = NodeHttpProxyResolver.ResolveHttpProxyUriForTarget("http://api.example.test/v1", env);

        Assert.Equal("http://proxy.example.test:8080/", proxy?.ToString());
    }

    [Fact]
    public void ResolveHttpProxyUriForTarget_FallsBackToAllProxy()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ALL_PROXY"] = "http://proxy.example.test:8080"
        };

        var proxy = NodeHttpProxyResolver.ResolveHttpProxyUriForTarget("https://api.anthropic.com/v1/messages", env);

        Assert.Equal("http://proxy.example.test:8080/", proxy?.ToString());
    }

    [Theory]
    [InlineData("api.openai.com", "api.openai.com", true)]
    [InlineData("api.openai.com", ".openai.com", true)]
    [InlineData("api.openai.com", "*.openai.com", true)]
    [InlineData("api.openai.com:443", "api.openai.com:443", true)]
    [InlineData("api.openai.com:443", "api.openai.com:8443", false)]
    public void ResolveHttpProxyUriForTarget_HonorsNoProxyEntries(
        string targetHost,
        string noProxy,
        bool bypassed)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTPS_PROXY"] = "http://proxy.example.test:8080",
            ["NO_PROXY"] = noProxy
        };
        var target = $"https://{targetHost}/v1/responses";

        var proxy = NodeHttpProxyResolver.ResolveHttpProxyUriForTarget(target, env);

        if (bypassed)
        {
            Assert.Null(proxy);
        }
        else
        {
            Assert.Equal("http://proxy.example.test:8080/", proxy?.ToString());
        }
    }

    [Fact]
    public void ResolveHttpProxyUriForTarget_HonorsNoProxyWildcard()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTPS_PROXY"] = "http://proxy.example.test:8080",
            ["NO_PROXY"] = "*"
        };

        var proxy = NodeHttpProxyResolver.ResolveHttpProxyUriForTarget("https://api.openai.com/v1/responses", env);

        Assert.Null(proxy);
    }

    [Fact]
    public void ResolveHttpProxyUriForTarget_RejectsUnsupportedProxyProtocols()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTPS_PROXY"] = "socks5://proxy.example.test:1080"
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => NodeHttpProxyResolver.ResolveHttpProxyUriForTarget("https://api.openai.com/v1/responses", env));

        Assert.Contains(NodeHttpProxyResolver.UnsupportedProxyProtocolMessage, error.Message, StringComparison.Ordinal);
        Assert.Contains("socks5:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentProxy_UsesTargetSpecificProxyAndBypassRules()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTPS_PROXY"] = "http://secure-proxy.example.test:8080",
            ["HTTP_PROXY"] = "http://plain-proxy.example.test:8080",
            ["NO_PROXY"] = "metadata.google.internal"
        };
        var proxy = Assert.IsAssignableFrom<IWebProxy>(NodeHttpProxyResolver.CreateEnvironmentProxy(env));

        var httpsProxy = proxy.GetProxy(new Uri("https://api.openai.com/v1/responses"));
        var httpProxy = proxy.GetProxy(new Uri("http://api.example.test/v1"));

        Assert.NotNull(httpsProxy);
        Assert.NotNull(httpProxy);
        Assert.Equal("http://secure-proxy.example.test:8080/", httpsProxy.ToString());
        Assert.Equal("http://plain-proxy.example.test:8080/", httpProxy.ToString());
        Assert.True(proxy.IsBypassed(new Uri("https://metadata.google.internal/computeMetadata/v1")));
    }

    [Fact]
    public void EnvironmentProxy_ExposesCredentialsFromProxyUriUserInfo()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTPS_PROXY"] = "http://user%40name:p%40ss@proxy.example.test:8080"
        };
        var proxy = Assert.IsAssignableFrom<IWebProxy>(NodeHttpProxyResolver.CreateEnvironmentProxy(env));
        var proxyUri = proxy.GetProxy(new Uri("https://api.openai.com/v1/responses"));

        Assert.NotNull(proxyUri);
        var credential = proxy.Credentials?.GetCredential(proxyUri, "Basic");

        Assert.NotNull(credential);
        Assert.Equal("user@name", credential.UserName);
        Assert.Equal("p@ss", credential.Password);
    }

    [Fact]
    public void ResolveHttpProxyUriForTarget_ReadsAmbientEnvironmentWhenScopedEnvMissing()
    {
        using var environment = EnvironmentVariableScope.Acquire();
        environment.Set("HTTPS_PROXY", "http://ambient-proxy.example.test:8080");
        environment.Set("NO_PROXY", null);

        var proxy = NodeHttpProxyResolver.ResolveHttpProxyUriForTarget("https://api.openai.com/v1/responses");

        Assert.Equal("http://ambient-proxy.example.test:8080/", proxy?.ToString());
    }
}
