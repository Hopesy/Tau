// 作者：xxx
using Tau.Ai.Auth;
using Tau.Ai.Auth.OAuth;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【登录元数据】Node 的说明链接、选择项说明及手动提示完整送达类型化宿主。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task NativeAuth_PreservesInfoSelectAndManualPromptMetadata()
    {
        using var fixture = new Fixture("auth-metadata", "models");
        WriteMetadataExtension(fixture, """
            interaction.notify({type:'info',message:'Setup required',links:[{url:'https://guide.invalid',label:'Guide'},{url:'https://account.invalid'}]});
            const selection=await interaction.prompt({type:'select',message:'Choose login',options:[{id:'browser',label:'Browser',description:'Open this computer browser'}]});
            if(selection!=='browser')throw Error('Wrong selection');
            const code=await interaction.prompt({type:'manual_code',message:'Paste callback',placeholder:'https://callback.invalid'});
            if(code!=='callback')throw Error('Wrong callback');
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.Empty(session.StartupExtensionErrors);
        var interaction = new MetadataInteraction();
        await session.Runner.GetOAuthProvider("auth-metadata")!.LoginAsync(new CodingAgentInteractionOAuthCallbacks(interaction));
        Assert.Equal("info", interaction.Notification!.Type);
        Assert.Equal(new[] { new ProviderAuthInfoLink("https://guide.invalid", "Guide"), new ProviderAuthInfoLink("https://account.invalid") }, interaction.Notification.Links);
        Assert.Equal("Open this computer browser", interaction.Prompts[0].Options![0].Description);
        Assert.Equal("manual_code", interaction.Prompts[1].Type);
        Assert.Equal("Paste callback", interaction.Prompts[1].Message); Assert.Equal("https://callback.invalid", interaction.Prompts[1].Placeholder);
    }

    /// <summary>【CodingAgent】【提示取消】单个 Node 提示取消会关闭宿主输入，不影响登录继续完成。</summary>
    /// <param name="promptType">手动代码或选择提示。</param><returns>测试任务。</returns>
    [Theory]
    [InlineData("manual_code")]
    [InlineData("select")]
    public async Task NativeAuthPrompt_PropagatesIndependentCancellation(string promptType)
    {
        using var fixture = new Fixture("auth-manual-cancel", "models");
        WriteMetadataExtension(fixture, """
            const controller=new AbortController();
            const pending=interaction.prompt({type:'__TYPE__',message:'Choose or paste',options:[{id:'browser',label:'Browser'}],signal:controller.signal});
            setTimeout(()=>controller.abort(),100);
            let cancelled=false;try{await pending;}catch{cancelled=true;}
            if(!cancelled||interaction.signal.aborted)throw Error('Wrong cancellation scope');
            """.Replace("__TYPE__", promptType, StringComparison.Ordinal));
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
            { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.Empty(session.StartupExtensionErrors);
        var interaction = new MetadataInteraction { WaitForCancellation = true };
        var result = await session.Runner.GetOAuthProvider("auth-metadata")!.LoginAsync(new CodingAgentInteractionOAuthCallbacks(interaction));
        Assert.Equal("fixture-access", result.Access);
        await interaction.PromptEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(promptType, Assert.Single(interaction.Prompts).Type);
        Assert.True(interaction.Prompts[0].Signal.IsCancellationRequested);
    }

    /// <summary>【CodingAgent】【文本兼容】旧输入回调仍看到选择说明，info 链接标签和地址不会丢失。</summary><returns>测试任务。</returns>
    [Fact]
    public async Task LegacyAuthCallbacks_RenderMetadataAsCompleteText()
    {
        var callbacks = new MetadataTextCallbacks();
        var interaction = new CodingAgentCallbackAuthInteraction(callbacks, default);
        interaction.Notify(new ProviderAuthNotification("info", "Setup", Links: [new("https://guide.invalid", "Guide"), new("https://account.invalid")]));
        Assert.Equal("Setup\nGuide: https://guide.invalid\nhttps://account.invalid", callbacks.Progress);
        interaction.Notify(new ProviderAuthNotification("info", "Legacy setup", Url: "https://legacy.invalid", Instructions: "Read this guide"));
        Assert.Equal("Legacy setup\nRead this guide\nhttps://legacy.invalid", callbacks.Progress);
        Assert.Equal("browser", await interaction.PromptAsync(new ProviderAuthPrompt("select", "Choose", Options: [new("browser", "Browser", "Extra details")])));
        Assert.Contains("Extra details", callbacks.Prompt);
    }

    /// <summary>【CodingAgent】【元数据扩展】注册只执行给定本地交互的原生 OAuth 提供方。</summary>
    /// <param name="fixture">测试目录。</param><param name="loginBody">交互代码。</param>
    private static void WriteMetadataExtension(Fixture fixture, string loginBody)
    {
        var directory = Path.Combine(fixture.AgentDirectory, "extensions"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "metadata.js"), "export default pi=>{pi.registerProvider({id:'auth-metadata',name:'Metadata',getModels:()=>[],auth:{oauth:{name:'Login',login:async interaction=>{"
            + loginBody + "return{type:'oauth',access:'fixture-access',refresh:'',expires:Number.MAX_SAFE_INTEGER};},refresh:async c=>c,toAuth:async c=>({apiKey:c.access})}}});};");
    }

    /// <summary>【CodingAgent】【类型化宿主】捕获结构化通知与提示，并支持独立输入取消。</summary>
    private sealed class MetadataInteraction : AuthInteraction
    {
        public CancellationToken CancellationToken => default;
        public ProviderAuthNotification? Notification { get; private set; }
        public List<ProviderAuthPrompt> Prompts { get; } = [];
        public bool WaitForCancellation { get; init; }
        public TaskCompletionSource PromptEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>禁止结构化提示退化为文本。</summary><param name="prompt">文本。</param><returns>未使用。</returns>
        public Task<string> PromptAsync(string prompt) => throw new NotSupportedException();
        /// <summary>保存提示并返回合成输入，或等待提示取消。</summary><param name="prompt">结构化提示。</param><returns>合成输入。</returns>
        public async Task<string> PromptAsync(ProviderAuthPrompt prompt)
        {
            Prompts.Add(prompt);
            if (WaitForCancellation)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, prompt.Signal); }
                finally { PromptEnded.TrySetResult(); }
            }
            return prompt.Type == "select" ? "browser" : "callback";
        }
        /// <summary>禁止结构化通知退化为文本。</summary><param name="message">文本。</param>
        public void Notify(string message) => throw new NotSupportedException();
        /// <summary>保存完整通知。</summary><param name="notification">结构化通知。</param>
        public void Notify(ProviderAuthNotification notification) => Notification = notification;
    }

    /// <summary>【CodingAgent】【旧文本宿主】记录默认接口投影，保留旧扩展兼容性。</summary>
    private sealed class MetadataTextCallbacks : IOAuthLoginCallbacks
    {
        public string? Progress { get; private set; }
        public string? Prompt { get; private set; }
        /// <summary>测试不打开浏览器。</summary><param name="url">地址。</param><param name="instructions">说明。</param>
        public void OnAuth(string url, string? instructions = null) => throw new NotSupportedException();
        /// <summary>保存文本并选中浏览器。</summary><param name="message">提示。</param><param name="placeholder">占位。</param><param name="allowEmpty">空值策略。</param><returns>选中 ID。</returns>
        public Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false) { Prompt = message; return Task.FromResult("browser"); }
        /// <summary>保存说明文本。</summary><param name="message">文本。</param>
        public void OnProgress(string message) => Progress = message;
        /// <summary>不提供旧手动输入。</summary><returns>空值。</returns>
        public Task<string>? OnManualCodeInputAsync() => null;
    }
}
