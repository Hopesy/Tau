// 作者：xxx
using Tau.Ai.Providers;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed class BuiltInProviderNamesTests
{
    /// <summary>【AI】【提供方名称】统一 Models 接口暴露主线名称，不再回传内部标识或旧界面标签。</summary>
    /// <param name="providerId">提供方标识。</param><param name="expected">主线显示名。</param>
    [Theory]
    [InlineData("openai", "OpenAI")]
    [InlineData("anthropic", "Anthropic")]
    [InlineData("google", "Google")]
    [InlineData("nvidia", "NVIDIA")]
    [InlineData("zai", "Z.AI")]
    [InlineData("openai-codex", "OpenAI Codex (legacy)")]
    [InlineData("baseten", "Baseten")]
    [InlineData("qwen-token-plan", "Qwen Token Plan")]
    public void Models_ExposeNativeProviderNames(string providerId, string expected)
    {
        var models = BuiltInProviders.CreateBuiltInModels(new ModelConfigurationStore([]));
        Assert.Equal(expected, models.GetProvider(providerId)!.Name);
    }
}
