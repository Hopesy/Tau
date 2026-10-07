// 作者：xxx
using Tau.CodingAgent.Runtime;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Tau.CodingAgent.Tests;

public sealed partial class CodingAgentRequestConfigurationTests
{
    /// <summary>【CodingAgent】【能力传输】真实 HTTP 验证图像和分类协议的认证地址、请求钩子和结果格式。</summary>
    /// <param name="images">是否调用图像协议。</param><returns>异步测试任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtensionProvider_CapabilitiesUseRealTransport(bool images)
    {
        using var fixture = new Fixture("capability-http", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "http.js"), $$$$$$"""
            export default pi=>{
              const image={id:'image',type:'image',provider:'capability-http',api:'openrouter-images',baseUrl:'http://127.0.0.1:1',output:['image','text']};
              const classifier={id:'classifier',type:'classifier',provider:'capability-http',api:'typesafe-system-one',baseUrl:'http://127.0.0.1:1'};
              pi.registerProvider({id:'capability-http',getModels:()=>[],getAllModels:()=>[image,classifier],
                auth:{apiKey:{name:'test',resolve:async()=>({auth:{apiKey:'capability-http-key',headers:{'X-Auth':'resolved'},baseUrl:'{{{{{{endpoint}}}}}}'}})}}});
              pi.registerCommand('capability-http',{handler:async(_,ctx)=>{
                const isImage={{{{{{images.ToString().ToLowerInvariant()}}}}}};
                const model=ctx.modelRegistry.findOfType(isImage?'image':'classifier','capability-http',isImage?'image':'classifier');
                let payloadSeen=false,responseSeen=false;
                const options={maxRetries:0,headers:{'X-Request':'explicit'},onPayload:async(payload,actual)=>{
                  if(actual.baseUrl!=='{{{{{{endpoint}}}}}}'||await ctx.modelRegistry.getApiKeyForProvider('capability-http')!=='capability-http-key')throw Error('auth callback');
                  payloadSeen=true;return {...payload,custom:'hook'};
                },onResponse:value=>{if(value.status!==200)throw Error('response status');responseSeen=true;}};
                const result=isImage?await ctx.modelRegistry.generateImages(model,{input:[{type:'text',text:'draw this'}]},options):
                  await ctx.modelRegistry.classify(model,{state:{text:'classify this'},questions:{safe:{type:'bool',instructions:'test',criteria:{true:'yes',false:'no'}}}},options);
                if(result.stopReason!=='stop'||!payloadSeen||!responseSeen||typeof result.timestamp!=='number'||result.usage.input!==12)throw Error('HTTP result '+JSON.stringify(result));
                if(isImage?result.output[1].data!=='aW1hZ2U=':result.answers.safe.probability!==0.9)throw Error('HTTP content');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" }, deadline.Token);
        var served = ServeCapabilityAsync(server, images, deadline.Token);
        await DrainAsync(session.RunAsync("/capability-http", deadline.Token));
        var request = await served;
        Assert.Contains("Authorization: Bearer capability-http-key", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Auth: resolved", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Request: explicit", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(images ? "POST /chat/completions" : "POST /systemone", request, StringComparison.Ordinal);
    }

    /// <summary>【CodingAgent】【能力测试服务】读取真实请求并返回图像或分类协议响应。</summary>
    /// <param name="server">监听器。</param><param name="images">响应类型。</param><param name="token">截止信号。</param><returns>原始请求头。</returns>
    private static async Task<string> ServeCapabilityAsync(TcpListener server, bool images, CancellationToken token)
    {
        using var client = await server.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        var headers = new StringBuilder();
        var single = new byte[1];
        while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await stream.ReadExactlyAsync(single, token);
            headers.Append((char)single[0]);
            Assert.True(headers.Length < 32768);
        }
        var length = int.Parse(headers.ToString().Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("hook", document.RootElement.GetProperty("custom").GetString());
        Assert.Contains(images ? "draw this" : "classify this", document.RootElement.GetRawText());
        var response = images
            ? """{"choices":[{"message":{"content":"caption","images":[{"image_url":{"url":"data:image/png;base64,aW1hZ2U="}}]}}],"usage":{"prompt_tokens":12,"completion_tokens":3}}"""
            : """{"answers":{"safe":{"type":"noul","noul":0.9}},"usage":{"input_tokens":12,"output_tokens":3}}""";
        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), token);
        await stream.WriteAsync(bytes, token);
        return headers.ToString();
    }

    /// <summary>【CodingAgent】【原生能力】验证完整认证、原生结果、自定义选项、回调重入及取消后的可用性。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_NativeCapabilitiesKeepAuthHooksAndResults()
    {
        using var fixture = new Fixture("native-capabilities", "models");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "native.js"), """
            export default pi=>{
              const models=[{id:'same',type:'image',provider:'capabilities',api:'custom-image',baseUrl:'http://baseline.test',output:['image','text']},
                {id:'same',type:'classifier',provider:'capabilities',api:'custom-classifier',baseUrl:'http://baseline.test'}];
              const usage={input:7,output:3,cacheRead:0,cacheWrite:0,totalTokens:10,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}};
              const check=async(model,options)=>{
                if(model.baseUrl!=='http://resolved.test'||options.apiKey!=='capability-key'||options.headers['X-Auth']!=='dynamic'||options.env.TENANT!=='test')throw Error('incomplete request authentication');
                if(options.customValue?.nested!==41||options.timeoutMs!==4000)throw Error('lost native options');
                const payload=await options.onPayload({test:1},model);
                if(payload.test!==2)throw Error('payload callback');
                await options.onResponse({status:200,headers:{'x-response':'ok'}},model);
              };
              pi.registerProvider({id:'capabilities',name:'Capabilities',getModels:()=>[],getAllModels:()=>models,
                auth:{apiKey:{name:'test',resolve:async()=>({auth:{apiKey:'capability-key',headers:{'X-Auth':'dynamic'},baseUrl:'http://resolved.test'},env:{TENANT:'test'}})}},
                generateImages:async(model,context,options)=>{
                  await check(model,options);
                  if(context.input[0].text!=='draw')throw Error('input lost');
                  return {api:model.api,provider:model.provider,model:model.id,output:[{type:'text',text:'caption'},{type:'image',mimeType:'image/png',data:'aW1hZ2U='}],usage,responseId:'image-id',timestamp:123456789,stopReason:'stop'};
                },
                classify:async(model,context,options)=>{
                  if(context.state.cancel){await new Promise((resolve,reject)=>options.signal.addEventListener('abort',()=>reject(Error('cancelled')),{once:true}));}
                  await check(model,options);
                  if(context.questions.safe.type!=='bool'||options.temperature!==0.2)throw Error('classifier context/options lost '+JSON.stringify({context,temperature:options.temperature}));
                  return {api:model.api,provider:model.provider,model:model.id,answers:{safe:{type:'bool',probability:0.85}},usage,timestamp:123456790,stopReason:'stop'};
                }});
              pi.registerCommand('native-capabilities',{handler:async(_,ctx)=>{
                const registry=ctx.modelRegistry;
                const image=registry.findOfType('image','capabilities','same'),classifier=registry.findOfType('classifier','capabilities','same');
                let payloads=0,responses=0;
                const options={timeoutMs:4000,temperature:0.2,customValue:{nested:41},onPayload:async payload=>{
                  if(await registry.getApiKeyForProvider('capabilities')!=='capability-key')throw Error('callback reentry failed');payloads++;return {...payload,test:2};
                },onResponse:value=>{if(value.status!==200)throw Error('response callback');responses++;}};
                const images=await registry.generateImages(image,{input:[{type:'text',text:'draw'}]},options);
                if(images.stopReason!=='stop'||images.output[1].data!=='aW1hZ2U='||images.usage.totalTokens!==10||images.timestamp!==123456789||images.responseId!=='image-id')throw Error('image result '+JSON.stringify(images));
                const context={state:{text:'hello'},questions:{safe:{type:'bool',instructions:'test',criteria:{true:'yes',false:'no'}}}};
                const result=await registry.classify(classifier,context,options);
                if(result.stopReason!=='stop'||result.answers.safe.probability!==0.85||result.usage.input!==7||result.timestamp!==123456790||payloads!==2||responses!==2)throw Error('classifier result '+JSON.stringify(result));
                const wrong=await registry.classify(image,context,options);
                if(wrong.stopReason!=='error')throw Error('wrong model should resolve error');
                const controller=new AbortController();const pending=registry.classify(classifier,{...context,state:{cancel:true}},{...options,signal:controller.signal});
                setTimeout(()=>controller.abort(),80);const aborted=await pending;
                if(aborted.stopReason!=='aborted')throw Error('cancelled request rejected');
                if((await registry.classify(classifier,context,options)).stopReason!=='stop')throw Error('request after cancellation failed');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "session-provider", ModelId = "test-model" }, deadline.Token);
        await DrainAsync(session.RunAsync("/native-capabilities", deadline.Token));
    }

    /// <summary>【CodingAgent】【能力目录】验证跨命令快照、原生输出字段、混合注册和注销恢复。</summary>
    /// <returns>异步测试任务。</returns>
    [Fact]
    public async Task ExtensionProvider_TypedRegistryKeepsAllCapabilities()
    {
        using var fixture = new Fixture("typed-registry", "models");
        var directory = Path.Combine(fixture.AgentDirectory, "extensions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "typed.js"), """
            export default pi=>{
              pi.registerProvider('typed-registry',{api:'openai-completions',apiKey:'test',baseUrl:'http://127.0.0.1:1',models:[
                {id:'same'}, {id:'same',type:'image',api:'openrouter-images',output:['image','text']},
                {id:'same',type:'classifier',api:'typesafe-system-one'}]});
              pi.registerCommand('typed-check',{handler:async(_,ctx)=>{
                const registry=ctx.modelRegistry;
                if(registry.getAll().some(model=>model.type&&model.type!=='chat'))throw Error('nonchat leaked into chat selection');
                for(const type of ['chat','image','classifier']){
                  const models=registry.getModelsOfType(type,'typed-registry');
                  if(models.length!==1||registry.findOfType(type,'typed-registry','same')?.id!=='same'||registry.getModelOfType(type,'typed-registry','same')?.id!=='same')throw Error('lost '+type);
                  models[0].name='mutated';
                  if(registry.getModelOfType(type,'typed-registry','same').name==='mutated')throw Error('mutable snapshot');
                }
                if(registry.findOfType('image','typed-registry','same').output.join(',')!=='image,text')throw Error('output shape lost');
                if(!registry.getModelsOfType('classifier','typesafe').length)throw Error('built-in classifier missing');
                const original=registry.getModelsOfType('image','openrouter');
                if(!original.length)throw Error('built-in images missing');
                registry.registerProvider('openrouter',{models:[{id:'only',type:'image',api:'openrouter-images',baseUrl:'http://127.0.0.1:1'}]});
                if(registry.getModelsOfType('image','openrouter').length!==1||registry.getModelsOfType('chat','openrouter').length)throw Error('replace mixed models');
                registry.unregisterProvider('openrouter');
                if(registry.getModelsOfType('image','openrouter').length!==original.length||!registry.getModelsOfType('chat','openrouter').length)throw Error('restore mixed baseline');
              }});
            };
            """);
        await using var session = await CodingAgentSdk.CreateSessionAsync(new()
        { Cwd = fixture.Root, AgentDirectory = fixture.AgentDirectory, NoSession = true, ProviderId = "typed-registry" });
        await DrainAsync(session.RunAsync("/typed-check"));
        await DrainAsync(session.RunAsync("/typed-check"));
    }
}
