using Tau.Tui.Runtime;

namespace Tau.Tui.Tests;

public sealed class TuiAutocompleteProviderTests
{
    [Fact]
    public async Task GetSuggestionsAsync_FiltersSlashCommandsAndApplyAddsTrailingSpace()
    {
        var provider = new TuiCombinedAutocompleteProvider(
            [
                new TuiSlashCommand("model", "Select model", "[search]"),
                new TuiSlashCommand("metadata", "Inspect metadata"),
                new TuiSlashCommand("quit", "Exit")
            ],
            basePath: Environment.CurrentDirectory);

        var suggestions = await provider.GetSuggestionsAsync("/mo", cursorIndex: 3);

        Assert.NotNull(suggestions);
        var item = Assert.Single(suggestions!.Items);
        Assert.Equal("model", item.Value);
        Assert.Equal("/mo", suggestions.Prefix);
        Assert.Equal("[search] - Select model", item.Description);

        var result = provider.ApplyCompletion("/mo", 3, new TuiAutocompleteItem("model", "model"), "/mo");

        Assert.Equal("/model ", result.Text);
        Assert.Equal("/model ".Length, result.CursorIndex);
    }

    [Fact]
    public async Task GetSuggestionsAsync_UsesCommandArgumentCompletions()
    {
        var provider = new TuiCombinedAutocompleteProvider(
            [
                new TuiSlashCommand(
                    "theme",
                    GetArgumentCompletionsAsync: (prefix, _) => ValueTask.FromResult<IReadOnlyList<TuiAutocompleteItem>?>(
                        prefix == "d" ? [new TuiAutocompleteItem("dark", "dark")] : []))
            ],
            basePath: Environment.CurrentDirectory);

        var suggestions = await provider.GetSuggestionsAsync("/theme d", cursorIndex: "/theme d".Length);

        Assert.NotNull(suggestions);
        Assert.Equal("d", suggestions!.Prefix);
        Assert.Equal("dark", Assert.Single(suggestions.Items).Value);
    }

    [Fact]
    public async Task GetSuggestionsAsync_CompletesRelativeFilePaths()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "script.cs"), "class C {}");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "notes.md"), "# notes");

        var provider = new TuiCombinedAutocompleteProvider(basePath: temp.Path);

        var suggestions = await provider.GetSuggestionsAsync("./s", cursorIndex: 3);

        Assert.NotNull(suggestions);
        Assert.Equal("./s", suggestions!.Prefix);
        Assert.Equal(["src/", "script.cs"], suggestions.Items.Select(item => item.Label).ToArray());

        var result = provider.ApplyCompletion("./s", 3, suggestions.Items[0], "./s");

        Assert.Equal("./src/", result.Text);
        Assert.Equal("./src/".Length, result.CursorIndex);
    }

    [Fact]
    public async Task GetSuggestionsAsync_CompletesAtFileAttachmentAndQuotesSpaces()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "two words.txt"), "hello");

        var provider = new TuiCombinedAutocompleteProvider(basePath: temp.Path);

        var suggestions = await provider.GetSuggestionsAsync("@two", cursorIndex: 4);

        Assert.NotNull(suggestions);
        var item = Assert.Single(suggestions!.Items);
        Assert.Equal("@\"two words.txt\"", item.Value);

        var result = provider.ApplyCompletion("@two", 4, item, "@two");

        Assert.Equal("@\"two words.txt\" ", result.Text);
        Assert.Equal("@\"two words.txt\" ".Length, result.CursorIndex);
    }

    [Fact]
    public async Task GetSuggestionsAsync_ForceReturnsRootPathSuggestions()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "alpha"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "beta.txt"), "hello");

        var provider = new TuiCombinedAutocompleteProvider(basePath: temp.Path);

        var suggestions = await provider.GetSuggestionsAsync(string.Empty, cursorIndex: 0, force: true);

        Assert.NotNull(suggestions);
        Assert.Equal(string.Empty, suggestions!.Prefix);
        Assert.Equal(["alpha/", "beta.txt"], suggestions.Items.Select(item => item.Label).ToArray());
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tau-tui-autocomplete-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
