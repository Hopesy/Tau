// 作者：xxx
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.Ai.Serialization;

/// <summary>【AI】【模型序列化】根据普通 type 字段恢复模型派生类型，兼容没有类型的旧聊天条目。</summary>
internal sealed class ModelJsonConverter : JsonConverter<Model>
{
    /// <summary>读取模型并恢复实际能力类型。</summary>
    /// <param name="reader">当前 JSON 读取器。</param>
    /// <param name="typeToConvert">目标基类。</param>
    /// <param name="options">序列化配置。</param>
    /// <returns>完整的聊天、图像或分类模型。</returns>
    public override Model Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var stored = JsonSerializer.Deserialize(ref reader, TauAiJsonContext.Default.StoredModel)
            ?? throw new JsonException("Model must be a JSON object.");
        return stored.ToModel();
    }

    /// <summary>通过没有多态转换器的快照类型写入所有公共字段。</summary>
    /// <param name="writer">JSON 写入器。</param>
    /// <param name="value">需要持久化的模型。</param>
    /// <param name="options">序列化配置。</param>
    public override void Write(Utf8JsonWriter writer, Model value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new StoredModel(value), TauAiJsonContext.Default.StoredModel);
}

/// <summary>【AI】【模型序列化】避免递归调用基类转换器的完整存储快照。</summary>
internal sealed record StoredModel : Model
{
    public IReadOnlyList<string>? OutputModalities { get; init; }

    /// <summary>供源生成 JSON 读取器构造快照。</summary>
    public StoredModel() { }

    /// <summary>复制模型公共属性，并保存图像专有输出能力。</summary>
    /// <param name="model">原始模型。</param>
    [SetsRequiredMembers]
    public StoredModel(Model model) : base(model) => OutputModalities = (model as ImagesModel)?.OutputModalities;

    /// <summary>恢复能力类型，未知类型保留标记以便运行时目录过滤。</summary>
    /// <returns>保留字段的派生模型。</returns>
    public Model ToModel()
    {
        Model model = Type switch
        {
            ModelTypes.Image => new ImagesModel { Id = Id, Name = Name, Api = Api, Provider = Provider, OutputModalities = OutputModalities ?? ["image"] },
            ModelTypes.Classifier => new ClassifierModel { Id = Id, Name = Name, Api = Api, Provider = Provider },
            _ => new Model { Id = Id, Name = Name, Api = Api, Provider = Provider }
        };
        return model with
        {
            Type = Type,
            BaseUrl = BaseUrl,
            Reasoning = Reasoning,
            ThinkingLevelMap = ThinkingLevelMap,
            InputModalities = InputModalities,
            InputLimits = InputLimits,
            PromptCache = PromptCache,
            Cost = Cost,
            ContextWindow = ContextWindow,
            MaxOutputTokens = MaxOutputTokens,
            Headers = Headers,
            SamplingParams = SamplingParams,
            Compat = Compat
        };
    }
}
