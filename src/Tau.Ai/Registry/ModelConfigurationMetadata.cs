// 作者：xxx
using System.Text.Json;

namespace Tau.Ai.Registry;

public sealed partial class ModelConfigurationStore
{
    /// <summary>【AI】【目录交换】读取原生模型数组，按类型分别解析同名聊天、图像及分类模型。</summary>
    /// <param name="provider">提供方标识。</param><param name="models">具有完整协议和地址的模型数组。</param>
    /// <returns>全部受支持类型的模型。</returns>
    public static IReadOnlyList<Model> ReadProviderModels(string provider, JsonElement models)
    {
        if (models.ValueKind != JsonValueKind.Array) throw new JsonException("Provider models must be an array.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WritePropertyName("models"); models.WriteTo(writer); writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        var result = new List<Model>();
        foreach (var type in new[] { ModelTypes.Chat, ModelTypes.Image, ModelTypes.Classifier })
        {
            var catalog = new Dictionary<string, Dictionary<string, Model>>(StringComparer.OrdinalIgnoreCase);
            ApplyProvider(provider, document.RootElement, catalog, type);
            result.AddRange(catalog[provider].Values);
        }
        return result;
    }
    /// <summary>【AI】【模型元数据】读取类型化输入限制。</summary>
    /// <param name="model">模型 JSON。</param><returns>输入限制，缺失时为空。</returns>
    internal static ModelInputLimits? ParseInputLimits(JsonElement model)
    {
        if (ReadMetadataObject(model, "inputLimits", "inputLimits") is not { } value) return null;
        ModelImageInputLimits? images = null;
        // 1. 【AI】【图片限制】逐层检查对象及整数范围，允许等值的小数和指数写法
        if (ReadMetadataObject(value, "images", "inputLimits.images") is { } imageValue)
        {
            ModelImageResizeOptions? resize = null;
            if (ReadMetadataObject(imageValue, "resize", "inputLimits.images.resize") is { } resizeValue)
                resize = new()
                {
                    MaxWidth = (int?)ReadMetadataInteger(resizeValue, "maxWidth", "inputLimits.images.resize", int.MaxValue),
                    MaxHeight = (int?)ReadMetadataInteger(resizeValue, "maxHeight", "inputLimits.images.resize", int.MaxValue),
                    MaxBytes = ReadMetadataInteger(resizeValue, "maxBytes", "inputLimits.images.resize"),
                    JpegQuality = (int?)ReadMetadataInteger(resizeValue, "jpegQuality", "inputLimits.images.resize", 100)
                };
            images = new()
            {
                Resize = resize,
                MaxPerMessage = (int?)ReadMetadataInteger(imageValue, "maxPerMessage", "inputLimits.images", int.MaxValue),
                MaxPerRequest = (int?)ReadMetadataInteger(imageValue, "maxPerRequest", "inputLimits.images", int.MaxValue)
            };
        }
        return new() { MaxRequestBytes = ReadMetadataInteger(value, "maxRequestBytes", "inputLimits"), Images = images };
    }

    /// <summary>【AI】【缓存元数据】读取缓存寿命。</summary>
    /// <param name="model">模型 JSON。</param><returns>缓存寿命，缺失时为空。</returns>
    internal static ModelPromptCache? ParsePromptCache(JsonElement model) =>
        ReadMetadataObject(model, "promptCache", "promptCache") is { } value
            ? new() { Short = ReadCacheLifetime(value, "short"), Long = ReadCacheLifetime(value, "long") } : null;

    /// <summary>【AI】【元数据校验】校验整个提供方，包括尚未匹配到模型的覆盖，避免部分配置发布。</summary>
    /// <param name="provider">提供方 JSON 对象。</param>
    internal static void ValidateProviderMetadata(JsonElement provider)
    {
        if (provider.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            foreach (var model in models.EnumerateArray())
                if (model.ValueKind == JsonValueKind.Object) { ParseInputLimits(model); ParsePromptCache(model); }
        if (provider.TryGetProperty("modelOverrides", out var overrides) && overrides.ValueKind == JsonValueKind.Object)
            foreach (var entry in overrides.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Object) { ParseInputLimits(entry.Value); ParsePromptCache(entry.Value); }
    }

    /// <summary>【AI】【元数据校验】区分可省略对象与显式无效值。</summary>
    /// <param name="parent">父对象。</param><param name="name">属性名。</param><param name="path">诊断路径。</param>
    /// <returns>对象；属性缺失时为空。</returns>
    private static JsonElement? ReadMetadataObject(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException($"{path} must be an object.");
        return value;
    }

    /// <summary>【AI】【元数据校验】读取可表示的正整数，不将字符串、布尔或空值转换为数字。</summary>
    /// <param name="parent">父对象。</param><param name="name">属性名。</param><param name="path">父诊断路径。</param>
    /// <param name="maximum">允许的最大值。</param><returns>整数；属性缺失时为空。</returns>
    private static long? ReadMetadataInteger(JsonElement parent, string name, string path, long maximum = long.MaxValue)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) ||
            number < 1 || number > maximum || decimal.Truncate(number) != number)
            throw new JsonException($"{path}.{name} must be an integer between 1 and {maximum}.");
        return (long)number;
    }

    /// <summary>【AI】【缓存校验】读取有限且大于零的缓存秒数。</summary>
    /// <param name="parent">缓存对象。</param><param name="name">短期或长期字段名。</param><returns>寿命；属性缺失时为空。</returns>
    private static double? ReadCacheLifetime(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number <= 0)
            throw new JsonException($"promptCache.{name} must be a finite number greater than zero.");
        return number;
    }

    /// <summary>【AI】【模型覆盖】逐层合并输入限制，未覆盖字段保留原值。</summary>
    /// <param name="baseline">原始限制。</param><param name="overlay">字段覆盖。</param><returns>合并限制。</returns>
    private static ModelInputLimits? MergeInputLimits(ModelInputLimits? baseline, ModelInputLimits? overlay)
    {
        if (overlay is null) return baseline;
        if (baseline is null) return overlay;
        var images = overlay.Images;
        if (images is not null && baseline.Images is { } previous)
        {
            var resize = images.Resize;
            if (resize is not null && previous.Resize is { } previousResize)
                resize = new() { MaxWidth = resize.MaxWidth ?? previousResize.MaxWidth, MaxHeight = resize.MaxHeight ?? previousResize.MaxHeight,
                    MaxBytes = resize.MaxBytes ?? previousResize.MaxBytes, JpegQuality = resize.JpegQuality ?? previousResize.JpegQuality };
            images = new() { Resize = resize ?? previous.Resize, MaxPerMessage = images.MaxPerMessage ?? previous.MaxPerMessage, MaxPerRequest = images.MaxPerRequest ?? previous.MaxPerRequest };
        }
        return new() { MaxRequestBytes = overlay.MaxRequestBytes ?? baseline.MaxRequestBytes, Images = images ?? baseline.Images };
    }

    /// <summary>【AI】【缓存覆盖】分别合并短期和长期寿命。</summary>
    /// <param name="baseline">原始寿命。</param><param name="overlay">字段覆盖。</param><returns>合并寿命。</returns>
    private static ModelPromptCache? MergePromptCache(ModelPromptCache? baseline, ModelPromptCache? overlay) => overlay is null ? baseline :
        new() { Short = overlay.Short ?? baseline?.Short, Long = overlay.Long ?? baseline?.Long };
}
