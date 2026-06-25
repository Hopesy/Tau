using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentLatestRelease(string Version, string? PackageName = null, string? Note = null);

public sealed record CodingAgentVersionCheckOptions(int TimeoutMilliseconds = 10_000);

internal static class CodingAgentVersionNotificationFormatter
{
    public static string Format(CodingAgentLatestRelease release, string commandName)
    {
        var lines = new List<string>
        {
            "Update Available",
            $"New version {release.Version} is available. Run {commandName} update"
        };

        if (!string.IsNullOrWhiteSpace(release.Note))
        {
            lines.Add(release.Note.Trim());
        }

        lines.Add("Changelog: https://pi.dev/changelog");
        return string.Join(Environment.NewLine, lines);
    }
}

internal static partial class CodingAgentVersionCheck
{
    public const string LatestVersionUrl = "https://pi.dev/api/latest-version";

    public static int? ComparePackageVersions(string leftVersion, string rightVersion)
    {
        if (!SemanticVersion.TryParse(leftVersion, out var left) ||
            !SemanticVersion.TryParse(rightVersion, out var right))
        {
            return null;
        }

        return left.CompareTo(right);
    }

    public static bool IsNewerPackageVersion(string candidateVersion, string currentVersion)
    {
        var comparison = ComparePackageVersions(candidateVersion, currentVersion);
        return comparison is null
            ? !candidateVersion.Trim().Equals(currentVersion.Trim(), StringComparison.Ordinal)
            : comparison > 0;
    }

    public static async Task<CodingAgentLatestRelease?> GetLatestReleaseAsync(
        string currentVersion,
        CodingAgentVersionCheckOptions? options = null,
        HttpClient? httpClient = null,
        Func<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (IsSet(environment("PI_SKIP_VERSION_CHECK")) || IsSet(environment("PI_OFFLINE")))
        {
            return null;
        }

        var ownsClient = httpClient is null;
        using var client = ownsClient ? new HttpClient() : null;
        var effectiveClient = httpClient ?? client!;
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestVersionUrl);
        request.Headers.UserAgent.ParseAdd(GetPiUserAgent(currentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, options?.TimeoutMilliseconds ?? 10_000)));
        using var response = await effectiveClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var version = versionElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var packageName = ReadTrimmedString(document.RootElement, "packageName");
        var note = ReadTrimmedString(document.RootElement, "note");
        return new CodingAgentLatestRelease(version, packageName, note);
    }

    public static async Task<string?> GetLatestVersionAsync(
        string currentVersion,
        CodingAgentVersionCheckOptions? options = null,
        HttpClient? httpClient = null,
        Func<string, string?>? environment = null,
        CancellationToken cancellationToken = default) =>
        (await GetLatestReleaseAsync(
                currentVersion,
                options,
                httpClient,
                environment,
                cancellationToken)
            .ConfigureAwait(false))?.Version;

    public static async Task<CodingAgentLatestRelease?> CheckForNewVersionAsync(
        string currentVersion,
        CodingAgentVersionCheckOptions? options = null,
        HttpClient? httpClient = null,
        Func<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var latest = await GetLatestReleaseAsync(
                    currentVersion,
                    options,
                    httpClient,
                    environment,
                    cancellationToken)
                .ConfigureAwait(false);
            return latest is not null && IsNewerPackageVersion(latest.Version, currentVersion)
                ? latest
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetPiUserAgent(string currentVersion)
    {
        var runtimeVersion = typeof(CodingAgentVersionCheck).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        var runtime = string.IsNullOrWhiteSpace(runtimeVersion)
            ? $"dotnet/{Environment.Version}"
            : $"dotnet/{Environment.Version} {runtimeVersion}";
        return $"pi/{currentVersion.Trim()} ({RuntimeInformation.OSDescription}; {runtime}; {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";
    }

    private static string? ReadTrimmedString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsSet(string? value) => !string.IsNullOrEmpty(value);

    private sealed record SemanticVersion(
        long Major,
        long Minor,
        long Patch,
        IReadOnlyList<string> Prerelease) : IComparable<SemanticVersion>
    {
        public int CompareTo(SemanticVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            var core = Major.CompareTo(other.Major);
            if (core != 0)
            {
                return core;
            }

            core = Minor.CompareTo(other.Minor);
            if (core != 0)
            {
                return core;
            }

            core = Patch.CompareTo(other.Patch);
            if (core != 0)
            {
                return core;
            }

            return ComparePrerelease(Prerelease, other.Prerelease);
        }

        public static bool TryParse(string value, out SemanticVersion version)
        {
            version = default!;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var match = VersionPattern().Match(value.Trim());
            if (!match.Success ||
                !long.TryParse(match.Groups["major"].Value, out var major) ||
                !long.TryParse(match.Groups["minor"].Value, out var minor) ||
                !long.TryParse(match.Groups["patch"].Value, out var patch))
            {
                return false;
            }

            var prerelease = match.Groups["pre"].Success
                ? match.Groups["pre"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries)
                : [];
            version = new SemanticVersion(major, minor, patch, prerelease);
            return true;
        }

        private static int ComparePrerelease(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left.Count == 0 && right.Count == 0)
            {
                return 0;
            }

            if (left.Count == 0)
            {
                return 1;
            }

            if (right.Count == 0)
            {
                return -1;
            }

            var limit = Math.Min(left.Count, right.Count);
            for (var i = 0; i < limit; i++)
            {
                var comparison = ComparePrereleaseIdentifier(left[i], right[i]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.Count.CompareTo(right.Count);
        }

        private static int ComparePrereleaseIdentifier(string left, string right)
        {
            var leftNumeric = IsNumericIdentifier(left);
            var rightNumeric = IsNumericIdentifier(right);
            if (leftNumeric && rightNumeric)
            {
                return long.Parse(left, System.Globalization.CultureInfo.InvariantCulture)
                    .CompareTo(long.Parse(right, System.Globalization.CultureInfo.InvariantCulture));
            }

            if (leftNumeric)
            {
                return -1;
            }

            if (rightNumeric)
            {
                return 1;
            }

            return string.CompareOrdinal(left, right);
        }

        private static bool IsNumericIdentifier(string value) =>
            value.Length > 0 && value.All(static character => character is >= '0' and <= '9');
    }

    [GeneratedRegex(@"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex VersionPattern();
}
