using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentExternalEditorTests
{
    [Fact]
    public async Task EditAsync_WithoutVisualOrEditorReportsUnconfigured()
    {
        var editor = new SystemCodingAgentExternalEditor(
            visualProvider: () => null,
            editorProvider: () => null,
            tempFileFactory: () => Path.Combine(Path.GetTempPath(), $"tau-editor-test-{Guid.NewGuid():N}.md"));

        var result = await editor.EditAsync("draft");

        Assert.False(result.EditorConfigured);
        Assert.False(result.Edited);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task EditAsync_UsesConfiguredEditorAndDeletesTemporaryFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"tau-editor-test-{Guid.NewGuid():N}.md");
        var command = CreateEditorCommand("edited from script");
        var editor = new SystemCodingAgentExternalEditor(
            visualProvider: () => null,
            editorProvider: () => command,
            tempFileFactory: () => tempFile);

        try
        {
            var result = await editor.EditAsync("draft");

            Assert.True(result.EditorConfigured);
            Assert.True(result.Edited);
            Assert.Equal("edited from script", result.Text);
            Assert.False(File.Exists(tempFile));
        }
        finally
        {
            File.Delete(command);
        }
    }

    [Fact]
    public async Task EditAsync_PrefersVisualOverEditor()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"tau-editor-test-{Guid.NewGuid():N}.md");
        var visual = CreateEditorCommand("edited by visual");
        var fallbackEditor = CreateEditorCommand("edited by editor");
        var editor = new SystemCodingAgentExternalEditor(
            visualProvider: () => visual,
            editorProvider: () => fallbackEditor,
            tempFileFactory: () => tempFile);

        try
        {
            var result = await editor.EditAsync("draft");

            Assert.True(result.Edited);
            Assert.Equal("edited by visual", result.Text);
        }
        finally
        {
            File.Delete(visual);
            File.Delete(fallbackEditor);
        }
    }

    private static string CreateEditorCommand(string replacement)
    {
        var script = Path.Combine(Path.GetTempPath(), $"tau-editor-script-{Guid.NewGuid():N}.cmd");
        File.WriteAllLines(script,
        [
            "@echo off",
            $"echo {replacement}> \"%~1\""
        ]);
        return script;
    }
}
