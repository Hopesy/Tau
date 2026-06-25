namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentGitSource(
    string Repo,
    string Host,
    string Path,
    string? Ref,
    bool Pinned);

public static class CodingAgentGitUrlParser
{
    private static readonly IReadOnlyDictionary<string, string> HostedAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["github"] = "github.com",
            ["gitlab"] = "gitlab.com",
            ["bitbucket"] = "bitbucket.org"
        };

    public static bool TryParse(string source, out CodingAgentGitSource parsed)
    {
        parsed = default!;
        var trimmed = source.Trim();
        var hasGitPrefix = trimmed.StartsWith("git:", StringComparison.OrdinalIgnoreCase);
        var raw = hasGitPrefix ? trimmed["git:".Length..].Trim() : trimmed;
        if (raw.Length == 0)
        {
            return false;
        }

        if (!hasGitPrefix && !HasExplicitProtocol(raw))
        {
            return false;
        }

        var split = SplitRef(raw);
        if (TryParseHostedShortcut(split.Repo, split.Ref, out parsed))
        {
            return true;
        }

        return TryParseGeneric(split.Repo, split.Ref, out parsed);
    }

    public static CodingAgentGitSource? Parse(string source) =>
        TryParse(source, out var parsed) ? parsed : null;

    private static bool TryParseHostedShortcut(string repoWithoutRef, string? gitRef, out CodingAgentGitSource parsed)
    {
        parsed = default!;
        var colon = repoWithoutRef.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        var alias = repoWithoutRef[..colon];
        if (!HostedAliases.TryGetValue(alias, out var host))
        {
            return false;
        }

        var path = repoWithoutRef[(colon + 1)..].TrimStart('/');
        return TryBuild(
            repo: $"https://{host}/{path}",
            host,
            path,
            gitRef,
            out parsed);
    }

    private static bool TryParseGeneric(string repoWithoutRef, string? gitRef, out CodingAgentGitSource parsed)
    {
        parsed = default!;
        var repo = repoWithoutRef;
        var host = string.Empty;
        var path = string.Empty;

        if (repoWithoutRef.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = repoWithoutRef.IndexOf(':', StringComparison.Ordinal);
            if (colon <= "git@".Length)
            {
                return false;
            }

            host = repoWithoutRef["git@".Length..colon];
            path = repoWithoutRef[(colon + 1)..];
        }
        else if (repoWithoutRef.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(repoWithoutRef, UriKind.Absolute, out var uri))
            {
                return false;
            }

            host = uri.Host;
            path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimStart('/');
        }
        else
        {
            var slash = repoWithoutRef.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0)
            {
                return false;
            }

            host = repoWithoutRef[..slash];
            path = repoWithoutRef[(slash + 1)..];
            if (!host.Contains('.', StringComparison.Ordinal) && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            repo = $"https://{repoWithoutRef}";
        }

        return TryBuild(repo.TrimEnd('/'), host, path, gitRef, out parsed);
    }

    private static bool TryBuild(
        string repo,
        string host,
        string path,
        string? gitRef,
        out CodingAgentGitSource parsed)
    {
        parsed = default!;
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedPath = path.Trim('/').Trim();
        if (normalizedPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[..^".git".Length];
        }

        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(normalizedPath) ||
            normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2)
        {
            return false;
        }

        if (HasUnsafeGitInstallPart(host, allowSlash: false) ||
            HasUnsafeGitInstallPart(normalizedPath, allowSlash: true))
        {
            return false;
        }

        parsed = new CodingAgentGitSource(
            repo,
            host,
            normalizedPath,
            string.IsNullOrWhiteSpace(gitRef) ? null : gitRef,
            !string.IsNullOrWhiteSpace(gitRef));
        return true;
    }

    private static bool HasExplicitProtocol(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
        source.StartsWith("git://", StringComparison.OrdinalIgnoreCase);

    private static bool HasUnsafeGitInstallPart(string value, bool allowSlash)
    {
        var decoded = DecodeForValidation(value);
        if (decoded is null)
        {
            return true;
        }

        foreach (var candidate in new[] { value, decoded })
        {
            if (candidate.Contains('\0', StringComparison.Ordinal) ||
                candidate.Contains('\\', StringComparison.Ordinal) ||
                candidate.StartsWith("/", StringComparison.Ordinal))
            {
                return true;
            }

            if (!allowSlash && candidate.Contains('/', StringComparison.Ordinal))
            {
                return true;
            }

            if (candidate.Split('/').Contains("..", StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? DecodeForValidation(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static (string Repo, string? Ref) SplitRef(string source)
    {
        var hash = source.LastIndexOf('#');
        if (hash > 0 && hash < source.Length - 1)
        {
            return (source[..hash], source[(hash + 1)..]);
        }

        if (source.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = source.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                return (source, null);
            }

            var pathWithMaybeRef = source[(colon + 1)..];
            var separator = pathWithMaybeRef.IndexOf('@', StringComparison.Ordinal);
            if (separator < 0 || separator == pathWithMaybeRef.Length - 1)
            {
                return (source, null);
            }

            var repoPath = pathWithMaybeRef[..separator];
            var gitRef = pathWithMaybeRef[(separator + 1)..];
            return ($"{source[..(colon + 1)]}{repoPath}", gitRef);
        }

        if (source.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            var pathWithMaybeRef = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimStart('/');
            var separator = pathWithMaybeRef.IndexOf('@', StringComparison.Ordinal);
            if (separator < 0 || separator == pathWithMaybeRef.Length - 1)
            {
                return (source, null);
            }

            var repoPath = pathWithMaybeRef[..separator];
            var builder = new UriBuilder(uri)
            {
                Path = $"/{repoPath}",
                Fragment = string.Empty
            };
            return (builder.Uri.ToString().TrimEnd('/'), pathWithMaybeRef[(separator + 1)..]);
        }

        var slash = source.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return (source, null);
        }

        var host = source[..slash];
        var path = source[(slash + 1)..];
        var refSeparator = path.IndexOf('@', StringComparison.Ordinal);
        if (refSeparator < 0 || refSeparator == path.Length - 1)
        {
            return (source, null);
        }

        return ($"{host}/{path[..refSeparator]}", path[(refSeparator + 1)..]);
    }
}
