// 作者：xxx
using Tau.Ai;
using Tau.Ai.Auth;
using Tau.Ai.Providers;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

/// <summary>【CodingAgent】【认证模型解析测试】验证精确、模糊、带斜杠与提供方认证优先级。</summary>
public sealed class CodingAgentAuthModelResolverTests
{
    /// <summary>【CodingAgent】【重名 ID】唯一已认证提供方优先，没有或多个认证时保持歧义。</summary><param name="configured">已认证提供方个数。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BareId_RequiresUniqueAuthenticatedProvider(int configured)
    {
        var models = new Models([Provider("first", configured >= 1, "chat"), Provider("second", configured >= 2, "chat")]);
        if (configured == 1) Assert.Equal("first", (await Resolve(models, "chat")).Model.Provider);
        else Assert.Contains("ambiguous across providers", (await Assert.ThrowsAsync<CodingAgentAuthCommandException>(() => Resolve(models, "chat"))).Message);
    }

    /// <summary>【CodingAgent】【斜杠归属】优先明确提供方，但其未认证时唯一已认证字面 ID 可以优先。</summary><param name="providerReady">推断提供方已认证。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlashId_PrefersUsableProviderOrLiteralMatch(bool providerReady)
    {
        var models = new Models([Provider("first", providerReady, "chat"), Provider("gateway", true, "first/chat")]);
        var result = await Resolve(models, "first/chat");
        Assert.Equal(providerReady ? "first" : "gateway", result.Model.Provider); Assert.False(result.Custom);
    }

    /// <summary>【CodingAgent】【斜杠回退】已知前缀没有对应模型时，跨目录字面 ID 仍可解析。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task SlashId_FallsBackToLiteralModel()
    {
        var models = new Models([Provider("first", true, "unrelated"), Provider("gateway", true, "first/chat:extended")]);
        Assert.Equal("gateway", (await Resolve(models, "first/chat:extended")).Model.Provider);
    }

    /// <summary>【CodingAgent】【别名与日期】部分匹配优先非日期别名，没有别名时选择最新日期后缀。</summary><param name="alias">是否有别名。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialPattern_PrefersAliasThenLatestDate(bool alias)
    {
        var ids = alias ? new[] { "chat-20251001", "chat-20261001", "chat-latest" } : ["chat-20251001", "chat-20261001"];
        var models = new Models([Provider("test", true, ids)]);
        Assert.Equal(alias ? "chat-latest" : "chat-20261001", (await Resolve(models, "chat")).Model.Id);
    }

    /// <summary>【CodingAgent】【推理后缀】已有模型含冒号时完整 ID 优先；真正的推理后缀只在匹配失败后解析。</summary><param name="reference">引用。</param><param name="expected">模型 ID。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("chat:extended", "chat:extended")]
    [InlineData("chat:extended:high", "chat:extended")]
    [InlineData("chat:high", "chat")]
    public async Task ThinkingSuffix_PreservesLiteralColonIds(string reference, string expected)
    {
        var models = new Models([Provider("test", true, "chat", "chat:extended")]);
        Assert.Equal(expected, (await Resolve(models, reference)).Model.Id);
    }

    /// <summary>【CodingAgent】【明确提供方】忽略大小写并允许重复 provider 前缀；未登记 ID 可继承基础模型。</summary><param name="custom">是否使用自定义 ID。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitProvider_AllowsCanonicalAndCustomIds(bool custom)
    {
        var models = new Models([Provider("test", true, "chat")]);
        var result = await CodingAgentAuthModelResolver.ResolveAsync(models, "TEST", custom ? "test/new:high" : "TEST/CHAT", null, default);
        Assert.Equal(custom, result.Custom); Assert.Equal(custom ? "new" : "chat", result.Model.Id);
        Assert.Equal(custom, result.Model.Reasoning); Assert.Equal("test", result.Model.Provider);
    }

    /// <summary>【CodingAgent】【无匹配】未知提供方或没有明确提供方的未知模型不可生成自定义模型。</summary><param name="provider">提供方。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public async Task UnknownReference_IsRejected(string? provider)
    {
        var models = new Models([Provider("test", true, "chat")]);
        await Assert.ThrowsAsync<CodingAgentAuthCommandException>(() => CodingAgentAuthModelResolver.ResolveAsync(models, provider, "unknown", null, default));
    }

    /// <summary>【CodingAgent】【测试解析】在无显式提供方时执行模式匹配。</summary><param name="models">集合。</param><param name="reference">引用。</param><returns>选择结果。</returns>
    private static Task<(Model Model, bool Custom)> Resolve(Models models, string reference) => CodingAgentAuthModelResolver.ResolveAsync(models, null, reference, null, default);

    /// <summary>【CodingAgent】【测试目录】创建固定认证状态与模型列表。</summary><param name="id">提供方。</param><param name="configured">已配置认证。</param><param name="ids">模型 ID。</param><returns>提供方定义。</returns>
    private static ProviderDefinition Provider(string id, bool configured, params string[] ids) => new(id,
        models: ids.Select(model => new Model { Id = model, Name = model, Provider = id, Api = "fixture" }),
        auth: new(apiKey: new("Key", _ => Task.FromResult<ProviderAuthResult?>(configured ? new("fixture-key") : null))));
}
