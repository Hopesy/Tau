// 作者：xxx
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentSettingsStore
{
    /// <summary>【CodingAgent】【缓存提示】读取合并后的显示设置，上游默认关闭费用及恢复提示。</summary>
    /// <returns>是否显示缓存相关提示。</returns>
    public bool GetShowCacheMissNotices() => Load().ShowCacheMissNotices ?? false;

    /// <summary>【CodingAgent】【缓存提示】保存全局显示偏好，保留其他设置及项目覆盖。</summary>
    /// <param name="show">是否显示缓存相关提示。</param>
    public void SetShowCacheMissNotices(bool show) => Save(LoadGlobal() with { ShowCacheMissNotices = show });

    /// <summary>【CodingAgent】【预热设置】只读取全局设置，禁止项目配置开启额外计费。</summary>
    /// <returns>有效模式；缺失或非法值回退为运行期间预热。</returns>
    public CodingAgentCacheWarmingMode GetCacheWarmingMode()
    {
        var settings = LoadGlobal();
        return settings.AdditionalSettings?.TryGetValue("cacheWarming", out var value) == true && value.ValueKind == JsonValueKind.String
            ? value.GetString() switch { "off" => CodingAgentCacheWarmingMode.Off, "idle" => CodingAgentCacheWarmingMode.Idle,
                _ => CodingAgentCacheWarmingMode.Streaming } : CodingAgentCacheWarmingMode.Streaming;
    }

    /// <summary>【CodingAgent】【预热设置】仅修改全局预热模式，并保留并发写入的其他设置。</summary>
    /// <param name="mode">新的有效模式。</param>
    public void SetCacheWarmingMode(CodingAgentCacheWarmingMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var settings = LoadGlobal();
        var additional = new Dictionary<string, JsonElement>(settings.AdditionalSettings ?? new Dictionary<string, JsonElement>())
        { ["cacheWarming"] = JsonSerializer.SerializeToElement(mode.ToString().ToLowerInvariant()) };
        Save(settings with { AdditionalSettings = additional });
    }
}
