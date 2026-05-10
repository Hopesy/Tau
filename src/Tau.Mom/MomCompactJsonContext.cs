using System.Text.Json.Serialization;

namespace Tau.Mom;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ChannelLogEntry))]
[JsonSerializable(typeof(ChannelLogAttachment))]
[JsonSerializable(typeof(ChannelAttachmentEntry))]
internal sealed partial class MomCompactJsonContext : JsonSerializerContext
{
}
