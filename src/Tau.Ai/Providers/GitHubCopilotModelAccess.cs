// 作者：xxx
using System.Text.Json;

namespace Tau.Ai.Providers;

/// <summary>【AI】【Copilot 模型权限】SDK 与宿主共用的账户聊天目录过滤。</summary>
public static class GitHubCopilotModelAccess
{
    /// <summary>【AI】【可用模型】完整字符串数组表示账户允许列表；缺省或无效值保持旧凭据兼容，空数组禁止全部聊天模型。</summary>
    /// <param name="models">提供方目录，可包含其他能力。</param><param name="properties">OAuth 原生字段，非 OAuth 传空。</param><returns>过滤后的目录。</returns>
    public static IReadOnlyList<Model> Filter(IReadOnlyList<Model> models, IReadOnlyDictionary<string, JsonElement>? properties)
    {
        var ids = properties is not null && properties.TryGetValue("availableModelIds", out var value) ? value : default;
        return FilterIds(models, ReadIds(ids));
    }

    /// <summary>【AI】【Copilot 模型权限】从原生凭据读取公开的模型清单，拒绝非 OAuth 和损坏字段。</summary>
    /// <param name="credential">原生存储凭据。</param><returns>有效模型 ID；缺省或无效时为空引用。</returns>
    public static IReadOnlyList<string>? ReadAvailableModelIds(JsonElement? credential) =>
        credential is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String && type.GetString() == "oauth" && value.TryGetProperty("availableModelIds", out var ids)
            ? ReadIds(ids) : null;

    /// <summary>【AI】【Copilot 模型权限】过滤原生凭据允许的聊天模型，供宿主与扩展目录共用。</summary>
    /// <param name="models">提供方模型。</param><param name="credential">原生存储凭据。</param><returns>允许的模型目录。</returns>
    public static IReadOnlyList<Model> FilterCredential(IReadOnlyList<Model> models, JsonElement? credential) =>
        FilterIds(models, ReadAvailableModelIds(credential));

    /// <summary>【AI】【权限清单】验证字符串数组并复制，避免凭据对象进入普通运行时快照。</summary>
    /// <param name="ids">原生清单字段。</param><returns>有效 ID 或空引用。</returns>
    private static IReadOnlyList<string>? ReadIds(JsonElement ids) => ids.ValueKind == JsonValueKind.Array &&
        ids.EnumerateArray().All(id => id.ValueKind == JsonValueKind.String) ? ids.EnumerateArray().Select(id => id.GetString()!).ToArray() : null;

    /// <summary>【AI】【目录过滤】只对聊天模型应用精确匹配的允许清单。</summary>
    /// <param name="models">模型目录。</param><param name="ids">允许清单。</param><returns>过滤后的模型。</returns>
    private static IReadOnlyList<Model> FilterIds(IReadOnlyList<Model> models, IReadOnlyList<string>? ids)
    {
        if (ids is null) return models;
        var allowed = ids.ToHashSet(StringComparer.Ordinal);
        return models.Where(model => ModelTypes.GetModelType(model) != ModelTypes.Chat || allowed.Contains(model.Id)).ToArray();
    }
}
