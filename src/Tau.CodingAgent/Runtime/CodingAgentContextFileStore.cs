using System.Text;

namespace Tau.CodingAgent.Runtime;

public sealed record CodingAgentContextFile(
    string FilePath,
    string Content,
    string Scope);

public sealed class CodingAgentContextFileStore
{
    private static readonly string[] CandidateFileNames = ["AGENTS.md", "CLAUDE.md"];

    private readonly string _cwd;
    private readonly string _userContextDirectory;
    private readonly bool _includeDefaults;

    public CodingAgentContextFileStore(
        string? cwd = null,
        string? userContextDirectory = null,
        bool includeDefaults = true)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : Path.GetFullPath(cwd);
        _userContextDirectory = string.IsNullOrWhiteSpace(userContextDirectory)
            ? GetDefaultUserContextDirectory()
            : Path.GetFullPath(userContextDirectory);
        _includeDefaults = includeDefaults;
    }

    public IReadOnlyList<CodingAgentContextFile> Load()
    {
        if (!_includeDefaults)
        {
            return [];
        }

        var files = new List<CodingAgentContextFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var userContext = LoadFromDirectory(_userContextDirectory, "user");
        if (userContext is not null && seenPaths.Add(userContext.FilePath))
        {
            files.Add(userContext);
        }

        var projectFiles = new List<CodingAgentContextFile>();
        for (var directory = new DirectoryInfo(_cwd);
             directory is not null;
             directory = directory.Parent)
        {
            var contextFile = LoadFromDirectory(directory.FullName, "project");
            if (contextFile is not null && seenPaths.Add(contextFile.FilePath))
            {
                projectFiles.Add(contextFile);
            }
        }

        projectFiles.Reverse();
        files.AddRange(projectFiles);
        return files;
    }

    public static string FormatForSystemPrompt(IReadOnlyList<CodingAgentContextFile> contextFiles)
    {
        if (contextFiles.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("# Project Context");
        builder.AppendLine();
        builder.AppendLine("Project-specific instructions and guidelines:");
        builder.AppendLine();

        foreach (var contextFile in contextFiles)
        {
            builder.Append("## ")
                .Append(NormalizePromptPath(contextFile.FilePath))
                .AppendLine();
            builder.AppendLine();
            builder.AppendLine(contextFile.Content.TrimEnd());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static CodingAgentContextFile? LoadFromDirectory(string directory, string scope)
    {
        foreach (var fileName in CandidateFileNames)
        {
            var filePath = Path.Combine(directory, fileName);
            if (!File.Exists(filePath))
            {
                continue;
            }

            try
            {
                return new CodingAgentContextFile(
                    Path.GetFullPath(filePath),
                    File.ReadAllText(filePath),
                    scope);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return null;
    }

    private static string NormalizePromptPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    private static string GetDefaultUserContextDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tau");
}
