// 作者：xxx
using System.Text.RegularExpressions;
using Tau.Ai.Registry;

namespace Tau.Ai.Providers.Google;

/// <summary>【Google】【思考选项】统一模型元数据、原生配置和通用等级的转换。</summary>
internal static class GoogleThinking
{
    /// <summary>【Google】【简化入口】先钳制通用等级，再映射为离散等级或对应令牌预算。</summary>
    /// <param name="model">目标模型。</param><param name="requested">可选通用等级。</param><param name="budgets">用户预算覆盖。</param>
    /// <returns>原生思考选项；未指定等级时显式关闭。</returns>
    internal static GoogleThinkingOptions ResolveSimple(Model model, ThinkingLevel? requested, ThinkingBudgets? budgets)
    {
        if (requested is null) return new() { Enabled = false };
        var key = requested == ThinkingLevel.ExtraHigh ? "xhigh" : requested.Value.ToString().ToLowerInvariant();
        var clamped = ModelCatalog.ClampThinkingLevel(model, key);
        if (clamped == "off") return new() { Enabled = false };
        var level = ResolveLevel(model, clamped);
        if (UsesLevel(model.Id)) return new() { Enabled = true, Level = level.ToUpperInvariant() };
        return new() { Enabled = true, BudgetTokens = GetBudget(model, level, budgets) };
    }

    /// <summary>【Google】【原生报文】原生等级不重复映射；关闭时按元数据选择可用最低等级。</summary>
    /// <param name="model">目标模型。</param><param name="options">原生思考选项。</param><returns>thinkingConfig 对象。</returns>
    internal static Dictionary<string, object> BuildConfig(Model model, GoogleThinkingOptions options)
    {
        if (!options.Enabled)
        {
            if (UsesLevel(model.Id))
            {
                var fallback = ModelCatalog.ClampThinkingLevel(model, "off");
                if (fallback != "off") return new() { ["thinkingLevel"] = ResolveLevel(model, fallback).ToUpperInvariant() };
            }
            return new() { ["thinkingBudget"] = 0 };
        }
        var config = new Dictionary<string, object> { ["includeThoughts"] = true };
        if (options.Level is not null)
        {
            var level = options.Level.ToUpperInvariant();
            if (level is not ("THINKING_LEVEL_UNSPECIFIED" or "MINIMAL" or "LOW" or "MEDIUM" or "HIGH"))
                throw new InvalidOperationException($"Unsupported Google thinking level: {options.Level}");
            config["thinkingLevel"] = level;
        }
        else if (options.BudgetTokens.HasValue) config["thinkingBudget"] = options.BudgetTokens.Value;
        return config;
    }

    /// <summary>【Google】【传输格式】模型名称只决定等级或预算格式，支持的等级由元数据决定。</summary>
    /// <param name="modelId">模型 ID。</param><returns>是否使用离散等级。</returns>
    private static bool UsesLevel(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        return id is "gemini-flash-latest" or "gemini-flash-lite-latest" ||
            Regex.IsMatch(id, "gemini-3(?:\\.[0-9]+)?-(?:pro|flash)|gemma-?4", RegexOptions.CultureInvariant);
    }

    /// <summary>【Google】【等级映射】允许标准等级及模型映射，非法映射立即报告来源。</summary>
    /// <param name="model">目标模型。</param><param name="level">钳制后的通用等级。</param><returns>标准小写等级。</returns>
    private static string ResolveLevel(Model model, string level)
    {
        var mapped = model.ThinkingLevelMap?.GetValueOrDefault(level);
        var resolved = mapped?.ToLowerInvariant() ?? level;
        if (resolved is "minimal" or "low" or "medium" or "high") return resolved;
        var description = model.ThinkingLevelMap?.ContainsKey(level) == true ? mapped ?? "null" : "undefined";
        throw new InvalidOperationException($"Unsupported Google thinking level mapping for {model.Provider}/{model.Id}: {level} -> {description}");
    }

    /// <summary>【Google】【令牌预算】应用映射后等级的用户预算，再采用对应传输的默认值。</summary>
    /// <param name="model">目标模型。</param><param name="level">标准等级。</param><param name="budgets">用户覆盖。</param>
    /// <returns>预算值，未知模型使用动态预算 -1。</returns>
    private static int GetBudget(Model model, string level, ThinkingBudgets? budgets)
    {
        var custom = level switch { "minimal" => budgets?.Minimal, "low" => budgets?.Low, "medium" => budgets?.Medium, _ => budgets?.High };
        if (custom.HasValue) return custom.Value;
        // 1. 【Google】【兼容预算】保留现有 CLI 传输的默认预算，主线两种传输使用各自预算表
        var cli = model.Api.Equals("google-gemini-cli", StringComparison.OrdinalIgnoreCase);
        var pro = model.Id.Contains("2.5-pro", StringComparison.Ordinal);
        var flash = model.Id.Contains("2.5-flash", StringComparison.Ordinal);
        if (!cli && !pro && !flash) return -1;
        return level switch
        {
            "minimal" => cli ? 1024 : model.Api != "google-vertex" && model.Id.Contains("2.5-flash-lite", StringComparison.Ordinal) ? 512 : 128,
            "low" => 2048,
            "medium" => 8192,
            _ => cli ? 16384 : pro ? 32768 : 24576
        };
    }
}
