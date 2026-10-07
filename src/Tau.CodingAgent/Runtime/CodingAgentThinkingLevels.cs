using Tau.Ai;
using Tau.Ai.Registry;

namespace Tau.CodingAgent.Runtime;

internal static class CodingAgentThinkingLevels
{
    /// <summary>【CodingAgent】【思考恢复】按显式参数、会话、设置及 medium 默认值选择等级，并限制模型能力。</summary>
    /// <param name="model">当前模型。</param>
    /// <param name="requested">显式或模型范围指定的等级。</param>
    /// <param name="persisted">会话保存的等级；null 表示没有记录。</param>
    /// <param name="defaultLevel">全局默认等级。</param>
    /// <returns>适用于当前模型的等级；非推理模型为空。</returns>
    public static ThinkingLevel? ResolveStartup(Model model, string? requested, string? persisted, string? defaultLevel)
    {
        foreach (var candidate in new[] { requested, persisted, defaultLevel, "medium" })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !TryParse(candidate, out var level)) continue;
            // 1. 【CodingAgent】【思考恢复】off 必须显式传递，避免被供应商配置的推理默认值覆盖
            return ClampForModel(model, level ?? ThinkingLevel.Off);
        }
        return null;
    }

    public static readonly IReadOnlyList<string> DefaultLevels =
        ["off", "minimal", "low", "medium", "high", "xhigh"];

    private static readonly IReadOnlyList<string> LevelsWithoutXhigh =
        ["off", "minimal", "low", "medium", "high"];

    private static readonly IReadOnlyList<string> OffOnlyLevels = ["off"];

    /// <summary>【CodingAgent】【推理目录】优先使用模型显式等级表，旧模型保留已支持的兼容规则。</summary>
    /// <param name="model">模型。</param><returns>从低到高的可选等级。</returns>
    public static IReadOnlyList<string> AvailableForModel(Model model)
    {
        if (model.ThinkingLevelMap is not null) return ModelCatalog.GetSupportedThinkingLevels(model);
        if (!model.Reasoning)
        {
            return OffOnlyLevels;
        }

        return ModelCatalog.SupportsXhigh(model)
            ? DefaultLevels
            : LevelsWithoutXhigh;
    }

    /// <summary>【CodingAgent】【推理参数】解析原生等级及兼容别名，off 使用空值表示。</summary>
    /// <param name="value">文本等级。</param><param name="level">解析结果。</param><returns>是否为有效等级。</returns>
    public static bool TryParse(string? value, out ThinkingLevel? level)
    {
        level = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "off":
            case "none":
                return true;
            case "minimal":
                level = ThinkingLevel.Minimal;
                return true;
            case "low":
                level = ThinkingLevel.Low;
                return true;
            case "medium":
            case "med":
                level = ThinkingLevel.Medium;
                return true;
            case "high":
                level = ThinkingLevel.High;
                return true;
            case "xhigh":
            case "extrahigh":
            case "extra-high":
                level = ThinkingLevel.ExtraHigh;
                return true;
            case "max":
                level = ThinkingLevel.Max;
                return true;
            default:
                return false;
        }
    }

    public static ThinkingLevel? ParseOrNull(string? value) =>
        TryParse(value, out var level) ? level : null;

    /// <summary>【CodingAgent】【推理约束】将所选等级钳制到模型允许的等级。</summary>
    /// <param name="model">目标模型。</param><param name="requested">所选等级。</param><returns>实际等级，空值表示 off。</returns>
    public static ThinkingLevel? ClampForModel(Model model, ThinkingLevel? requested)
    {
        if (requested is null)
        {
            return null;
        }

        if (!model.Reasoning)
        {
            return null;
        }

        if (model.ThinkingLevelMap is not null || requested == ThinkingLevel.Max)
            return ParseOrNull(ModelCatalog.ClampThinkingLevel(model, Format(requested)));

        return requested == ThinkingLevel.ExtraHigh && !ModelCatalog.SupportsXhigh(model)
            ? ThinkingLevel.High
            : requested;
    }

    /// <summary>【CodingAgent】【推理切换】循环选择模型支持的等级，显式禁用的等级不会出现。</summary>
    /// <param name="model">目标模型。</param><param name="current">当前等级。</param><returns>下一等级。</returns>
    public static ThinkingLevel? CycleForModel(Model model, ThinkingLevel? current)
    {
        if (model.ThinkingLevelMap is not null)
        {
            var levels = AvailableForModel(model);
            var index = levels.ToList().IndexOf(Format(current));
            return ParseOrNull(levels[(index + 1) % levels.Count]);
        }
        if (!model.Reasoning)
        {
            return null;
        }

        var clamped = ClampForModel(model, current);
        return clamped switch
        {
            null => ThinkingLevel.Low,
            ThinkingLevel.Minimal => ThinkingLevel.Low,
            ThinkingLevel.Low => ThinkingLevel.Medium,
            ThinkingLevel.Medium => ThinkingLevel.High,
            ThinkingLevel.High => ModelCatalog.SupportsXhigh(model) ? ThinkingLevel.ExtraHigh : null,
            ThinkingLevel.ExtraHigh => null,
            _ => ThinkingLevel.Low
        };
    }

    /// <summary>【CodingAgent】【推理协议】输出原生等级名称。</summary>
    /// <param name="level">内部等级。</param><returns>原生文本。</returns>
    public static string Format(ThinkingLevel? level) => level switch
    {
        null => "off",
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.ExtraHigh => "xhigh",
        ThinkingLevel.Max => "max",
        _ => "off"
    };

    public static string? FormatRaw(ThinkingLevel? level) =>
        level is null ? null : Format(level);
}
