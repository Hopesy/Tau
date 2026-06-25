using System.Net;
using System.Text;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentVersionCheckTests
{
    [Fact]
    public void ComparePackageVersions_OrdersStableAndPrereleaseVersions()
    {
        Assert.True(CodingAgentVersionCheck.ComparePackageVersions("0.70.6", "0.70.5") > 0);
        Assert.Equal(0, CodingAgentVersionCheck.ComparePackageVersions("0.70.5", "0.70.5"));
        Assert.True(CodingAgentVersionCheck.ComparePackageVersions("0.70.4", "0.70.5") < 0);
        Assert.True(CodingAgentVersionCheck.ComparePackageVersions("5.0.0-beta.20", "5.0.0-beta.9") > 0);
        Assert.True(CodingAgentVersionCheck.ComparePackageVersions("5.0.0", "5.0.0-beta.20") > 0);
        Assert.Null(CodingAgentVersionCheck.ComparePackageVersions("latest", "0.70.5"));
        Assert.False(CodingAgentVersionCheck.IsNewerPackageVersion("0.70.5", "0.70.5"));
        Assert.True(CodingAgentVersionCheck.IsNewerPackageVersion("0.70.6", "0.70.5"));
        Assert.True(CodingAgentVersionCheck.IsNewerPackageVersion("latest", "0.70.5"));
    }

    [Fact]
    public async Task CheckForNewVersionAsync_ReturnsOnlyNewerVersions()
    {
        using var client = CreateClient(_ => JsonResponse("""{"version":"1.2.3"}"""));

        Assert.Null(await CodingAgentVersionCheck.CheckForNewVersionAsync(
            "1.2.3",
            httpClient: client,
            environment: NoEnvironment));
        Assert.Equal(new CodingAgentLatestRelease("1.2.3"), await CodingAgentVersionCheck.CheckForNewVersionAsync(
            "1.2.2",
            httpClient: client,
            environment: NoEnvironment));
    }

    [Fact]
    public async Task GetLatestVersionAsync_UsesLatestVersionApiWithPiUserAgent()
    {
        HttpRequestMessage? capturedRequest = null;
        using var client = CreateClient(request =>
        {
            capturedRequest = request;
            return JsonResponse("""{"version":"1.2.4"}""");
        });

        var version = await CodingAgentVersionCheck.GetLatestVersionAsync(
            "1.2.3",
            httpClient: client,
            environment: NoEnvironment);

        Assert.Equal("1.2.4", version);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest.Method);
        Assert.Equal(CodingAgentVersionCheck.LatestVersionUrl, capturedRequest.RequestUri?.ToString());
        Assert.StartsWith("pi/1.2.3 ", capturedRequest.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            capturedRequest.Headers.Accept,
            header => header.MediaType == "application/json");
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ReturnsPackageMetadataAndTrimmedNote()
    {
        using var client = CreateClient(_ => JsonResponse("""
            {
              "packageName": " @new-scope/pi ",
              "version": " 1.2.4 ",
              "note": " **Read this** "
            }
            """));

        var release = await CodingAgentVersionCheck.GetLatestReleaseAsync(
            "1.2.3",
            httpClient: client,
            environment: NoEnvironment);

        Assert.Equal(new CodingAgentLatestRelease("1.2.4", "@new-scope/pi", "**Read this**"), release);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_SkipsApiCallsWhenVersionChecksAreDisabled()
    {
        var calls = 0;
        using var client = CreateClient(_ =>
        {
            calls++;
            return JsonResponse("""{"version":"1.2.4"}""");
        });

        var release = await CodingAgentVersionCheck.GetLatestReleaseAsync(
            "1.2.3",
            httpClient: client,
            environment: name => name == "PI_SKIP_VERSION_CHECK" ? "1" : null);

        Assert.Null(release);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void VersionNotificationFormatter_RendersUpdateInstructionNoteAndChangelog()
    {
        var text = CodingAgentVersionNotificationFormatter.Format(
            new CodingAgentLatestRelease("1.2.4", Note: " **Read this** "),
            "pi");

        Assert.Contains("Update Available", text, StringComparison.Ordinal);
        Assert.Contains("New version 1.2.4 is available. Run pi update", text, StringComparison.Ordinal);
        Assert.Contains("**Read this**", text, StringComparison.Ordinal);
        Assert.Contains("Changelog: https://pi.dev/changelog", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForNewVersionAsync_SwallowsNetworkAndParseFailures()
    {
        using var throwingClient = CreateClient(_ => throw new HttpRequestException("offline"));
        using var invalidJsonClient = CreateClient(_ => JsonResponse("""{"version":""}"""));

        Assert.Null(await CodingAgentVersionCheck.CheckForNewVersionAsync(
            "1.2.3",
            httpClient: throwingClient,
            environment: NoEnvironment));
        Assert.Null(await CodingAgentVersionCheck.CheckForNewVersionAsync(
            "1.2.3",
            httpClient: invalidJsonClient,
            environment: NoEnvironment));
    }

    private static string? NoEnvironment(string name) => null;

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new DelegateHandler(handler));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
