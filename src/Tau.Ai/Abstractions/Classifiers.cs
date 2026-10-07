// 作者：xxx
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tau.Ai;

/// <summary>【AI】【模型类型】统一模型目录支持的能力标记。</summary>
public static class ModelTypes
{
    public const string Chat = "chat";
    public const string Image = "image";
    public const string Classifier = "classifier";

    /// <summary>读取模型类型，并兼容未声明类型的旧聊天模型。</summary>
    /// <param name="model">目标模型。</param>
    /// <returns>显式类型或默认聊天类型。</returns>
    public static string GetModelType(Model model) => model.Type ?? Chat;

    /// <summary>判断模型是否属于支持的能力类型。</summary>
    /// <param name="model">目标模型。</param>
    /// <returns>支持的类型返回 true。</returns>
    public static bool IsKnown(Model model) => GetModelType(model) is Chat or Image or Classifier;
}

/// <summary>【AI】【分类模型】对结构化状态执行选择、评分和布尔判断的模型。</summary>
public record ClassifierModel : Model
{
    /// <summary>创建具有分类类型标记的模型。</summary>
    public ClassifierModel() => Type = ModelTypes.Classifier;
}

/// <summary>分类问题的公共指令。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClassifierChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ClassifierScoreQuestion), "score")]
[JsonDerivedType(typeof(ClassifierBoolQuestion), "bool")]
public abstract record ClassifierQuestion(string Instructions);

/// <summary>从名称到说明的候选集合中选择一个答案。</summary>
public sealed record ClassifierChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string> Criteria)
    : ClassifierQuestion(Instructions);

/// <summary>根据有序标准生成分数。</summary>
public sealed record ClassifierScoreQuestion(string Instructions, IReadOnlyList<string> Criteria)
    : ClassifierQuestion(Instructions);

/// <summary>真假判断的两个标准。</summary>
public sealed record ClassifierBoolCriteria(
    [property: JsonPropertyName("true")] string True,
    [property: JsonPropertyName("false")] string False);

/// <summary>根据真假标准生成概率。</summary>
public sealed record ClassifierBoolQuestion(string Instructions, ClassifierBoolCriteria Criteria)
    : ClassifierQuestion(Instructions);

/// <summary>分类请求的 JSON 对象状态及按标识组织的问题。</summary>
public sealed record ClassifierContext(JsonElement State, IReadOnlyDictionary<string, ClassifierQuestion> Questions);

/// <summary>分类答案的判别联合。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClassifierChoiceAnswer), "choice")]
[JsonDerivedType(typeof(ClassifierScoreAnswer), "score")]
[JsonDerivedType(typeof(ClassifierBoolAnswer), "bool")]
public abstract record ClassifierAnswer;

/// <summary>选择结果、各候选概率及置信度。</summary>
public sealed record ClassifierChoiceAnswer(string Choice, IReadOnlyDictionary<string, double> Probabilities, double Confidence) : ClassifierAnswer;

/// <summary>评分及置信度。</summary>
public sealed record ClassifierScoreAnswer(double Score, double Confidence) : ClassifierAnswer;

/// <summary>判断为真的概率。</summary>
public sealed record ClassifierBoolAnswer(double Probability) : ClassifierAnswer;

/// <summary>分类请求的认证、回调、超时和重试设置。</summary>
public record ClassifierOptions
{
    /// <summary>【AI】【分类选项】保留原生扩展提供方的额外请求字段。</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalOptions { get; init; }
    public string? ApiKey { get; init; }
    [JsonIgnore]
    public CancellationToken Signal { get; init; }
    [JsonIgnore]
    public Func<ProviderResponse, ClassifierModel, ValueTask>? OnResponse { get; init; }
    [JsonIgnore]
    public Func<JsonElement, ClassifierModel, ValueTask<JsonElement?>>? OnPayload { get; init; }
    public IDictionary<string, string>? Headers { get; init; }
    public TimeSpan? Timeout { get; init; }
    public TimeSpan? MaxRetryDelay { get; init; }
    public int? MaxRetries { get; init; }
    public double? Temperature { get; init; }
    public IDictionary<string, object>? Metadata { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
}

/// <summary>分类调用结果；错误与取消同样以结果返回。</summary>
public sealed record ClassifierResult
{
    public required string Api { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public IReadOnlyDictionary<string, ClassifierAnswer> Answers { get; init; } = new Dictionary<string, ClassifierAnswer>();
    public Usage? Usage { get; init; }
    public ClassifierStopReason StopReason { get; init; } = ClassifierStopReason.Stop;
    public string? ErrorMessage { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public enum ClassifierStopReason
{
    Stop,
    Error,
    Aborted
}
