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
[JsonSerializable(typeof(CodingAgentJsonlSessionPreviewDto))]
[JsonSerializable(typeof(CodingAgentJsonlPreviewFilterDto))]
[JsonSerializable(typeof(CodingAgentJsonlTreeMetadataDto))]
[JsonSerializable(typeof(CodingAgentJsonlEntryMetadataDto))]
[JsonSerializable(typeof(CodingAgentJsonlImportAuditDto))]
[JsonSerializable(typeof(CodingAgentJsonlBranchTimelineEntryDto))]
[JsonSerializable(typeof(CodingAgentJsonlBranchLabelDto))]
[JsonSerializable(typeof(CodingAgentJsonlAuditWarningDto))]
[JsonSerializable(typeof(CodingAgentJsonlImportResultDto))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, int>))]
[JsonSerializable(typeof(CodingAgentJsonlTimelineMessageDto))]
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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebChatJsonlSessionHeader))]
[JsonSerializable(typeof(WebChatJsonlMessageEntry))]
internal sealed partial class WebUiJsonlContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodingAgentJsonlSessionHeader))]
[JsonSerializable(typeof(CodingAgentJsonlSessionEntry))]
[JsonSerializable(typeof(CodingAgentJsonlSessionMessage))]
[JsonSerializable(typeof(CodingAgentJsonlSessionContent))]
internal sealed partial class WebUiCodingAgentJsonlContext : JsonSerializerContext
{
}

internal sealed record WebChatStoreDocument(IReadOnlyList<WebChatSessionDto> Sessions);
