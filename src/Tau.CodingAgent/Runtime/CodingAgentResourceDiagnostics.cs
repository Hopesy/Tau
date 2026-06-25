namespace Tau.CodingAgent.Runtime;

public static class CodingAgentResourceTypes
{
    public const string Extension = "extension";
    public const string Skill = "skill";
    public const string Prompt = "prompt";
    public const string Theme = "theme";
}

public static class CodingAgentResourceDiagnosticTypes
{
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Collision = "collision";
}

public sealed record CodingAgentResourceCollision(
    string ResourceType,
    string Name,
    string WinnerPath,
    string LoserPath,
    string? WinnerSource = null,
    string? LoserSource = null);

public sealed record CodingAgentResourceDiagnostic(
    string Type,
    string Message,
    string? Path = null,
    CodingAgentResourceCollision? Collision = null);

public static class CodingAgentResourceDiagnostics
{
    public static CodingAgentResourceDiagnostic FromExtension(CodingAgentExtensionDiagnostic diagnostic) =>
        new(
            NormalizeType(diagnostic.Severity),
            diagnostic.Message,
            NormalizePath(diagnostic.Path));

    public static CodingAgentResourceDiagnostic FromPackage(CodingAgentPackageDiagnostic diagnostic) =>
        new(
            NormalizeType(diagnostic.Severity),
            diagnostic.Message,
            NormalizePath(diagnostic.Source));

    public static CodingAgentResourceDiagnostic FromTheme(CodingAgentThemeDiagnostic diagnostic) =>
        new(
            diagnostic.Collision is null
                ? NormalizeType(diagnostic.Severity)
                : CodingAgentResourceDiagnosticTypes.Collision,
            diagnostic.Message,
            NormalizePath(diagnostic.Path),
            diagnostic.Collision);

    public static IReadOnlyList<CodingAgentResourceDiagnostic> FromExtensions(
        IReadOnlyList<CodingAgentExtensionDiagnostic> diagnostics) =>
        diagnostics.Select(FromExtension).ToArray();

    public static IReadOnlyList<CodingAgentResourceDiagnostic> FromPackages(
        IReadOnlyList<CodingAgentPackageDiagnostic> diagnostics) =>
        diagnostics.Select(FromPackage).ToArray();

    public static IReadOnlyList<CodingAgentResourceDiagnostic> FromThemes(
        IReadOnlyList<CodingAgentThemeDiagnostic> diagnostics) =>
        diagnostics.Select(FromTheme).ToArray();

    public static CodingAgentResourceDiagnostic Collision(
        string resourceType,
        string name,
        string winnerPath,
        string loserPath,
        string? winnerSource = null,
        string? loserSource = null,
        string? message = null)
    {
        var collision = new CodingAgentResourceCollision(
            resourceType,
            name,
            winnerPath,
            loserPath,
            winnerSource,
            loserSource);
        return new CodingAgentResourceDiagnostic(
            CodingAgentResourceDiagnosticTypes.Collision,
            string.IsNullOrWhiteSpace(message)
                ? $"{resourceType} \"{name}\" collision; {winnerPath} wins over {loserPath}"
                : message.Trim(),
            winnerPath,
            collision);
    }

    private static string NormalizeType(string type) =>
        type.Equals(CodingAgentResourceDiagnosticTypes.Error, StringComparison.OrdinalIgnoreCase)
            ? CodingAgentResourceDiagnosticTypes.Error
            : CodingAgentResourceDiagnosticTypes.Warning;

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();
}
