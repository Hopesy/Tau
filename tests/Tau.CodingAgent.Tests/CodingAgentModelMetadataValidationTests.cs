// 作者：xxx
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【元数据校验】真实扩展同步捕获无效元数据，目录和后续注册保持可用。</summary>
    /// <param name="overrideOnly">是否校验尚未匹配模型的覆盖。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtensionProvider_MetadataErrorsAreSynchronousAndAtomic(bool overrideOnly)
    {
        using var fixture = new Fixture("metadata-validation", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "metadata-validation.js"), $$$$$$"""
            export default pi=>{
              const base={api:'openai-completions',baseUrl:'https://metadata.invalid'};
              pi.registerProvider('metadata',{...base,models:[{id:'keep',promptCache:{short:60}}]});
              pi.registerCommand('validate',{handler:(_,ctx)=>{
                const invalid=[
                  {inputLimits:null},{inputLimits:[]},{inputLimits:{maxRequestBytes:0}},
                  {inputLimits:{images:false}},{inputLimits:{images:{maxPerMessage:-1}}},
                  {inputLimits:{images:{maxPerRequest:1.5}}},{inputLimits:{images:{resize:null}}},
                  {inputLimits:{images:{resize:{maxWidth:0}}}},{inputLimits:{images:{resize:{maxHeight:'1'}}}},
                  {inputLimits:{images:{resize:{maxBytes:true}}}},{inputLimits:{images:{resize:{jpegQuality:101}}}},
                  {promptCache:null},{promptCache:[]},{promptCache:{short:0}},{promptCache:{short:null}},
                  {promptCache:{long:-1}},{promptCache:{long:Infinity}},{promptCache:{long:NaN}},{promptCache:{long:'60'}}
                ];
                for(const metadata of invalid){
                  let caught=false;
                  try{pi.registerProvider('metadata',{{{{{{(overrideOnly ? "{modelOverrides:{missing:metadata}}" : "{...base,models:[{id:'replacement',...metadata}]}")}}}}}});}
                  catch(error){if(!/inputLimits|promptCache/.test(error.message))throw error;caught=true;}
                  if(!caught)throw Error('invalid metadata was not rejected synchronously');
                  if(ctx.modelRegistry.find('metadata','keep')?.promptCache?.short!==60)throw Error('previous registration lost');
                }
                pi.registerProvider('after',{...base,models:[{id:'valid',promptCache:{short:0.5},inputLimits:{images:{resize:{jpegQuality:1}}}}]});
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" });
        Assert.Empty(session.StartupExtensionErrors);
        await DrainAsync(session.RunAsync("/validate"));
        Assert.Equal("keep", Assert.Single(session.Runner.GetModels("metadata")).Id);
        Assert.Equal(0.5, Assert.Single(session.Runner.GetModels("after")).PromptCache!.Short);
    }

    /// <summary>【CodingAgent】【配置诊断】无效文件允许内置模型启动，并将具体字段错误送达原生扩展查询。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionModelRegistryReportsFileMetadataErrors()
    {
        using var fixture = new Fixture("metadata-diagnostic", "models");
        File.WriteAllText(Path.Combine(fixture.AgentDirectory, "models.json"), """{"providers":{"invalid":{"models":[{"id":"bad","promptCache":{"short":0}}]}}}""");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "diagnostic.js"), """
            export default pi=>{
              pi.registerCommand('verify',{handler:(_,ctx)=>{
                const error=ctx.modelRegistry.getError();
                if(!error?.includes('promptCache.short'))throw Error('file diagnostic missing');
                if(ctx.modelRegistry.getAll().some(model=>model.provider==='invalid'))throw Error('invalid file partially loaded');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "openai" });
        Assert.Empty(session.StartupExtensionErrors);
        await DrainAsync(session.RunAsync("/verify"));
    }
}
