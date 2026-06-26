using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentGitUrlParserTests
{
    [Theory]
    [InlineData("git:github.com/example/pkg@main", "https://github.com/example/pkg", "github.com", "example/pkg", "main", true)]
    [InlineData("git:user/repo#v2", "https://github.com/user/repo", "github.com", "user/repo", "v2", true)]
    [InlineData("git:github:user/repo#v1.2.3", "https://github.com/user/repo", "github.com", "user/repo", "v1.2.3", true)]
    [InlineData("git:gist:user/abcdef#v1", "https://gist.github.com/user/abcdef", "gist.github.com", "user/abcdef", "v1", true)]
    [InlineData("git:sourcehut:~user/repo#main", "https://git.sr.ht/~user/repo", "git.sr.ht", "~user/repo", "main", true)]
    [InlineData("git:git@gitlab.com:group/repo@feature/foo", "git@gitlab.com:group/repo", "gitlab.com", "group/repo", "feature/foo", true)]
    [InlineData("https://github.com/user/repo.git", "https://github.com/user/repo.git", "github.com", "user/repo", null, false)]
    [InlineData("https://www.github.com/user/repo.git#v1", "https://www.github.com/user/repo.git", "github.com", "user/repo", "v1", true)]
    [InlineData("https://github.com/user/repo/tree/release", "https://github.com/user/repo/tree/release", "github.com", "user/repo", "release", true)]
    [InlineData("https://gitlab.com/group/subgroup/repo.git#v3", "https://gitlab.com/group/subgroup/repo.git", "gitlab.com", "group/subgroup/repo", "v3", true)]
    [InlineData("https://bitbucket.org/team/repo.git#stable", "https://bitbucket.org/team/repo.git", "bitbucket.org", "team/repo", "stable", true)]
    [InlineData("https://gist.github.com/user/abcdef.git#rev", "https://gist.github.com/user/abcdef.git", "gist.github.com", "user/abcdef", "rev", true)]
    [InlineData("https://git.sr.ht/~user/repo#tip", "https://git.sr.ht/~user/repo", "git.sr.ht", "~user/repo", "tip", true)]
    [InlineData("ssh://git@example.com/org/repo@release", "ssh://git@example.com/org/repo", "example.com", "org/repo", "release", true)]
    [InlineData("git:localhost/org/repo", "https://localhost/org/repo", "localhost", "org/repo", null, false)]
    public void TryParse_ReturnsGitSourceForSupportedForms(
        string source,
        string repo,
        string host,
        string path,
        string? gitRef,
        bool pinned)
    {
        Assert.True(CodingAgentGitUrlParser.TryParse(source, out var parsed));

        Assert.Equal(repo, parsed.Repo);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(path, parsed.Path);
        Assert.Equal(gitRef, parsed.Ref);
        Assert.Equal(pinned, parsed.Pinned);
    }

    [Theory]
    [InlineData("github.com/example/pkg")]
    [InlineData("git:github.com/example/../pkg")]
    [InlineData("git:github.com/example/%2e%2e/pkg")]
    [InlineData("git:https://github.com/example/%2e%2e/pkg")]
    [InlineData("git:git@evil.example:../../victim/repo")]
    [InlineData("https://evil.example/..%2F..%2Fvictim/repo")]
    [InlineData("https://evil.example/..%2F..%2Fvictim/repo%")]
    [InlineData("git:git@evil.example:/absolute/repo")]
    [InlineData("git:git@evil.example:user\\repo/name")]
    [InlineData("git:git@evil.example:user/repo\0name")]
    [InlineData("git:github.com/example\\pkg")]
    [InlineData("git:github.com/example")]
    [InlineData("git:/example/pkg")]
    [InlineData("npm:@scope/pkg")]
    public void TryParse_RejectsNonGitOrUnsafeForms(string source)
    {
        Assert.False(CodingAgentGitUrlParser.TryParse(source, out _));
    }
}
