using System.Text;
using Tau.Ai;

namespace Tau.CodingAgent.Runtime;

/// <summary>
/// A non-fatal warning or fatal error produced while parsing CLI arguments. Mirrors upstream
/// <c>cli/args.ts</c> <c>diagnostics</c>: <c>warning</c> entries are printed and execution continues,
/// <c>error</c> entries are printed and the process exits with code 1.
/// </summary>
internal sealed record CodingAgentCliDiagnostic(string Type, string Message);

internal sealed record CodingAgentCliArguments(
    bool PrintMode,
    bool RpcMode,
    bool Help,
    bool Version,
    bool NoContextFiles,
    bool NoThemes,
    IReadOnlyList<string> ThemePaths,
    string? Provider,
    string? Model,
    string? SystemPrompt,
    IReadOnlyList<string> AppendSystemPrompt,
    string? Thinking,
    bool Offline,
    bool NoTools,
    IReadOnlyList<string>? Tools,
    IReadOnlyList<CodingAgentCliDiagnostic> Diagnostics,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> FileArguments,
    IReadOnlyDictionary<string, string?> ExtensionFlags)
{
    /// <summary>
    /// Upstream CLI tool names (<c>cli/args.ts</c> <c>allTools</c>) mapped to the Tau tool
    /// <see cref="IAgentTool.Name"/> values they select.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CliToolNameToTauToolName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["read"] = "read_file",
            ["bash"] = "shell",
            ["edit"] = "edit_file",
            ["write"] = "write_file",
            ["grep"] = "grep",
            ["find"] = "glob",
            ["ls"] = "ls"
        };

    private static readonly HashSet<string> OptionsWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "--provider",
        "--model",
        "--api-key",
        "--system-prompt",
        "--session",
        "--fork",
        "--session-dir",
        "--models",
        "--extension",
        "-e",
        "--skill",
        "--prompt-template",
        "--export"
    };

    private static readonly HashSet<string> BooleanOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--continue",
        "-c",
        "--resume",
        "-r",
        "--no-session",
        "--no-extensions",
        "-ne",
        "--no-skills",
        "-ns",
        "--no-prompt-templates",
        "-np",
        "--verbose",
        "--json"
    };

    private static readonly string ValidToolNames = string.Join(", ", CliToolNameToTauToolName.Keys);

    /// <summary>
    /// Valid <c>--thinking</c> levels, mirroring upstream <c>cli/args.ts</c>
    /// <c>VALID_THINKING_LEVELS</c>.
    /// </summary>
    private static readonly IReadOnlyList<string> ValidThinkingLevels =
        ["off", "minimal", "low", "medium", "high", "xhigh"];

    public static CodingAgentCliArguments Parse(IReadOnlyList<string> args)
    {
        var printMode = false;
        var rpcMode = false;
        var help = false;
        var version = false;
        var noContextFiles = false;
        var noThemes = false;
        var themePaths = new List<string>();
        string? provider = null;
        string? model = null;
        string? systemPrompt = null;
        var appendSystemPrompt = new List<string>();
        var messages = new List<string>();
        var fileArguments = new List<string>();
        var extensionFlags = new Dictionary<string, string?>(StringComparer.Ordinal);
        var offline = false;
        var noTools = false;
        List<string>? tools = null;
        string? thinking = null;
        var diagnostics = new List<CodingAgentCliDiagnostic>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--mode", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    rpcMode = args[++i].Equals("rpc", StringComparison.OrdinalIgnoreCase);
                }

                continue;
            }

            if (arg.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))
            {
                rpcMode = arg["--mode=".Length..].Equals("rpc", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                help = true;
                continue;
            }

            if (arg.Equals("--version", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                version = true;
                continue;
            }

            if (arg.Equals("--offline", StringComparison.OrdinalIgnoreCase))
            {
                offline = true;
                continue;
            }

            if (arg.Equals("--print", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-p", StringComparison.OrdinalIgnoreCase))
            {
                printMode = true;
                continue;
            }

            if (arg.StartsWith("--print=", StringComparison.OrdinalIgnoreCase))
            {
                printMode = true;
                var inlinePrompt = arg["--print=".Length..];
                if (!string.IsNullOrWhiteSpace(inlinePrompt))
                {
                    messages.Add(inlinePrompt);
                }

                continue;
            }

            if (arg.Equals("--no-context-files", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-nc", StringComparison.OrdinalIgnoreCase))
            {
                noContextFiles = true;
                continue;
            }

            if (arg.Equals("--no-themes", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-nt", StringComparison.OrdinalIgnoreCase))
            {
                noThemes = true;
                continue;
            }

            if (arg.Equals("--no-tools", StringComparison.OrdinalIgnoreCase))
            {
                noTools = true;
                continue;
            }

            if (arg.Equals("--tools", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--tools=", StringComparison.OrdinalIgnoreCase))
            {
                string? rawTools;
                if (arg.StartsWith("--tools=", StringComparison.OrdinalIgnoreCase))
                {
                    rawTools = arg["--tools=".Length..];
                }
                else if (i + 1 < args.Count)
                {
                    rawTools = args[++i];
                }
                else
                {
                    throw new ArgumentException("error: --tools requires a comma-separated tool list");
                }

                tools ??= new List<string>();
                foreach (var name in rawTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (CliToolNameToTauToolName.TryGetValue(name, out var tauToolName))
                    {
                        if (!tools.Contains(tauToolName, StringComparer.Ordinal))
                        {
                            tools.Add(tauToolName);
                        }
                    }
                    else
                    {
                        diagnostics.Add(new CodingAgentCliDiagnostic(
                            "warning",
                            $"Unknown tool \"{name}\". Valid tools: {ValidToolNames}"));
                    }
                }

                continue;
            }

            if (arg.Equals("--thinking", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--thinking=", StringComparison.OrdinalIgnoreCase))
            {
                string rawThinking;
                if (arg.StartsWith("--thinking=", StringComparison.OrdinalIgnoreCase))
                {
                    rawThinking = arg["--thinking=".Length..];
                }
                else if (i + 1 < args.Count)
                {
                    rawThinking = args[++i];
                }
                else
                {
                    throw new ArgumentException("error: --thinking requires a level argument");
                }

                var normalizedThinking = rawThinking.Trim().ToLowerInvariant();
                if (ValidThinkingLevels.Contains(normalizedThinking, StringComparer.Ordinal))
                {
                    thinking = normalizedThinking;
                }
                else
                {
                    diagnostics.Add(new CodingAgentCliDiagnostic(
                        "warning",
                        $"Invalid thinking level \"{rawThinking}\". Valid values: {string.Join(", ", ValidThinkingLevels)}"));
                }

                continue;
            }

            if (arg.Equals("--theme", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException("error: --theme requires a path argument");
                }

                themePaths.Add(args[++i]);
                continue;
            }

            if (arg.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
            {
                themePaths.Add(arg["--theme=".Length..]);
                continue;
            }

            if (TryConsumeStringOption(args, ref i, "--provider", out var providerValue))
            {
                provider = providerValue;
                continue;
            }

            if (TryConsumeStringOption(args, ref i, "--model", out var modelValue))
            {
                model = modelValue;
                continue;
            }

            if (TryConsumeStringOption(args, ref i, "--system-prompt", out var systemPromptValue))
            {
                systemPrompt = systemPromptValue;
                continue;
            }

            if (TryConsumeStringOption(args, ref i, "--append-system-prompt", out var appendValue))
            {
                if (!string.IsNullOrWhiteSpace(appendValue))
                {
                    appendSystemPrompt.Add(appendValue);
                }

                continue;
            }

            if (arg.StartsWith("@", StringComparison.Ordinal) && arg.Length > 1)
            {
                fileArguments.Add(arg[1..]);
                continue;
            }

            if (OptionsWithValue.Contains(arg))
            {
                if (i + 1 < args.Count)
                {
                    i++;
                }

                continue;
            }

            if (arg.Equals("--list-models", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal) &&
                    !args[i + 1].StartsWith("@", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            if (BooleanOptions.Contains(arg))
            {
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var equalsIndex = arg.IndexOf('=', StringComparison.Ordinal);
                if (equalsIndex >= 0)
                {
                    var inlineName = arg[2..equalsIndex];
                    if (inlineName.Length > 0)
                    {
                        extensionFlags[inlineName] = arg[(equalsIndex + 1)..];
                    }

                    continue;
                }

                var flagName = arg[2..];
                if (flagName.Length == 0)
                {
                    continue;
                }

                if (i + 1 < args.Count &&
                    !args[i + 1].StartsWith("-", StringComparison.Ordinal) &&
                    !args[i + 1].StartsWith("@", StringComparison.Ordinal))
                {
                    extensionFlags[flagName] = args[++i];
                }
                else
                {
                    extensionFlags[flagName] = null;
                }

                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                diagnostics.Add(new CodingAgentCliDiagnostic("error", $"Unknown option: {arg}"));
                continue;
            }

            messages.Add(arg);
        }

        return new CodingAgentCliArguments(
            printMode,
            rpcMode,
            help,
            version,
            noContextFiles,
            noThemes,
            themePaths,
            provider,
            model,
            systemPrompt,
            appendSystemPrompt,
            thinking,
            offline,
            noTools,
            tools,
            diagnostics,
            messages,
            fileArguments,
            extensionFlags);
    }

    private static bool TryConsumeStringOption(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        out string? value)
    {
        var arg = args[index];
        if (arg.Equals(option, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Count)
            {
                throw new ArgumentException($"error: {option} requires an argument");
            }

            value = args[++index];
            return true;
        }

        var prefix = option + "=";
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg[prefix.Length..];
            return true;
        }

        value = null;
        return false;
    }
}

public sealed record CodingAgentInitialPrompt(string Text, IReadOnlyList<ImageContent> Images)
{
    public bool HasImages => Images.Count > 0;

    public IReadOnlyList<ContentBlock> ToContentBlocks()
    {
        var blocks = new List<ContentBlock> { new TextContent(Text) };
        blocks.AddRange(Images);
        return blocks;
    }
}

internal sealed record CodingAgentInitialMessageOptions(
    bool AutoResizeImages = true,
    bool BlockImages = false,
    string? WorkingDirectory = null);

internal static class CodingAgentInitialMessageBuilder
{
    public static async Task<CodingAgentInitialPrompt?> BuildAsync(
        IReadOnlyList<string> messages,
        IReadOnlyList<string> fileArguments,
        string? stdinContent = null,
        CodingAgentInitialMessageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CodingAgentInitialMessageOptions();
        var parts = new List<string>();
        var images = new List<ImageContent>();

        if (stdinContent is not null)
        {
            parts.Add(stdinContent);
        }

        if (fileArguments.Count > 0)
        {
            var processedFiles = await ProcessFileArgumentsAsync(fileArguments, options, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(processedFiles.Text))
            {
                parts.Add(processedFiles.Text);
            }

            images.AddRange(processedFiles.Images);
        }

        if (messages.Count > 0)
        {
            parts.Add(messages[0]);
        }

        var text = string.Concat(parts);
        if (text.Length == 0 && images.Count == 0)
        {
            return null;
        }

        return new CodingAgentInitialPrompt(text, images);
    }

    private static async Task<ProcessedFiles> ProcessFileArgumentsAsync(
        IReadOnlyList<string> fileArguments,
        CodingAgentInitialMessageOptions options,
        CancellationToken cancellationToken)
    {
        var text = string.Empty;
        var images = new List<ImageContent>();
        var cwd = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? Environment.CurrentDirectory
            : options.WorkingDirectory;

        foreach (var fileArgument in fileArguments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveReadPath(fileArgument, cwd);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"File not found: {path}", path);
            }

            var info = new FileInfo(path);
            if (info.Length == 0)
            {
                continue;
            }

            var mimeType = await DetectSupportedImageMimeTypeAsync(path, cancellationToken).ConfigureAwait(false);
            if (mimeType is null)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    text += $"<file name=\"{path}\">\n{content}\n</file>\n";
                }
                catch (Exception ex) when (ex is DecoderFallbackException or IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"Could not read file {path}: {ex.Message}", ex);
                }

                continue;
            }

            if (options.BlockImages)
            {
                text += $"<file name=\"{path}\">[Image blocked by settings.]</file>\n";
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var processed = CodingAgentImagePreprocessor.Process(bytes, mimeType, options.AutoResizeImages);
            if (processed is null)
            {
                var message = options.AutoResizeImages
                    ? "[Image omitted: could not be resized below the inline image size limit.]"
                    : "[Image omitted: exceeds the inline image size limit.]";
                text += $"<file name=\"{path}\">{message}</file>\n";
                continue;
            }

            images.Add(new ImageContent(processed.Data, processed.MimeType));
            var dimensionNote = CodingAgentImagePreprocessor.FormatDimensionNote(processed);
            text += dimensionNote is null
                ? $"<file name=\"{path}\"></file>\n"
                : $"<file name=\"{path}\">{dimensionNote}</file>\n";
        }

        return new ProcessedFiles(text, images);
    }

    private static string ResolveReadPath(string filePath, string cwd)
    {
        var expanded = ExpandPath(NormalizeUnicodeSpaces(NormalizeAtPrefix(filePath)));
        var resolved = Path.IsPathFullyQualified(expanded)
            ? expanded
            : Path.GetFullPath(Path.Combine(cwd, expanded));

        if (File.Exists(resolved))
        {
            return resolved;
        }

        foreach (var variant in CreatePathVariants(resolved))
        {
            if (File.Exists(variant))
            {
                return variant;
            }
        }

        return resolved;
    }

    private static IEnumerable<string> CreatePathVariants(string resolved)
    {
        var narrowNoBreakSpace = resolved
            .Replace(" AM.", "\u202fAM.", StringComparison.OrdinalIgnoreCase)
            .Replace(" PM.", "\u202fPM.", StringComparison.OrdinalIgnoreCase);
        if (!narrowNoBreakSpace.Equals(resolved, StringComparison.Ordinal))
        {
            yield return narrowNoBreakSpace;
        }

        var nfd = resolved.Normalize(NormalizationForm.FormD);
        if (!nfd.Equals(resolved, StringComparison.Ordinal))
        {
            yield return nfd;
        }

        var curly = resolved.Replace("'", "\u2019", StringComparison.Ordinal);
        if (!curly.Equals(resolved, StringComparison.Ordinal))
        {
            yield return curly;
        }

        var nfdCurly = nfd.Replace("'", "\u2019", StringComparison.Ordinal);
        if (!nfdCurly.Equals(resolved, StringComparison.Ordinal) &&
            !nfdCurly.Equals(nfd, StringComparison.Ordinal))
        {
            yield return nfdCurly;
        }
    }

    private static string NormalizeAtPrefix(string filePath) =>
        filePath.StartsWith("@", StringComparison.Ordinal) ? filePath[1..] : filePath;

    private static string NormalizeUnicodeSpaces(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(ch is '\u00a0' or '\u202f' or '\u205f' or '\u3000' ||
                (ch >= '\u2000' && ch <= '\u200a')
                    ? ' '
                    : ch);
        }

        return builder.ToString();
    }

    private static string ExpandPath(string filePath)
    {
        if (filePath == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (filePath.StartsWith("~/", StringComparison.Ordinal) ||
            filePath.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                filePath[2..]);
        }

        return filePath;
    }

    private static async Task<string?> DetectSupportedImageMimeTypeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16];
        await using var stream = File.OpenRead(path);
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
            .ConfigureAwait(false);
        if (bytesRead == 0)
        {
            return null;
        }

        if (bytesRead >= 8 &&
            buffer[0] == 0x89 &&
            buffer[1] == 0x50 &&
            buffer[2] == 0x4e &&
            buffer[3] == 0x47 &&
            buffer[4] == 0x0d &&
            buffer[5] == 0x0a &&
            buffer[6] == 0x1a &&
            buffer[7] == 0x0a)
        {
            return "image/png";
        }

        if (bytesRead >= 3 && buffer[0] == 0xff && buffer[1] == 0xd8 && buffer[2] == 0xff)
        {
            return "image/jpeg";
        }

        if (bytesRead >= 6 &&
            buffer[0] == 'G' &&
            buffer[1] == 'I' &&
            buffer[2] == 'F' &&
            buffer[3] == '8' &&
            (buffer[4] == '7' || buffer[4] == '9') &&
            buffer[5] == 'a')
        {
            return "image/gif";
        }

        if (bytesRead >= 12 &&
            buffer[0] == 'R' &&
            buffer[1] == 'I' &&
            buffer[2] == 'F' &&
            buffer[3] == 'F' &&
            buffer[8] == 'W' &&
            buffer[9] == 'E' &&
            buffer[10] == 'B' &&
            buffer[11] == 'P')
        {
            return "image/webp";
        }

        return null;
    }

    private sealed record ProcessedFiles(string Text, IReadOnlyList<ImageContent> Images);
}
