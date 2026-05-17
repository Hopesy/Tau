using System.Text.Json.Serialization;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(WebChatStoreDocument))]
[JsonSerializable(typeof(WebChatSessionDto[]))]
[JsonSerializable(typeof(WebChatSessionDto))]
[JsonSerializable(typeof(WebChatMessageDto))]
[JsonSerializable(typeof(WebChatAttachmentDto))]
[JsonSerializable(typeof(WebChatAttachmentDto[]))]
[JsonSerializable(typeof(WebChatToolCallDto))]
[JsonSerializable(typeof(WebChatToolCallDto[]))]
[JsonSerializable(typeof(SendMessageRequest))]
[JsonSerializable(typeof(WebChatStreamEventDto))]
[JsonSerializable(typeof(WebUiAuthStatusDto))]
internal sealed partial class WebUiJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebChatStreamEventDto))]
[JsonSerializable(typeof(WebChatToolCallDto))]
internal sealed partial class WebUiNdjsonContext : JsonSerializerContext
{
}

internal sealed record WebChatStoreDocument(IReadOnlyList<WebChatSessionDto> Sessions);
