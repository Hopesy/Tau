using Tau.Ai.Utilities;

namespace Tau.Ai.Tests;

public sealed class MessageTransformerTests
{
    [Fact]
    public void DowngradeUnsupportedImages_ReplacesUserAndToolImagesWithPlaceholders()
    {
        var model = new Model
        {
            Id = "text-model",
            Name = "Text model",
            Api = "openai-chat-completions",
            Provider = "openai",
            InputModalities = ["text"]
        };
        var messages = new ChatMessage[]
        {
            new UserMessage(
            [
                new TextContent("before"),
                new ImageContent("aW1hZ2U=", "image/png"),
                new ImageContent("aW1hZ2Uy", "image/jpeg"),
                new TextContent("after")
            ]),
            new ToolResultMessage(
                "call_1",
                [
                    new ImageContent("dG9vbA==", "image/png"),
                    new TextContent("tool text"),
                    new ImageContent("dG9vbDI=", "image/png")
                ])
            {
                ToolName = "screenshot"
            }
        };

        var transformed = MessageTransformer.DowngradeUnsupportedImages(messages, model);

        Assert.NotSame(messages, transformed);
        var user = Assert.IsType<UserMessage>(transformed[0]);
        Assert.Collection(
            user.Content,
            block => Assert.Equal("before", Assert.IsType<TextContent>(block).Text),
            block => Assert.Equal(MessageTransformer.NonVisionUserImagePlaceholder, Assert.IsType<TextContent>(block).Text),
            block => Assert.Equal("after", Assert.IsType<TextContent>(block).Text));

        var tool = Assert.IsType<ToolResultMessage>(transformed[1]);
        Assert.Equal("call_1", tool.ToolCallId);
        Assert.Equal("screenshot", tool.ToolName);
        Assert.Collection(
            tool.Content,
            block => Assert.Equal(MessageTransformer.NonVisionToolImagePlaceholder, Assert.IsType<TextContent>(block).Text),
            block => Assert.Equal("tool text", Assert.IsType<TextContent>(block).Text),
            block => Assert.Equal(MessageTransformer.NonVisionToolImagePlaceholder, Assert.IsType<TextContent>(block).Text));
    }

    [Fact]
    public void DowngradeUnsupportedImages_PreservesOriginalMessagesForVisionModels()
    {
        var model = new Model
        {
            Id = "vision-model",
            Name = "Vision model",
            Api = "openai-chat-completions",
            Provider = "openai",
            InputModalities = ["text", "image"]
        };
        var messages = new ChatMessage[]
        {
            new UserMessage([new ImageContent("aW1hZ2U=", "image/png")])
        };

        var transformed = MessageTransformer.DowngradeUnsupportedImages(messages, model);

        Assert.Same(messages, transformed);
    }
}
