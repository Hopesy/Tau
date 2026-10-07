// 作者：xxx
using System.Text.Json;
using Tau.Ai.Serialization;

namespace Tau.Ai.Registry;

/// <summary>【AI】【分类目录】从嵌入快照加载分类模型，运行时无需联网或反射序列化。</summary>
internal static class GeneratedBuiltInClassifierModels
{
    internal static IReadOnlyList<ClassifierModel> Models { get; } = Load();

    /// <summary>读取经过生成器验证的内置快照，损坏时直接报告错误。</summary>
    /// <returns>可供统一模型集合注册的只读分类目录。</returns>
    private static IReadOnlyList<ClassifierModel> Load()
    {
        using var stream = typeof(GeneratedBuiltInClassifierModels).Assembly.GetManifestResourceStream(
            "Tau.Ai.Registry.generated-classifier-models.seed.json")
            ?? throw new InvalidOperationException("Missing embedded classifier catalog.");
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidOperationException("Unsupported classifier catalog version.");
        var models = document.RootElement.GetProperty("models").Deserialize(TauAiJsonContext.Default.ClassifierModelArray)
            ?? throw new InvalidOperationException("Invalid classifier catalog.");
        return Array.AsReadOnly(models);
    }
}
