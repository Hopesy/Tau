using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentGitUrlParserTests
{
    [Theory]
    [InlineData("git:github.com/example/pkg@main", "https://github.com/example/pkg", "github.com", "example/pkg", "main", true)]
    [InlineData("git:github:user/repo#v1.2.3", "https://github.com/user/repo", "github.com", "user/repo", "v1.2.3", true)]
    [InlineData("git:git@gitlab.com:group/repo@feature/foo", "git@gitlab.com:group/repo", "gitlab.com", "group/repo", "feature/foo", true)]
    [InlineData("https://github.com/user/repo.git", "https://github.com/user/repo.git", "github.com", "user/repo", null, false)]
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
    [InlineData("git:github.com/example\\pkg")]
    [InlineData("git:github.com/example")]
    [InlineData("git:/example/pkg")]
    [InlineData("npm:@scope/pkg")]
    public void TryParse_RejectsNonGitOrUnsafeForms(string source)
    {
        Assert.False(CodingAgentGitUrlParser.TryParse(source, out _));
    }
}
