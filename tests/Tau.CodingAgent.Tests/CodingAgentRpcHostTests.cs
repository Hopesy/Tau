using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text.Json;
using Tau.AgentCore;
using Tau.AgentCore.Runtime;
using Tau.Ai;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CodingAgentRpcHostTests
{
    [Fact]
    public async Task RunAsync_PromptWritesAcceptedResponseAndAgentEvents()
    {
        FakeCodingAgentRunner? runnerRef = null;
        var runner = new FakeCodingAgentRunner((input, _) => RunPrompt(runnerRef!, input));
        runnerRef = runner;
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"hello\"}\n"),
            output);

        var exitCode = await host.RunAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(["hello"], runner.Inputs);
        Assert.Equal(2, runner.Messages.Count);

        var lines = ReadJsonLines(output);
        var response = FindResponse(lines, "prompt");
        Assert.Equal("p1", response.GetProperty("id").GetString());
        Assert.True(response.GetProperty("success").GetBoolean());

        var responseIndex = Enumerable.Range(0, lines.Count)
            .Single(index =>
                lines[index].GetProperty("type").GetString() == "response" &&
                lines[index].GetProperty("command").GetString() == "prompt");
        var agentStartIndex = lines
            .Select((line, index) => (line, index))
            .Single(item => item.line.GetProperty("type").GetString() == "agent_start")
            .index;
        Assert.True(responseIndex < agentStartIndex);

        var textDelta = lines.Single(line =>
            line.GetProperty("type").GetString() == "message_update" &&
            line.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "text_delta");
        Assert.False(textDelta.TryGetProperty("streamEvent", out _));
        Assert.Equal("rpc ok", textDelta.GetProperty("assistantMessageEvent").GetProperty("delta").GetString());

        Assert.Contains(lines, line => line.GetProperty("type").GetString() == "agent_end");
    }

    [Fact]
    public async Task RunAsync_PromptReturnsErrorWhenRunnerFailsBeforeStarting()
    {
        var runner = new FakeCodingAgentRunner((_, _) =>
            throw new InvalidOperationException("model preflight failed"));
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"hello\"}\n"),
            output);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var response = Assert.Single(lines);
        Assert.Equal("response", response.GetProperty("type").GetString());
        Assert.Equal("p1", response.GetProperty("id").GetString());
        Assert.Equal("prompt", response.GetProperty("command").GetString());
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Equal("model preflight failed", response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunAsync_PromptReturnsErrorWhenRunnerThrowsBeforeFirstEvent()
    {
        var runner = new FakeCodingAgentRunner((_, _) => ThrowBeforeFirstEvent());
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"hello\"}\n"),
            output);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var response = Assert.Single(lines);
        Assert.Equal("response", response.GetProperty("type").GetString());
        Assert.Equal("p1", response.GetProperty("id").GetString());
        Assert.Equal("prompt", response.GetProperty("command").GetString());
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Equal("async preflight failed", response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunAsync_MessageUpdateUsesUpstreamAssistantMessageEventShape()
    {
        var runner = new FakeCodingAgentRunner((_, _) => RunPromptWithAssistantMessageEvents());
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"hello\"}\n"),
            output);

        await host.RunAsync();

        var events = ReadJsonLines(output)
            .Where(line => line.GetProperty("type").GetString() == "message_update")
            .Select(line =>
            {
                Assert.False(line.TryGetProperty("streamEvent", out _));
                return line.GetProperty("assistantMessageEvent");
            })
            .ToArray();

        var textStart = events.Single(evt => evt.GetProperty("type").GetString() == "text_start");
        Assert.Equal(0, textStart.GetProperty("contentIndex").GetInt32());
        Assert.Equal("assistant", textStart.GetProperty("partial").GetProperty("role").GetString());

        var textDelta = events.Single(evt => evt.GetProperty("type").GetString() == "text_delta");
        Assert.Equal("he", textDelta.GetProperty("delta").GetString());
        Assert.Equal("hello", textDelta.GetProperty("partial").GetProperty("content")[0].GetProperty("text").GetString());

        var textEnd = events.Single(evt => evt.GetProperty("type").GetString() == "text_end");
        Assert.Equal("hello", textEnd.GetProperty("content").GetString());
        Assert.Equal("hello", textEnd.GetProperty("partial").GetProperty("content")[0].GetProperty("text").GetString());

        var thinkingEnd = events.Single(evt => evt.GetProperty("type").GetString() == "thinking_end");
        Assert.Equal("plan", thinkingEnd.GetProperty("content").GetString());
        Assert.Equal("plan", thinkingEnd.GetProperty("partial").GetProperty("content")[0].GetProperty("thinking").GetString());

        var toolCallEnd = events.Single(evt => evt.GetProperty("type").GetString() == "toolcall_end");
        Assert.Equal("call_1", toolCallEnd.GetProperty("toolCall").GetProperty("id").GetString());
        Assert.Equal("bash", toolCallEnd.GetProperty("toolCall").GetProperty("name").GetString());
        var toolCallArguments = toolCallEnd.GetProperty("toolCall").GetProperty("arguments");
        Assert.Equal(JsonValueKind.Object, toolCallArguments.ValueKind);
        Assert.Equal("pwd", toolCallArguments.GetProperty("command").GetString());
        Assert.Equal("sig_1", toolCallEnd.GetProperty("toolCall").GetProperty("thoughtSignature").GetString());
        Assert.Equal(
            JsonValueKind.Object,
            toolCallEnd.GetProperty("partial").GetProperty("content")[0].GetProperty("arguments").ValueKind);

        var done = events.Single(evt => evt.GetProperty("type").GetString() == "done");
        Assert.Equal("toolUse", done.GetProperty("reason").GetString());
        Assert.Equal("assistant", done.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal(
            JsonValueKind.Object,
            done.GetProperty("message").GetProperty("content")[0].GetProperty("arguments").ValueKind);
    }

    [Fact]
    public async Task RunAsync_PromptPassesCommandAndTreeSessionLogContextToRunner()
    {
        using var temp = TempDirectory.Create();
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var sessionId = treeController.GetSummary().SessionId;
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"hello\"}\n"),
            output,
            treeSessionController: treeController);

        await host.RunAsync();

        var context = Assert.Single(runner.RunLogContexts);
        Assert.NotNull(context);
        Assert.Equal("p1", context!.CorrelationId);
        Assert.Equal("p1", context.MessageId);
        Assert.Equal(sessionId, context.SessionId);
    }

    [Fact]
    public async Task RunAsync_UsesStrictLfJsonlAndDoesNotSplitUnicodeLineSeparators()
    {
        var prompt = "first\u2028second";
        FakeCodingAgentRunner? runnerRef = null;
        var runner = new FakeCodingAgentRunner((input, _) => RunPrompt(runnerRef!, input));
        runnerRef = runner;
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"first\u2028second\"}\n"),
            output);

        await host.RunAsync();

        Assert.Equal([prompt], runner.Inputs);
        Assert.True(FindResponse(ReadJsonLines(output), "prompt").GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_SteerFollowUpAndAbortTargetActivePrompt()
    {
        var runner = new FakeCodingAgentRunner((_, ct) => BlockingRun(ct));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"work\"}",
            "{\"id\":\"s1\",\"type\":\"steer\",\"message\":\"adjust now\"}",
            "{\"id\":\"f1\",\"type\":\"follow_up\",\"message\":\"afterwards\"}",
            "{\"id\":\"a1\",\"type\":\"abort\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output);

        await host.RunAsync();

        Assert.Equal(["adjust now"], runner.SteeringInputs);
        Assert.Equal(["afterwards"], runner.FollowUpInputs);

        var lines = ReadJsonLines(output);
        Assert.True(FindResponse(lines, "prompt").GetProperty("success").GetBoolean());
        Assert.True(FindResponse(lines, "steer").GetProperty("success").GetBoolean());
        Assert.True(FindResponse(lines, "follow_up").GetProperty("success").GetBoolean());
        Assert.True(FindResponse(lines, "abort").GetProperty("success").GetBoolean());
        Assert.Contains(lines, line =>
            line.GetProperty("type").GetString() == "agent_end" &&
            line.GetProperty("errorMessage").GetString() == "Cancelled");
    }

    [Fact]
    public async Task RunAsync_SteerFollowUpAndActivePromptStreamingBehaviorAcceptImages()
    {
        var runner = new FakeCodingAgentRunner((_, ct) => BlockingRun(ct));
        var output = new StringWriter();
        var imageJson = "{\"data\":\"aGVsbG8=\",\"mimeType\":\"image/png\"}";
        var input = string.Join(
            "\n",
            "{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"work\"}",
            $"{{\"id\":\"s1\",\"type\":\"steer\",\"message\":\"adjust image\",\"images\":[{imageJson}]}}",
            $"{{\"id\":\"f1\",\"type\":\"follow_up\",\"message\":\"afterwards image\",\"images\":[{imageJson}]}}",
            $"{{\"id\":\"ps1\",\"type\":\"prompt\",\"message\":\"stream steer image\",\"streamingBehavior\":\"steer\",\"images\":[{imageJson}]}}",
            $"{{\"id\":\"pf1\",\"type\":\"prompt\",\"message\":\"stream follow image\",\"streamingBehavior\":\"followUp\",\"images\":[{imageJson}]}}",
            "{\"id\":\"a1\",\"type\":\"abort\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output);

        await host.RunAsync();

        Assert.Equal(["adjust image", "stream steer image"], runner.SteeringInputs);
        Assert.Equal(["afterwards image", "stream follow image"], runner.FollowUpInputs);
        Assert.Collection(
            runner.SteeringContentInputs,
            blocks => AssertTextAndImage(blocks, "adjust image"),
            blocks => AssertTextAndImage(blocks, "stream steer image"));
        Assert.Collection(
            runner.FollowUpContentInputs,
            blocks => AssertTextAndImage(blocks, "afterwards image"),
            blocks => AssertTextAndImage(blocks, "stream follow image"));

        var lines = ReadJsonLines(output);
        Assert.True(FindResponseById(lines, "s1").GetProperty("success").GetBoolean());
        Assert.True(FindResponseById(lines, "f1").GetProperty("success").GetBoolean());
        Assert.True(FindResponseById(lines, "ps1").GetProperty("success").GetBoolean());
        Assert.True(FindResponseById(lines, "pf1").GetProperty("success").GetBoolean());
        Assert.True(FindResponseById(lines, "a1").GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_GetStateReportsPendingMessageCountForActivePromptQueues()
    {
        var runner = new FakeCodingAgentRunner((_, ct) => BlockingRun(ct));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"work\"}",
            "{\"id\":\"s1\",\"type\":\"steer\",\"message\":\"adjust now\"}",
            "{\"id\":\"f1\",\"type\":\"follow_up\",\"message\":\"afterwards\"}",
            "{\"id\":\"state1\",\"type\":\"get_state\"}",
            "{\"id\":\"a1\",\"type\":\"abort\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output);

        await host.RunAsync();

        var state = FindResponseById(ReadJsonLines(output), "state1").GetProperty("data");
        Assert.True(state.GetProperty("isStreaming").GetBoolean());
        Assert.False(state.GetProperty("isCompacting").GetBoolean());
        Assert.Equal(2, state.GetProperty("pendingMessageCount").GetInt32());
    }

    [Fact]
    public async Task RunAsync_GetStateDecrementsPendingMessageCountBeforeUserMessageStartEvent()
    {
        var steeringSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeCodingAgentRunner((_, ct) => RunPromptWithQueuedUserMessage(steeringSeen.Task, ct))
        {
            SteeringObserver = _ => steeringSeen.TrySetResult()
        };
        var input = new AsyncLineReader();
        var output = new JsonLineWriter();
        var host = new CodingAgentRpcHost(runner, input, output);
        var hostTask = host.RunAsync();

        input.Enqueue("{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"work\"}");
        await output.WaitForJsonLineAsync(
            line => line.GetProperty("type").GetString() == "response" &&
                    line.GetProperty("command").GetString() == "prompt",
            TimeSpan.FromSeconds(5));

        input.Enqueue("{\"id\":\"s1\",\"type\":\"steer\",\"message\":\"adjust now\"}");
        await output.WaitForJsonLineAsync(
            line => line.GetProperty("type").GetString() == "response" &&
                    line.GetProperty("command").GetString() == "steer",
            TimeSpan.FromSeconds(5));
        await output.WaitForJsonLineAsync(
            line => line.GetProperty("type").GetString() == "message_start" &&
                    line.GetProperty("message").GetProperty("role").GetString() == "user" &&
                    line.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString() == "adjust now",
            TimeSpan.FromSeconds(5));

        input.Enqueue("{\"id\":\"state1\",\"type\":\"get_state\"}");
        var state = await output.WaitForJsonLineAsync(
            line => line.GetProperty("type").GetString() == "response" &&
                    line.GetProperty("id").GetString() == "state1",
            TimeSpan.FromSeconds(5));

        Assert.Equal(0, state.GetProperty("data").GetProperty("pendingMessageCount").GetInt32());

        input.Enqueue("{\"id\":\"a1\",\"type\":\"abort\"}");
        input.Complete();
        await hostTask;
    }

    [Fact]
    public async Task RunAsync_GetStateSetModelMessagesAndCommandsReturnStructuredData()
    {
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.ConfigureAuth("openai", "google");
        runner.MutableMessages.Add(new UserMessage("saved prompt"));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"state1\",\"type\":\"get_state\"}",
            "{\"id\":\"available1\",\"type\":\"get_available_models\"}",
            "{\"id\":\"model1\",\"type\":\"set_model\",\"provider\":\"google\",\"modelId\":\"gemini-2.5-pro\"}",
            "{\"id\":\"messages1\",\"type\":\"get_messages\"}",
            "{\"id\":\"commands1\",\"type\":\"get_commands\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var state = FindResponse(lines, "get_state");
        Assert.Equal("state1", state.GetProperty("id").GetString());
        Assert.Equal("openai", state.GetProperty("data").GetProperty("model").GetProperty("provider").GetString());
        Assert.Equal(1, state.GetProperty("data").GetProperty("messageCount").GetInt32());
        Assert.Equal("one-at-a-time", state.GetProperty("data").GetProperty("steeringMode").GetString());
        Assert.Equal("one-at-a-time", state.GetProperty("data").GetProperty("followUpMode").GetString());
        Assert.False(state.GetProperty("data").GetProperty("autoCompactionEnabled").GetBoolean());

        var availableModels = FindResponse(lines, "get_available_models")
            .GetProperty("data")
            .GetProperty("models")
            .EnumerateArray()
            .Select(model => $"{model.GetProperty("provider").GetString()}/{model.GetProperty("id").GetString()}")
            .ToArray();
        Assert.Contains("openai/gpt-5.4", availableModels);
        Assert.Contains("google/gemini-2.5-pro", availableModels);

        var model = FindResponse(lines, "set_model");
        Assert.Equal("google", model.GetProperty("data").GetProperty("provider").GetString());
        Assert.Equal("gemini-2.5-pro", model.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("google", runner.Model.Provider);

        var messages = FindResponse(lines, "get_messages")
            .GetProperty("data")
            .GetProperty("messages");
        Assert.Equal("user", messages[0].GetProperty("role").GetString());

        var commands = FindResponse(lines, "get_commands")
            .GetProperty("data")
            .GetProperty("commands")
            .EnumerateArray()
            .Select(command => command.GetProperty("name").GetString())
            .ToArray();
        Assert.DoesNotContain("compact", commands);
        Assert.DoesNotContain("fork", commands);
    }

    [Fact]
    public async Task RunAsync_GetMessagesReturnsToolCallArgumentsAsObject()
    {
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.MutableMessages.Add(new AssistantMessage([
            new ToolCallContent("call_1", "bash", """{"command":"pwd","limit":3}""")
            {
                ThoughtSignature = "sig_1"
            }
        ]));
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"messages1\",\"type\":\"get_messages\"}\n"),
            output);

        await host.RunAsync();

        var toolCall = FindResponse(ReadJsonLines(output), "get_messages")
            .GetProperty("data")
            .GetProperty("messages")[0]
            .GetProperty("content")[0];

        Assert.Equal("toolCall", toolCall.GetProperty("type").GetString());
        Assert.Equal("call_1", toolCall.GetProperty("id").GetString());
        Assert.Equal("bash", toolCall.GetProperty("name").GetString());
        Assert.Equal("sig_1", toolCall.GetProperty("thoughtSignature").GetString());

        var arguments = toolCall.GetProperty("arguments");
        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);
        Assert.Equal("pwd", arguments.GetProperty("command").GetString());
        Assert.Equal(3, arguments.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task RunAsync_GetSessionStatsReturnsUsageCostTotals()
    {
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "stats session"
        };
        runner.MutableMessages.Add(new UserMessage("price this"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("priced")])
        {
            Usage = new Usage(
                InputTokens: 100,
                OutputTokens: 20,
                CacheReadTokens: 3,
                CacheWriteTokens: 4,
                ServiceTier: "flex",
                Cost: new UsageCost(0.01m, 0.02m, 0.003m, 0.004m))
        });
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"stats1\",\"type\":\"get_session_stats\"}\n"),
            output);

        await host.RunAsync();

        var response = FindResponse(ReadJsonLines(output), "get_session_stats");
        Assert.Equal("stats1", response.GetProperty("id").GetString());
        Assert.True(response.GetProperty("success").GetBoolean());
        var data = response.GetProperty("data");
        Assert.Equal("openai", data.GetProperty("provider").GetString());
        Assert.Equal("gpt-5.4", data.GetProperty("model").GetString());
        Assert.Equal("stats session", data.GetProperty("sessionName").GetString());
        var tokens = data.GetProperty("tokens");
        Assert.Equal(100, tokens.GetProperty("input").GetInt32());
        Assert.Equal(20, tokens.GetProperty("output").GetInt32());
        Assert.Equal(3, tokens.GetProperty("cacheRead").GetInt32());
        Assert.Equal(4, tokens.GetProperty("cacheWrite").GetInt32());
        Assert.Equal(127, tokens.GetProperty("total").GetInt32());
        Assert.Equal(0.037m, data.GetProperty("cost").GetDecimal());
        Assert.Equal(1, data.GetProperty("costRecords").GetInt32());
    }

    [Fact]
    public async Task RunAsync_GetSessionStatsWithTreeSessionUsesPersistedBranchUsageCost()
    {
        using var temp = TempDirectory.Create();
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.MutableMessages.Add(new UserMessage("price this"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("priced")])
        {
            Usage = new Usage(
                InputTokens: 100,
                OutputTokens: 20,
                CacheReadTokens: 3,
                CacheWriteTokens: 4,
                ServiceTier: "flex",
                Cost: new UsageCost(0.01m, 0.02m, 0.003m, 0.004m))
        });
        treeController.SyncFromRunner(runner);
        runner.MutableMessages[1] = new AssistantMessage([new TextContent("priced without runtime usage")]);
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"stats1\",\"type\":\"get_session_stats\"}\n"),
            output,
            treeSessionController: treeController);

        await host.RunAsync();

        var data = FindResponse(ReadJsonLines(output), "get_session_stats").GetProperty("data");
        var tokens = data.GetProperty("tokens");
        Assert.Equal(100, tokens.GetProperty("input").GetInt32());
        Assert.Equal(20, tokens.GetProperty("output").GetInt32());
        Assert.Equal(3, tokens.GetProperty("cacheRead").GetInt32());
        Assert.Equal(4, tokens.GetProperty("cacheWrite").GetInt32());
        Assert.Equal(127, tokens.GetProperty("total").GetInt32());
        Assert.Equal(0.037m, data.GetProperty("cost").GetDecimal());
        Assert.Equal(1, data.GetProperty("costRecords").GetInt32());
    }

    [Fact]
    public async Task RunAsync_GetCommandsIncludesPromptSkillAndExtensionCommands()
    {
        using var temp = TempDirectory.Create();
        var prompts = Path.Combine(temp.Path, ".tau", "prompts");
        var skills = Path.Combine(temp.Path, ".tau", "skills", "reviewer");
        var extensions = Path.Combine(temp.Path, ".tau", "extensions");
        Directory.CreateDirectory(prompts);
        Directory.CreateDirectory(skills);
        Directory.CreateDirectory(extensions);

        var promptFile = Path.Combine(prompts, "review.md");
        await File.WriteAllTextAsync(
            promptFile,
            """
            ---
            description: Review prompt
            argument-hint: <file>
            ---
            Review $1.
            """);
        var skillFile = Path.Combine(skills, "SKILL.md");
        await File.WriteAllTextAsync(
            skillFile,
            """
            ---
            name: reviewer
            description: Review skill
            ---
            Check the diff.
            """);
        var extensionFile = Path.Combine(extensions, "commands.json");
        await File.WriteAllTextAsync(
            extensionFile,
            """
            {
              "commands": [
                {
                  "name": "hello",
                  "description": "Say hello",
                  "argumentHint": "<name>",
                  "response": "Hello $ARGUMENTS"
                }
              ]
            }
            """);

        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"commands1\",\"type\":\"get_commands\"}\n"),
            output,
            promptTemplateStore: new CodingAgentPromptTemplateStore(cwd: temp.Path),
            skillStore: new CodingAgentSkillStore(cwd: temp.Path),
            extensionCommandStore: new CodingAgentExtensionCommandStore(cwd: temp.Path));

        await host.RunAsync();

        var commands = FindResponse(ReadJsonLines(output), "get_commands")
            .GetProperty("data")
            .GetProperty("commands")
            .EnumerateArray()
            .ToArray();

        var extension = commands.Single(command => command.GetProperty("name").GetString() == "hello");
        Assert.Equal("Say hello", extension.GetProperty("description").GetString());
        Assert.Equal("extension", extension.GetProperty("source").GetString());
        Assert.False(extension.TryGetProperty("usage", out _));
        Assert.Equal(extensionFile, extension.GetProperty("sourceInfo").GetProperty("path").GetString());
        Assert.Equal("project", extension.GetProperty("sourceInfo").GetProperty("scope").GetString());

        var prompt = commands.Single(command => command.GetProperty("name").GetString() == "review");
        Assert.Equal("Review prompt", prompt.GetProperty("description").GetString());
        Assert.Equal("prompt", prompt.GetProperty("source").GetString());
        Assert.False(prompt.TryGetProperty("usage", out _));
        Assert.Equal(promptFile, prompt.GetProperty("sourceInfo").GetProperty("path").GetString());

        var skill = commands.Single(command => command.GetProperty("name").GetString() == "skill:reviewer");
        Assert.Equal("Review skill", skill.GetProperty("description").GetString());
        Assert.Equal("skill", skill.GetProperty("source").GetString());
        Assert.False(skill.TryGetProperty("usage", out _));
        Assert.Equal(skillFile, skill.GetProperty("sourceInfo").GetProperty("path").GetString());
        Assert.Equal(skills, skill.GetProperty("sourceInfo").GetProperty("baseDir").GetString());
    }

    [Fact]
    public async Task RunAsync_ExtensionUiSelectEmitsRequestAndCompletesFromRpcResponse()
    {
        var input = new AsyncLineReader();
        var output = new JsonLineWriter();
        var bridge = new CodingAgentRpcExtensionUiBridge();
        var host = new CodingAgentRpcHost(
            new FakeCodingAgentRunner((_, _) => EmptyRun()),
            input,
            output,
            extensionUi: bridge);

        var runTask = host.RunAsync();
        var selectTask = bridge.SelectAsync("Pick target", ["alpha", "beta"]);
        var request = await output.WaitForJsonLineAsync(
                line => line.GetProperty("type").GetString() == "extension_ui_request",
                TimeSpan.FromSeconds(5));

        Assert.Equal("select", request.GetProperty("method").GetString());
        Assert.Equal("Pick target", request.GetProperty("title").GetString());
        Assert.False(request.TryGetProperty("timeout", out _));
        Assert.Equal(
            ["alpha", "beta"],
            request.GetProperty("options").EnumerateArray().Select(option => option.GetString()!).ToArray());
        var requestId = request.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestId));

        input.Enqueue($"{{\"type\":\"extension_ui_response\",\"id\":\"{requestId}\",\"value\":\"beta\"}}");
        var selected = await selectTask.WaitAsync(TimeSpan.FromSeconds(5));
        input.Complete();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("beta", selected);
        Assert.DoesNotContain(
            ReadJsonLines(output),
            line => line.GetProperty("type").GetString() == "response");
    }

    [Fact]
    public async Task RunAsync_ExtensionUiConfirmInputAndEditorCompleteFromRpcResponses()
    {
        var input = new AsyncLineReader();
        var output = new JsonLineWriter();
        var bridge = new CodingAgentRpcExtensionUiBridge();
        var host = new CodingAgentRpcHost(
            new FakeCodingAgentRunner((_, _) => EmptyRun()),
            input,
            output,
            extensionUi: bridge);

        var runTask = host.RunAsync();

        var confirmTask = bridge.ConfirmAsync("Continue?", "Run the task");
        var confirmRequest = await output.WaitForJsonLineAsync(
            line => line.TryGetProperty("method", out var method) && method.GetString() == "confirm",
            TimeSpan.FromSeconds(5));
        Assert.Equal("Run the task", confirmRequest.GetProperty("message").GetString());
        input.Enqueue($"{{\"type\":\"extension_ui_response\",\"id\":\"{confirmRequest.GetProperty("id").GetString()}\",\"confirmed\":true}}");
        Assert.True(await confirmTask.WaitAsync(TimeSpan.FromSeconds(5)));

        var inputTask = bridge.InputAsync("Name", "project");
        var inputRequest = await output.WaitForJsonLineAsync(
            line => line.TryGetProperty("method", out var method) && method.GetString() == "input",
            TimeSpan.FromSeconds(5));
        Assert.Equal("project", inputRequest.GetProperty("placeholder").GetString());
        input.Enqueue($"{{\"type\":\"extension_ui_response\",\"id\":\"{inputRequest.GetProperty("id").GetString()}\",\"value\":\"Tau\"}}");
        Assert.Equal("Tau", await inputTask.WaitAsync(TimeSpan.FromSeconds(5)));

        var editorTask = bridge.EditorAsync("Edit prompt", "prefill");
        var editorRequest = await output.WaitForJsonLineAsync(
            line => line.TryGetProperty("method", out var method) && method.GetString() == "editor",
            TimeSpan.FromSeconds(5));
        Assert.Equal("prefill", editorRequest.GetProperty("prefill").GetString());
        input.Enqueue($"{{\"type\":\"extension_ui_response\",\"id\":\"{editorRequest.GetProperty("id").GetString()}\",\"cancelled\":true}}");
        Assert.Null(await editorTask.WaitAsync(TimeSpan.FromSeconds(5)));

        input.Complete();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExtensionUiBridge_FireAndForgetRequestsUseUpstreamRpcShape()
    {
        var output = new JsonLineWriter();
        using var footerDataProvider = new CodingAgentFooterDataProvider(Environment.CurrentDirectory);
        var bridge = new CodingAgentRpcExtensionUiBridge();
        bridge.SetFooterDataProvider(footerDataProvider);
        _ = new CodingAgentRpcHost(
            new FakeCodingAgentRunner((_, _) => EmptyRun()),
            new StringReader(string.Empty),
            output,
            extensionUi: bridge);

        await bridge.SetStatusAsync("build", "running");
        await bridge.NotifyAsync("Heads up", "warning");
        await bridge.SetWidgetAsync("summary", ["line 1", "line 2"], "belowEditor");
        await bridge.NotifyAsync("Plain notice");
        await bridge.SetStatusAsync("idle", null);
        await bridge.SetWidgetAsync("empty", null);
        await bridge.SetTitleAsync("Tau");
        await bridge.SetEditorTextAsync("draft");

        var lines = ReadJsonLines(output);
        var notify = lines.Single(line =>
            line.GetProperty("method").GetString() == "notify" &&
            line.GetProperty("message").GetString() == "Heads up");
        Assert.Equal("Heads up", notify.GetProperty("message").GetString());
        Assert.Equal("warning", notify.GetProperty("notifyType").GetString());

        var plainNotify = lines.Single(line =>
            line.GetProperty("method").GetString() == "notify" &&
            line.GetProperty("message").GetString() == "Plain notice");
        Assert.False(plainNotify.TryGetProperty("notifyType", out _));

        var status = lines.Single(line =>
            line.GetProperty("method").GetString() == "setStatus" &&
            line.GetProperty("statusKey").GetString() == "build");
        Assert.Equal("extension_ui_request", status.GetProperty("type").GetString());
        Assert.Equal("build", status.GetProperty("statusKey").GetString());
        Assert.Equal("running", status.GetProperty("statusText").GetString());

        var idleStatus = lines.Single(line =>
            line.GetProperty("method").GetString() == "setStatus" &&
            line.GetProperty("statusKey").GetString() == "idle");
        Assert.False(idleStatus.TryGetProperty("statusText", out _));
        var extensionStatuses = footerDataProvider.GetExtensionStatuses();
        Assert.Equal("running", extensionStatuses["build"]);
        Assert.False(extensionStatuses.ContainsKey("idle"));

        var widget = lines.Single(line =>
            line.GetProperty("method").GetString() == "setWidget" &&
            line.GetProperty("widgetKey").GetString() == "summary");
        Assert.Equal("summary", widget.GetProperty("widgetKey").GetString());
        Assert.Equal("belowEditor", widget.GetProperty("widgetPlacement").GetString());
        Assert.Equal(
            ["line 1", "line 2"],
            widget.GetProperty("widgetLines").EnumerateArray().Select(line => line.GetString()!).ToArray());

        var emptyWidget = lines.Single(line =>
            line.GetProperty("method").GetString() == "setWidget" &&
            line.GetProperty("widgetKey").GetString() == "empty");
        Assert.False(emptyWidget.TryGetProperty("widgetLines", out _));
        Assert.False(emptyWidget.TryGetProperty("widgetPlacement", out _));

        Assert.Equal("Tau", lines.Single(line => line.GetProperty("method").GetString() == "setTitle").GetProperty("title").GetString());
        Assert.Equal(
            "draft",
            lines.Single(line => line.GetProperty("method").GetString() == "set_editor_text").GetProperty("text").GetString());
    }

    [Fact]
    public async Task ExtensionUiBridge_CancelledBeforeRequestReturnsDefaultsWithoutEmittingRequest()
    {
        var output = new JsonLineWriter();
        var bridge = new CodingAgentRpcExtensionUiBridge();
        _ = new CodingAgentRpcHost(
            new FakeCodingAgentRunner((_, _) => EmptyRun()),
            new StringReader(string.Empty),
            output,
            extensionUi: bridge);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.Null(await bridge.SelectAsync("Pick target", ["alpha"], cancellationToken: cts.Token));
        Assert.False(await bridge.ConfirmAsync("Continue?", "Run the task", cancellationToken: cts.Token));
        Assert.Empty(ReadJsonLines(output));
    }

    [Fact]
    public async Task ExtensionToolUiFireAndForgetActionsEmitRpcRequests()
    {
        Assert.True(IsNodeAvailable(), "node is required for javascript extension runtime tests");
        using var temp = TempDirectory.Create();
        var extensionDirectory = System.IO.Path.Combine(temp.Path, ".tau", "extensions", "ui-tool");
        Directory.CreateDirectory(extensionDirectory);
        WriteJavaScriptExtension(
            extensionDirectory,
            """
            export default function(pi) {
              pi.registerTool({
                name: "ui_tool",
                label: "UI Tool",
                description: "Exercise extension UI actions",
                parameters: { type: "object" },
                execute: async (_toolCallId, _params, _signal, _onUpdate, ctx) => {
                  if (ctx.hasUI) {
                    ctx.ui.notify("Build finished", "warning");
                    ctx.ui.setStatus("build", "done");
                    ctx.ui.setWidget("summary", ["one", "two"], { placement: "belowEditor" });
                    ctx.ui.setTitle("Tau UI");
                    ctx.ui.setEditorText("draft text");
                    ctx.ui.pasteToEditor("pasted text");
                  }
                  return "ui done";
                }
              });
            }
            """);
        var store = new CodingAgentExtensionCommandStore(
            cwd: temp.Path,
            userExtensionsDirectory: System.IO.Path.Combine(temp.Path, "missing-user-extensions"),
            javaScriptRuntime: new CodingAgentJavaScriptExtensionRuntime(temp.Path, nodeExecutable: "node"));
        var output = new JsonLineWriter();
        var bridge = new CodingAgentRpcExtensionUiBridge();
        _ = new CodingAgentRpcHost(
            new FakeCodingAgentRunner((_, _) => EmptyRun()),
            new StringReader(string.Empty),
            output,
            extensionCommandStore: store,
            extensionUi: bridge);
        var tool = Assert.Single(store.LoadTools());
        using var args = JsonDocument.Parse("{}");

        var result = await tool.ExecuteAsync("tool-call-1", args.RootElement);

        Assert.Equal("ui done", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
        var lines = ReadJsonLines(output);
        var notify = lines.Single(line => line.GetProperty("method").GetString() == "notify");
        Assert.Equal("Build finished", notify.GetProperty("message").GetString());
        Assert.Equal("warning", notify.GetProperty("notifyType").GetString());
        var status = lines.Single(line => line.GetProperty("method").GetString() == "setStatus");
        Assert.Equal("build", status.GetProperty("statusKey").GetString());
        Assert.Equal("done", status.GetProperty("statusText").GetString());
        var widget = lines.Single(line => line.GetProperty("method").GetString() == "setWidget");
        Assert.Equal("summary", widget.GetProperty("widgetKey").GetString());
        Assert.Equal("belowEditor", widget.GetProperty("widgetPlacement").GetString());
        Assert.Equal(
            ["one", "two"],
            widget.GetProperty("widgetLines").EnumerateArray().Select(line => line.GetString()!).ToArray());
        Assert.Equal("Tau UI", lines.Single(line => line.GetProperty("method").GetString() == "setTitle").GetProperty("title").GetString());
        Assert.Equal(
            ["draft text", "pasted text"],
            lines
                .Where(line => line.GetProperty("method").GetString() == "set_editor_text")
                .Select(line => line.GetProperty("text").GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task RunAsync_GetAvailableModelsAndSetModelRequireConfiguredAuth()
    {
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.ConfigureAuth("openai");
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"available1\",\"type\":\"get_available_models\"}",
            "{\"id\":\"model1\",\"type\":\"set_model\",\"provider\":\"google\",\"modelId\":\"gemini-2.5-pro\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var availableModels = FindResponse(lines, "get_available_models")
            .GetProperty("data")
            .GetProperty("models")
            .EnumerateArray()
            .Select(model => $"{model.GetProperty("provider").GetString()}/{model.GetProperty("id").GetString()}")
            .ToArray();
        Assert.Equal(["openai/gpt-5.4"], availableModels);

        var model = FindResponse(lines, "set_model");
        Assert.False(model.GetProperty("success").GetBoolean());
        Assert.Contains("configured auth", model.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("openai", runner.Model.Provider);
    }

    [Fact]
    public async Task RunAsync_NewSessionRecordsParentSessionMetadata()
    {
        using var temp = TempDirectory.Create();
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var parentSession = Path.Combine(temp.Path, "parent.jsonl");
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "Old Session"
        };
        runner.MutableMessages.Add(new UserMessage("old prompt"));
        treeController.SyncFromRunner(runner);

        var output = new StringWriter();
        var input = string.Join(
            "\n",
            $"{{\"id\":\"n1\",\"type\":\"new_session\",\"parentSession\":\"{JsonEscaped(parentSession)}\"}}",
            "{\"id\":\"s1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            treeSessionController: treeController);

        await host.RunAsync();

        Assert.Equal(1, runner.ResetSessionCalls);
        Assert.Empty(runner.Messages);
        Assert.Null(runner.SessionName);

        var lines = ReadJsonLines(output);
        var newSession = FindResponse(lines, "new_session");
        Assert.True(newSession.GetProperty("success").GetBoolean());
        Assert.False(newSession.GetProperty("data").GetProperty("cancelled").GetBoolean());

        var state = FindResponse(lines, "get_state").GetProperty("data");
        Assert.Equal(treePath, state.GetProperty("sessionFile").GetString());
        Assert.Equal(0, state.GetProperty("messageCount").GetInt32());

        var summary = treeController.GetSummary();
        Assert.Equal(parentSession, summary.ParentSession);
        Assert.Contains("\"parentSession\"", File.ReadAllText(treePath), StringComparison.Ordinal);
        Assert.Contains("\"action\":\"new\"", File.ReadAllText(treePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NewSessionCanSummarizeCurrentBranchBeforeResetting()
    {
        using var temp = TempDirectory.Create();
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var parentSession = Path.Combine(temp.Path, "parent.jsonl");
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "Current Session"
        };
        runner.MutableMessages.Add(new UserMessage("current prompt"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("current answer")]));
        runner.BranchSummaryHandler = (messages, instructions, replaceInstructions, _) =>
        {
            Assert.Equal("summarize blockers only", instructions);
            Assert.True(replaceInstructions);
            Assert.Equal(2, messages.Count);
            Assert.Equal("current prompt", ReadText(messages[0]));
            Assert.Equal("current answer", ReadText(messages[1]));
            return Task.FromResult(new CodingAgentBranchSummaryResult("new-session summary", messages.Count, 31));
        };
        treeController.SyncFromRunner(runner);

        var output = new StringWriter();
        var input = string.Join(
            "\n",
            $"{{\"id\":\"n1\",\"type\":\"new_session\",\"parentSession\":\"{JsonEscaped(parentSession)}\",\"summarizeCurrentBranch\":true,\"customInstructions\":\"summarize blockers only\",\"replaceInstructions\":true}}",
            "{\"id\":\"s1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            treeSessionController: treeController);

        await host.RunAsync();

        Assert.Equal(1, runner.ResetSessionCalls);
        Assert.Empty(runner.Messages);
        Assert.Null(runner.SessionName);

        var lines = ReadJsonLines(output);
        var newSession = FindResponse(lines, "new_session");
        Assert.True(newSession.GetProperty("success").GetBoolean());
        var data = newSession.GetProperty("data");
        Assert.False(data.GetProperty("cancelled").GetBoolean());
        Assert.True(data.GetProperty("summarizedCurrentBranch").GetBoolean());
        var branchSummary = data.GetProperty("branchSummary");
        Assert.Equal(2, branchSummary.GetProperty("entryCount").GetInt32());
        Assert.Equal(31, branchSummary.GetProperty("tokensBefore").GetInt32());

        var state = FindResponse(lines, "get_state").GetProperty("data");
        Assert.Equal(treePath, state.GetProperty("sessionFile").GetString());
        Assert.Equal(0, state.GetProperty("messageCount").GetInt32());

        var summary = treeController.GetSummary();
        Assert.Equal(parentSession, summary.ParentSession);
        var persisted = File.ReadAllText(treePath);
        Assert.Contains("\"type\":\"branch_summary\"", persisted, StringComparison.Ordinal);
        Assert.Contains("new-session summary", persisted, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"new\"", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NewSessionReturnsCancelledWhenSessionSwitchHookCancels()
    {
        using var temp = TempDirectory.Create();
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "Current Session"
        };
        runner.MutableMessages.Add(new UserMessage("current prompt"));
        treeController.SyncFromRunner(runner);

        CodingAgentSessionSwitchHookState? capturedHookState = null;
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"n1\",\"type\":\"new_session\"}\n"),
            output,
            treeSessionController: treeController,
            sessionSwitchHook: (state, _) =>
            {
                capturedHookState = state;
                return Task.FromResult<CodingAgentSessionSwitchHookResult?>(CodingAgentSessionSwitchHookResult.Cancel());
            });

        await host.RunAsync();

        var response = FindResponse(ReadJsonLines(output), "new_session");
        Assert.True(response.GetProperty("success").GetBoolean());
        var data = response.GetProperty("data");
        Assert.True(data.GetProperty("cancelled").GetBoolean());
        Assert.False(data.GetProperty("summarizedCurrentBranch").GetBoolean());
        Assert.NotNull(capturedHookState);
        Assert.Equal(CodingAgentTreeNavigationReason.NewSession, capturedHookState!.Reason);
        Assert.Equal(Path.GetFullPath(treePath), capturedHookState.CurrentSessionPath);
        Assert.Equal("Current Session", capturedHookState.CurrentSessionName);
        Assert.Equal("openai", capturedHookState.CurrentProvider);
        Assert.Equal("gpt-5.4", capturedHookState.CurrentModel);
        Assert.Null(capturedHookState.TargetSessionPath);
        Assert.Null(capturedHookState.TargetSession);
        Assert.Equal(1, capturedHookState.EntryCount);
        Assert.Equal(0, runner.ResetSessionCalls);
        Assert.Equal("Current Session", runner.SessionName);
        Assert.Single(runner.Messages);
    }

    [Fact]
    public async Task RunAsync_SetAndCycleThinkingLevelPersistSettings()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            TreeFilterMode: "labeled-only",
            RetryMaxAttempts: 4,
            RetryBaseDelayMilliseconds: 300,
            EnabledModels: ["openai/gpt-5.4"]));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"t1\",\"type\":\"set_thinking_level\",\"level\":\"high\"}",
            "{\"id\":\"s1\",\"type\":\"get_state\"}",
            "{\"id\":\"c1\",\"type\":\"cycle_thinking_level\"}",
            "{\"id\":\"c2\",\"type\":\"cycle_thinking_level\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            settingsStore: settingsStore);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        Assert.True(FindResponse(lines, "set_thinking_level").GetProperty("success").GetBoolean());
        Assert.Equal(
            "high",
            FindResponse(lines, "get_state").GetProperty("data").GetProperty("thinkingLevel").GetString());

        var firstCycle = lines
            .Where(line => line.GetProperty("type").GetString() == "response")
            .Where(line => line.GetProperty("command").GetString() == "cycle_thinking_level")
            .First();
        Assert.Equal("xhigh", firstCycle.GetProperty("data").GetProperty("level").GetString());

        var secondCycle = lines
            .Where(line => line.GetProperty("type").GetString() == "response")
            .Where(line => line.GetProperty("command").GetString() == "cycle_thinking_level")
            .Last();
        Assert.Equal(JsonValueKind.Null, secondCycle.GetProperty("data").ValueKind);
        Assert.Null(runner.ThinkingLevel);

        var saved = settingsStore.Load();
        Assert.Equal("openai", saved.DefaultProvider);
        Assert.Equal("gpt-5.4", saved.DefaultModel);
        Assert.Equal("labeled-only", saved.TreeFilterMode);
        Assert.Equal(4, saved.RetryMaxAttempts);
        Assert.Equal(300, saved.RetryBaseDelayMilliseconds);
        Assert.Equal(["openai/gpt-5.4"], saved.EnabledModels);
        Assert.Null(saved.DefaultThinkingLevel);
    }

    [Fact]
    public async Task RunAsync_SetThinkingLevelClampsToCurrentModelCapabilities()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot("google", "gemini-2.5-pro"));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.SelectModel("google", "gemini-2.5-pro");
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"t1\",\"type\":\"set_thinking_level\",\"level\":\"xhigh\"}",
            "{\"id\":\"s1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            settingsStore: settingsStore);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        Assert.True(FindResponse(lines, "set_thinking_level").GetProperty("success").GetBoolean());
        Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);
        Assert.Equal("high", FindResponse(lines, "get_state").GetProperty("data").GetProperty("thinkingLevel").GetString());
        Assert.Equal("high", settingsStore.Load().DefaultThinkingLevel);
    }

    [Fact]
    public async Task RunAsync_SetThinkingLevelTurnsOffForNonReasoningModel()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot("openai", "gpt-5.4", DefaultThinkingLevel: "high"));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.SetModelReasoning("openai", "gpt-5.4", false);
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"t1\",\"type\":\"set_thinking_level\",\"level\":\"high\"}",
            "{\"id\":\"s1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            settingsStore: settingsStore);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        Assert.True(FindResponse(lines, "set_thinking_level").GetProperty("success").GetBoolean());
        Assert.Null(runner.ThinkingLevel);
        Assert.Equal("off", FindResponse(lines, "get_state").GetProperty("data").GetProperty("thinkingLevel").GetString());
        Assert.Null(settingsStore.Load().DefaultThinkingLevel);
    }

    [Fact]
    public async Task RunAsync_ThinkingLevelCommandsValidateInputAndRejectActivePrompt()
    {
        var runner = new FakeCodingAgentRunner((_, ct) => BlockingRun(ct));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"bad1\",\"type\":\"set_thinking_level\",\"level\":\"deep\"}",
            "{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"work\"}",
            "{\"id\":\"t1\",\"type\":\"set_thinking_level\",\"level\":\"low\"}",
            "{\"id\":\"c1\",\"type\":\"cycle_thinking_level\"}",
            "{\"id\":\"a1\",\"type\":\"abort\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var invalid = FindResponseById(lines, "bad1");
        Assert.False(invalid.GetProperty("success").GetBoolean());
        Assert.Contains("Unsupported thinking level", invalid.GetProperty("error").GetString(), StringComparison.Ordinal);

        var setWhileActive = FindResponseById(lines, "t1");
        Assert.False(setWhileActive.GetProperty("success").GetBoolean());
        Assert.Contains("agent is running", setWhileActive.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        var cycleWhileActive = FindResponseById(lines, "c1");
        Assert.False(cycleWhileActive.GetProperty("success").GetBoolean());
        Assert.Contains("agent is running", cycleWhileActive.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(runner.ThinkingLevel);
    }

    [Fact]
    public async Task RunAsync_CycleModelPersistsDefaultModelAndReturnsScopedFlag()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            TreeFilterMode: "user-only",
            RetryMaxAttempts: 3,
            RetryBaseDelayMilliseconds: 250,
            DefaultThinkingLevel: "high",
            EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro"]));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            ThinkingLevel = ThinkingLevel.High
        };
        runner.ConfigureAuth("openai", "google");
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"m1\",\"type\":\"cycle_model\"}",
            "{\"id\":\"s1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            settingsStore: settingsStore);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var cycle = FindResponse(lines, "cycle_model");
        Assert.True(cycle.GetProperty("success").GetBoolean());
        Assert.True(cycle.GetProperty("data").GetProperty("isScoped").GetBoolean());
        Assert.Equal("google", cycle.GetProperty("data").GetProperty("model").GetProperty("provider").GetString());
        Assert.Equal("gemini-2.5-pro", cycle.GetProperty("data").GetProperty("model").GetProperty("id").GetString());
        Assert.Equal("high", cycle.GetProperty("data").GetProperty("thinkingLevel").GetString());
        Assert.Equal("google", runner.Model.Provider);

        var state = FindResponse(lines, "get_state");
        Assert.Equal("google", state.GetProperty("data").GetProperty("model").GetProperty("provider").GetString());
        Assert.Equal("gemini-2.5-pro", state.GetProperty("data").GetProperty("model").GetProperty("id").GetString());

        var saved = settingsStore.Load();
        Assert.Equal("google", saved.DefaultProvider);
        Assert.Equal("gemini-2.5-pro", saved.DefaultModel);
        Assert.Equal("user-only", saved.TreeFilterMode);
        Assert.Equal(3, saved.RetryMaxAttempts);
        Assert.Equal(250, saved.RetryBaseDelayMilliseconds);
        Assert.Equal("high", saved.DefaultThinkingLevel);
        Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro"], saved.EnabledModels);
    }

    [Fact]
    public async Task RunAsync_CycleModelAppliesScopedThinkingLevelOverride()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            DefaultThinkingLevel: "high",
            EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro:off"]));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            ThinkingLevel = ThinkingLevel.High
        };
        runner.ConfigureAuth("openai", "google");
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"m1\",\"type\":\"cycle_model\"}\n"),
            output,
            settingsStore: settingsStore);

        await host.RunAsync();

        var cycle = FindResponse(ReadJsonLines(output), "cycle_model");
        Assert.True(cycle.GetProperty("success").GetBoolean());
        Assert.True(cycle.GetProperty("data").GetProperty("isScoped").GetBoolean());
        Assert.Equal("google", cycle.GetProperty("data").GetProperty("model").GetProperty("provider").GetString());
        Assert.Equal("gemini-2.5-pro", cycle.GetProperty("data").GetProperty("model").GetProperty("id").GetString());
        Assert.Equal("off", cycle.GetProperty("data").GetProperty("thinkingLevel").GetString());
        Assert.Null(runner.ThinkingLevel);

        var saved = settingsStore.Load();
        Assert.Equal("google", saved.DefaultProvider);
        Assert.Equal("gemini-2.5-pro", saved.DefaultModel);
        Assert.Null(saved.DefaultThinkingLevel);
        Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro:off"], saved.EnabledModels);
    }

    [Fact]
    public async Task RunAsync_CycleModelClampsScopedThinkingLevelOverride()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            DefaultThinkingLevel: "low",
            EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro:xhigh"]));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            ThinkingLevel = ThinkingLevel.Low
        };
        runner.ConfigureAuth("openai", "google");
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"m1\",\"type\":\"cycle_model\"}\n"),
            output,
            settingsStore: settingsStore);

        await host.RunAsync();

        var cycle = FindResponse(ReadJsonLines(output), "cycle_model");
        Assert.True(cycle.GetProperty("success").GetBoolean());
        Assert.True(cycle.GetProperty("data").GetProperty("isScoped").GetBoolean());
        Assert.Equal("google", cycle.GetProperty("data").GetProperty("model").GetProperty("provider").GetString());
        Assert.Equal("gemini-2.5-pro", cycle.GetProperty("data").GetProperty("model").GetProperty("id").GetString());
        Assert.Equal("high", cycle.GetProperty("data").GetProperty("thinkingLevel").GetString());
        Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);

        var saved = settingsStore.Load();
        Assert.Equal("google", saved.DefaultProvider);
        Assert.Equal("gemini-2.5-pro", saved.DefaultModel);
        Assert.Equal("high", saved.DefaultThinkingLevel);
        Assert.Equal(["openai/gpt-5.4", "google/gemini-2.5-pro:xhigh"], saved.EnabledModels);
    }

    [Fact]
    public async Task RunAsync_CycleModelReturnsExplicitNullWhenOnlyOneScopedModelIsAvailable()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            EnabledModels: ["openai/gpt-5.4"]));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.ConfigureAuth("openai");
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"m1\",\"type\":\"cycle_model\"}\n"),
            output,
            settingsStore: settingsStore);

        await host.RunAsync();

        var response = FindResponse(ReadJsonLines(output), "cycle_model");
        Assert.True(response.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("data").ValueKind);
        Assert.Equal("openai", runner.Model.Provider);
        Assert.Equal("gpt-5.4", runner.Model.Id);
    }

    [Fact]
    public async Task RunAsync_CycleModelRejectsActivePrompt()
    {
        var runner = new FakeCodingAgentRunner((_, ct) => BlockingRun(ct));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"work\"}",
            "{\"id\":\"m1\",\"type\":\"cycle_model\"}",
            "{\"id\":\"a1\",\"type\":\"abort\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output);

        await host.RunAsync();

        var response = FindResponseById(ReadJsonLines(output), "m1");
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains("agent is running", response.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("openai", runner.Model.Provider);
        Assert.Equal("gpt-5.4", runner.Model.Id);
    }

    [Fact]
    public async Task RunAsync_SetAutoRetryPersistsSettingsAndUpdatesState()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            TreeFilterMode: "user-only",
            RetryMaxAttempts: 4,
            RetryBaseDelayMilliseconds: 125,
            DefaultThinkingLevel: "high",
            EnabledModels: ["openai/gpt-5.4"]));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"r1\",\"type\":\"set_auto_retry\",\"enabled\":true}",
            "{\"id\":\"s1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            settingsStore: settingsStore,
            retryOptions: CodingAgentRetryOptions.Disabled);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        Assert.True(FindResponse(lines, "set_auto_retry").GetProperty("success").GetBoolean());
        Assert.True(FindResponse(lines, "get_state").GetProperty("data").GetProperty("autoRetryEnabled").GetBoolean());

        var saved = settingsStore.Load();
        Assert.Equal("openai", saved.DefaultProvider);
        Assert.Equal("gpt-5.4", saved.DefaultModel);
        Assert.Equal("user-only", saved.TreeFilterMode);
        Assert.Equal(4, saved.RetryMaxAttempts);
        Assert.Equal(125, saved.RetryBaseDelayMilliseconds);
        Assert.Equal("high", saved.DefaultThinkingLevel);
        Assert.Equal(["openai/gpt-5.4"], saved.EnabledModels);
    }

    [Fact]
    public async Task RunAsync_SetAutoRetryFalseDisablesSettings()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            RetryMaxAttempts: 4,
            RetryBaseDelayMilliseconds: 125));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"r1\",\"type\":\"set_auto_retry\",\"enabled\":false}",
            "{\"id\":\"s1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            settingsStore: settingsStore,
            retryOptions: new CodingAgentRetryOptions(4, 125));

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        Assert.True(FindResponse(lines, "set_auto_retry").GetProperty("success").GetBoolean());
        Assert.False(FindResponse(lines, "get_state").GetProperty("data").GetProperty("autoRetryEnabled").GetBoolean());

        var saved = settingsStore.Load();
        Assert.Equal(0, saved.RetryMaxAttempts);
        Assert.Equal(0, saved.RetryBaseDelayMilliseconds);
    }

    [Fact]
    public async Task RunAsync_SetQueueModesPersistsSettingsAndUpdatesState()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            TreeFilterMode: "user-only",
            RetryMaxAttempts: 4,
            RetryBaseDelayMilliseconds: 125,
            DefaultThinkingLevel: "high",
            EnabledModels: ["openai/gpt-5.4"],
            AutoCompactionEnabled: true));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"q1\",\"type\":\"set_steering_mode\",\"mode\":\"all\"}",
            "{\"id\":\"q2\",\"type\":\"set_follow_up_mode\",\"mode\":\"all\"}",
            "{\"id\":\"s1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            settingsStore: settingsStore);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        Assert.True(FindResponse(lines, "set_steering_mode").GetProperty("success").GetBoolean());
        Assert.True(FindResponse(lines, "set_follow_up_mode").GetProperty("success").GetBoolean());
        Assert.Equal(AgentQueueMode.All, runner.SteeringMode);
        Assert.Equal(AgentQueueMode.All, runner.FollowUpMode);

        var state = FindResponse(lines, "get_state").GetProperty("data");
        Assert.Equal("all", state.GetProperty("steeringMode").GetString());
        Assert.Equal("all", state.GetProperty("followUpMode").GetString());
        Assert.True(state.GetProperty("autoCompactionEnabled").GetBoolean());

        var saved = settingsStore.Load();
        Assert.Equal("openai", saved.DefaultProvider);
        Assert.Equal("gpt-5.4", saved.DefaultModel);
        Assert.Equal("user-only", saved.TreeFilterMode);
        Assert.Equal(4, saved.RetryMaxAttempts);
        Assert.Equal(125, saved.RetryBaseDelayMilliseconds);
        Assert.Equal("high", saved.DefaultThinkingLevel);
        Assert.Equal(["openai/gpt-5.4"], saved.EnabledModels);
        Assert.Equal("all", saved.SteeringMode);
        Assert.Equal("all", saved.FollowUpMode);
        Assert.True(saved.AutoCompactionEnabled);
    }

    [Fact]
    public async Task RunAsync_QueueModeCommandsValidateInputAndRejectActivePrompt()
    {
        var runner = new FakeCodingAgentRunner((_, ct) => BlockingRun(ct));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"bad1\",\"type\":\"set_steering_mode\",\"mode\":\"serial\"}",
            "{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"work\"}",
            "{\"id\":\"q1\",\"type\":\"set_steering_mode\",\"mode\":\"all\"}",
            "{\"id\":\"q2\",\"type\":\"set_follow_up_mode\",\"mode\":\"all\"}",
            "{\"id\":\"a1\",\"type\":\"abort\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var invalid = FindResponseById(lines, "bad1");
        Assert.False(invalid.GetProperty("success").GetBoolean());
        Assert.Contains("Unsupported queue mode", invalid.GetProperty("error").GetString(), StringComparison.Ordinal);

        var steeringWhileActive = FindResponseById(lines, "q1");
        Assert.False(steeringWhileActive.GetProperty("success").GetBoolean());
        Assert.Contains("agent is running", steeringWhileActive.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        var followUpWhileActive = FindResponseById(lines, "q2");
        Assert.False(followUpWhileActive.GetProperty("success").GetBoolean());
        Assert.Contains("agent is running", followUpWhileActive.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentQueueMode.OneAtATime, runner.SteeringMode);
        Assert.Equal(AgentQueueMode.OneAtATime, runner.FollowUpMode);
    }

    [Fact]
    public async Task RunAsync_SetAutoCompactionPersistsSettingsAndUpdatesState()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            TreeFilterMode: "user-only",
            RetryMaxAttempts: 4,
            RetryBaseDelayMilliseconds: 125,
            DefaultThinkingLevel: "high",
            EnabledModels: ["openai/gpt-5.4"],
            SteeringMode: "all",
            FollowUpMode: "one-at-a-time",
            AutoCompactionEnabled: false));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"c1\",\"type\":\"set_auto_compaction\",\"enabled\":true}",
            "{\"id\":\"s1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            settingsStore: settingsStore,
            autoCompaction: CodingAgentAutoCompactionOptions.Disabled);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        Assert.True(FindResponse(lines, "set_auto_compaction").GetProperty("success").GetBoolean());
        Assert.True(FindResponse(lines, "get_state").GetProperty("data").GetProperty("autoCompactionEnabled").GetBoolean());

        var saved = settingsStore.Load();
        Assert.Equal("openai", saved.DefaultProvider);
        Assert.Equal("gpt-5.4", saved.DefaultModel);
        Assert.Equal("user-only", saved.TreeFilterMode);
        Assert.Equal(4, saved.RetryMaxAttempts);
        Assert.Equal(125, saved.RetryBaseDelayMilliseconds);
        Assert.Equal("high", saved.DefaultThinkingLevel);
        Assert.Equal(["openai/gpt-5.4"], saved.EnabledModels);
        Assert.Equal("all", saved.SteeringMode);
        Assert.Equal("one-at-a-time", saved.FollowUpMode);
        Assert.True(saved.AutoCompactionEnabled);
    }

    [Fact]
    public async Task RunAsync_SetAutoCompactionRejectsActivePrompt()
    {
        var runner = new FakeCodingAgentRunner((_, ct) => BlockingRun(ct));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"work\"}",
            "{\"id\":\"c1\",\"type\":\"set_auto_compaction\",\"enabled\":false}",
            "{\"id\":\"a1\",\"type\":\"abort\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output);

        await host.RunAsync();

        var response = FindResponseById(ReadJsonLines(output), "c1");
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains("agent is running", response.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_GetSettingsReturnsSettingsSnapshot()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            TreeFilterMode: "no-tools",
            RetryMaxAttempts: 4,
            RetryBaseDelayMilliseconds: 125,
            DefaultThinkingLevel: "high",
            EnabledModels: ["openai/gpt-5.4", "google/gemini-2.5-pro"],
            SteeringMode: "all",
            FollowUpMode: "one-at-a-time",
            AutoCompactionEnabled: true,
            Theme: "reload-theme",
            ShellPath: "C:\\tools\\bash.exe",
            ShellCommandPrefix: "source ~/.bashrc",
            NpmCommand: ["mise", "exec", "node@20", "--", "npm"],
            QuietStartup: true,
            CollapseChangelog: true,
            EnableInstallTelemetry: false,
            LastChangelogVersion: "0.1.0",
            TerminalShowImages: false,
            TerminalClearOnShrink: true,
            ImagesAutoResize: false,
            ImagesBlockImages: true,
            ShowHardwareCursor: true,
            EditorPaddingX: 2,
            AutocompleteMaxVisible: 12,
            MarkdownCodeBlockIndent: "    "));
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            new FakeCodingAgentRunner((_, _) => EmptyRun()),
            new StringReader("{\"id\":\"gs1\",\"type\":\"get_settings\"}\n"),
            output,
            settingsStore: settingsStore);

        await host.RunAsync();

        var data = FindResponse(ReadJsonLines(output), "get_settings").GetProperty("data");
        Assert.Equal(settingsPath, data.GetProperty("path").GetString());
        Assert.Equal("openai", data.GetProperty("defaultProvider").GetString());
        Assert.Equal("gpt-5.4", data.GetProperty("defaultModel").GetString());
        Assert.Equal("no-tools", data.GetProperty("treeFilterMode").GetString());
        Assert.True(data.GetProperty("retry").GetProperty("enabled").GetBoolean());
        Assert.Equal(4, data.GetProperty("retry").GetProperty("maxAttempts").GetInt32());
        Assert.Equal(125, data.GetProperty("retry").GetProperty("baseDelayMilliseconds").GetInt32());
        Assert.Equal("high", data.GetProperty("defaultThinkingLevel").GetString());
        Assert.Equal(
            ["openai/gpt-5.4", "google/gemini-2.5-pro"],
            data.GetProperty("enabledModels").EnumerateArray().Select(model => model.GetString()!).ToArray());
        Assert.Equal("all", data.GetProperty("steeringMode").GetString());
        Assert.Equal("one-at-a-time", data.GetProperty("followUpMode").GetString());
        Assert.True(data.GetProperty("autoCompactionEnabled").GetBoolean());
        Assert.Equal("reload-theme", data.GetProperty("theme").GetString());
        Assert.Equal("C:\\tools\\bash.exe", data.GetProperty("shellPath").GetString());
        Assert.Equal("source ~/.bashrc", data.GetProperty("shellCommandPrefix").GetString());
        Assert.Equal(
            ["mise", "exec", "node@20", "--", "npm"],
            data.GetProperty("npmCommand").EnumerateArray().Select(part => part.GetString()!).ToArray());
        Assert.True(data.GetProperty("quietStartup").GetBoolean());
        Assert.True(data.GetProperty("collapseChangelog").GetBoolean());
        Assert.False(data.GetProperty("enableInstallTelemetry").GetBoolean());
        Assert.Equal("0.1.0", data.GetProperty("lastChangelogVersion").GetString());
        var terminal = data.GetProperty("terminal");
        Assert.False(terminal.GetProperty("showImages").GetBoolean());
        Assert.True(terminal.GetProperty("clearOnShrink").GetBoolean());
        var images = data.GetProperty("images");
        Assert.False(images.GetProperty("autoResize").GetBoolean());
        Assert.True(images.GetProperty("blockImages").GetBoolean());
        Assert.Equal("    ", data.GetProperty("markdown").GetProperty("codeBlockIndent").GetString());
        Assert.True(data.GetProperty("showHardwareCursor").GetBoolean());
        Assert.Equal(2, data.GetProperty("editorPaddingX").GetInt32());
        Assert.Equal(12, data.GetProperty("autocompleteMaxVisible").GetInt32());
    }

    [Fact]
    public async Task RunAsync_UpdateSettingsPersistsAndAppliesRuntimeState()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot(
            "openai",
            "gpt-5.4",
            TreeFilterMode: "default",
            RetryMaxAttempts: 1,
            RetryBaseDelayMilliseconds: 10,
            DefaultThinkingLevel: "low",
            EnabledModels: ["openai/gpt-5.4"],
            SteeringMode: "one-at-a-time",
            FollowUpMode: "one-at-a-time",
            AutoCompactionEnabled: false,
            Theme: "dark"));
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.ConfigureAuth("google");
        var output = new StringWriter();
        var updateJson = string.Join(
            "\n",
            "{\"id\":\"us1\",\"type\":\"update_settings\",\"settings\":{\"model\":{\"provider\":\"google\",\"modelId\":\"gemini-2.5-pro\"},\"treeFilterMode\":\"labeled-only\",\"retry\":{\"enabled\":true,\"maxAttempts\":5,\"baseDelayMilliseconds\":250},\"defaultThinkingLevel\":\"xhigh\",\"enabledModels\":[\"google/gemini-2.5-pro\",\"openai/gpt-5.4\",\"google/gemini-2.5-pro\"],\"steeringMode\":\"all\",\"followUpMode\":\"all\",\"autoCompactionEnabled\":true,\"theme\":\"light\",\"shellPath\":\"C:\\\\tools\\\\bash.exe\",\"shellCommandPrefix\":\"source ~/.bashrc\",\"npmCommand\":[\"mise\",\"exec\",\"node@22\",\"--\",\"npm\",\"npm\"],\"quietStartup\":true,\"collapseChangelog\":true,\"enableInstallTelemetry\":false,\"lastChangelogVersion\":\"0.1.0\",\"terminal\":{\"showImages\":false,\"clearOnShrink\":true},\"images\":{\"autoResize\":false,\"blockImages\":true},\"markdown\":{\"codeBlockIndent\":\"    \"},\"showHardwareCursor\":true,\"editorPaddingX\":8,\"autocompleteMaxVisible\":1}}",
            "{\"id\":\"state1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(updateJson),
            output,
            settingsStore: settingsStore,
            retryOptions: CodingAgentRetryOptions.Disabled,
            autoCompaction: CodingAgentAutoCompactionOptions.Disabled);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var update = FindResponse(lines, "update_settings");
        Assert.True(update.GetProperty("success").GetBoolean());
        Assert.Equal("google", runner.Model.Provider);
        Assert.Equal("gemini-2.5-pro", runner.Model.Id);
        Assert.Equal(ThinkingLevel.High, runner.ThinkingLevel);
        Assert.Equal(AgentQueueMode.All, runner.SteeringMode);
        Assert.Equal(AgentQueueMode.All, runner.FollowUpMode);

        var state = FindResponse(lines, "get_state").GetProperty("data");
        Assert.True(state.GetProperty("autoRetryEnabled").GetBoolean());
        Assert.True(state.GetProperty("autoCompactionEnabled").GetBoolean());
        Assert.Equal("high", state.GetProperty("thinkingLevel").GetString());

        var saved = settingsStore.Load();
        Assert.Equal("google", saved.DefaultProvider);
        Assert.Equal("gemini-2.5-pro", saved.DefaultModel);
        Assert.Equal("labeled-only", saved.TreeFilterMode);
        Assert.Equal(5, saved.RetryMaxAttempts);
        Assert.Equal(250, saved.RetryBaseDelayMilliseconds);
        Assert.Equal("high", saved.DefaultThinkingLevel);
        Assert.Equal(["google/gemini-2.5-pro", "openai/gpt-5.4"], saved.EnabledModels);
        Assert.Equal("all", saved.SteeringMode);
        Assert.Equal("all", saved.FollowUpMode);
        Assert.True(saved.AutoCompactionEnabled);
        Assert.Equal("light", saved.Theme);
        Assert.Equal("C:\\tools\\bash.exe", saved.ShellPath);
        Assert.Equal("source ~/.bashrc", saved.ShellCommandPrefix);
        Assert.Equal(["mise", "exec", "node@22", "--", "npm", "npm"], saved.NpmCommand);
        Assert.True(saved.QuietStartup);
        Assert.True(saved.CollapseChangelog);
        Assert.False(saved.EnableInstallTelemetry);
        Assert.Equal("0.1.0", saved.LastChangelogVersion);
        Assert.False(saved.TerminalShowImages);
        Assert.True(saved.TerminalClearOnShrink);
        Assert.False(saved.ImagesAutoResize);
        Assert.True(saved.ImagesBlockImages);
        Assert.Equal("    ", saved.MarkdownCodeBlockIndent);
        Assert.True(saved.ShowHardwareCursor);
        Assert.Equal(3, saved.EditorPaddingX);
        Assert.Equal(3, saved.AutocompleteMaxVisible);
    }

    [Fact]
    public async Task RunAsync_UpdateSettingsValidatesInputAndRejectsActivePrompt()
    {
        using var temp = TempDirectory.Create();
        var settingsPath = Path.Combine(temp.Path, "settings.json");
        var settingsStore = new CodingAgentSettingsStore(settingsPath);
        settingsStore.Save(new CodingAgentSettingsSnapshot("openai", "gpt-5.4"));
        var runner = new FakeCodingAgentRunner((_, ct) => BlockingRun(ct));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"bad1\",\"type\":\"update_settings\",\"settings\":{\"treeFilterMode\":\"invalid\"}}",
            "{\"id\":\"badTerminal\",\"type\":\"update_settings\",\"settings\":{\"terminal\":false}}",
            "{\"id\":\"badPadding\",\"type\":\"update_settings\",\"settings\":{\"editorPaddingX\":\"wide\"}}",
            "{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"work\"}",
            "{\"id\":\"us1\",\"type\":\"update_settings\",\"settings\":{\"steeringMode\":\"all\"}}",
            "{\"id\":\"a1\",\"type\":\"abort\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output, settingsStore: settingsStore);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var invalid = FindResponseById(lines, "bad1");
        Assert.False(invalid.GetProperty("success").GetBoolean());
        Assert.Contains("Unsupported tree filter mode", invalid.GetProperty("error").GetString(), StringComparison.Ordinal);

        var badTerminal = FindResponseById(lines, "badTerminal");
        Assert.False(badTerminal.GetProperty("success").GetBoolean());
        Assert.Contains("settings.terminal", badTerminal.GetProperty("error").GetString(), StringComparison.Ordinal);

        var badPadding = FindResponseById(lines, "badPadding");
        Assert.False(badPadding.GetProperty("success").GetBoolean());
        Assert.Contains("settings.editorPaddingX", badPadding.GetProperty("error").GetString(), StringComparison.Ordinal);

        var active = FindResponseById(lines, "us1");
        Assert.False(active.GetProperty("success").GetBoolean());
        Assert.Contains("agent is running", active.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        var saved = settingsStore.Load();
        Assert.Null(saved.SteeringMode);
        Assert.Null(saved.TerminalShowImages);
        Assert.Null(saved.EditorPaddingX);
    }

    [Fact]
    public async Task RunAsync_BashReturnsShellResult()
    {
        var shell = new FakeShellRunner
        {
            Handler = (_, _) => Task.FromResult(new CodingAgentShellResult("hello\n", 0, Cancelled: false, Truncated: false))
        };
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"b1\",\"type\":\"bash\",\"command\":\"echo hello\"}\n"),
            output,
            shellRunner: shell);

        await host.RunAsync();

        Assert.Equal(["echo hello"], shell.Commands);
        var response = FindResponse(ReadJsonLines(output), "bash");
        Assert.True(response.GetProperty("success").GetBoolean());
        var data = response.GetProperty("data");
        Assert.Equal("hello\n", data.GetProperty("output").GetString());
        Assert.Equal(0, data.GetProperty("exitCode").GetInt32());
        Assert.False(data.GetProperty("cancelled").GetBoolean());
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_BashStreamsOutputChunksAndTruncationMetadata()
    {
        using var temp = TempDirectory.Create();
        var fullOutputPath = Path.Combine(temp.Path, "full-output.log");
        await File.WriteAllTextAsync(fullOutputPath, "full shell output", CancellationToken.None);

        var shell = new FakeShellRunner
        {
            ProgressHandler = (_, progress, _) =>
            {
                progress?.Report(new CodingAgentShellEvent("stdout", "stdout chunk\n", DateTimeOffset.UtcNow));
                progress?.Report(new CodingAgentShellEvent("stderr", "stderr chunk\n", DateTimeOffset.UtcNow));
                return Task.FromResult(new CodingAgentShellResult("tail chunk\n", 0, Cancelled: false, Truncated: true, fullOutputPath));
            }
        };
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"b1\",\"type\":\"bash\",\"command\":\"echo hello\"}\n"),
            output,
            shellRunner: shell);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var bashOutputs = lines.Where(line => line.GetProperty("type").GetString() == "bash_output").ToArray();
        Assert.Equal(3, bashOutputs.Length);
        Assert.Equal("b1", bashOutputs[0].GetProperty("id").GetString());
        Assert.Equal("b1", bashOutputs[0].GetProperty("requestId").GetString());
        Assert.Equal("stdout", bashOutputs[0].GetProperty("stream").GetString());
        Assert.Equal("stdout chunk\n", bashOutputs[0].GetProperty("text").GetString());
        Assert.Equal("stderr", bashOutputs[1].GetProperty("stream").GetString());
        Assert.Equal("stderr chunk\n", bashOutputs[1].GetProperty("text").GetString());

        var summary = bashOutputs[2];
        Assert.Equal("b1", summary.GetProperty("id").GetString());
        Assert.Equal("b1", summary.GetProperty("requestId").GetString());
        Assert.True(summary.GetProperty("truncated").GetBoolean());
        Assert.Equal(fullOutputPath, summary.GetProperty("fullOutputPath").GetString());
        Assert.False(summary.TryGetProperty("stream", out _));
        Assert.False(summary.TryGetProperty("text", out _));

        Assert.Contains(
            lines,
            line =>
                line.GetProperty("type").GetString() == "bash_event" &&
                line.GetProperty("event").GetString() == "completed");

        var response = FindResponse(lines, "bash");
        Assert.True(response.GetProperty("success").GetBoolean());
        var data = response.GetProperty("data");
        Assert.Equal("tail chunk\n", data.GetProperty("output").GetString());
        Assert.Equal(0, data.GetProperty("exitCode").GetInt32());
        Assert.False(data.GetProperty("cancelled").GetBoolean());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(fullOutputPath, data.GetProperty("fullOutputPath").GetString());
    }

    [Fact]
    public async Task RunAsync_BashRejectsConcurrentCommand()
    {
        var shell = new FakeShellRunner
        {
            Handler = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return new CodingAgentShellResult(string.Empty, null, Cancelled: true, Truncated: false);
            }
        };
        var input = new AsyncLineReader();
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            new FakeCodingAgentRunner((_, _) => EmptyRun()),
            input,
            output,
            shellRunner: shell);

        var runTask = host.RunAsync();
        input.Enqueue("{\"id\":\"b1\",\"type\":\"bash\",\"command\":\"sleep\"}");
        await shell.Started.WaitAsync(TimeSpan.FromSeconds(5));
        input.Enqueue("{\"id\":\"b2\",\"type\":\"bash\",\"command\":\"other\"}");
        input.Enqueue("{\"id\":\"a1\",\"type\":\"abort_bash\"}");
        input.Complete();

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["sleep"], shell.Commands);
        var lines = ReadJsonLines(output);
        var rejected = FindResponseById(lines, "b2");
        Assert.False(rejected.GetProperty("success").GetBoolean());
        Assert.Contains("already running", rejected.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(FindResponseById(lines, "a1").GetProperty("success").GetBoolean());
        var cancelled = FindResponseById(lines, "b1");
        Assert.True(cancelled.GetProperty("success").GetBoolean());
        Assert.True(cancelled.GetProperty("data").GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_AbortBashCancelsActiveShellCommand()
    {
        var shell = new FakeShellRunner
        {
            Handler = async (_, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                    return new CodingAgentShellResult("done", 0, Cancelled: false, Truncated: false);
                }
                catch (OperationCanceledException)
                {
                    return new CodingAgentShellResult("partial", null, Cancelled: true, Truncated: false);
                }
            }
        };
        var input = new AsyncLineReader();
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            new FakeCodingAgentRunner((_, _) => EmptyRun()),
            input,
            output,
            shellRunner: shell);

        var runTask = host.RunAsync();
        input.Enqueue("{\"id\":\"b1\",\"type\":\"bash\",\"command\":\"long\"}");
        await shell.Started.WaitAsync(TimeSpan.FromSeconds(5));
        input.Enqueue("{\"id\":\"a1\",\"type\":\"abort_bash\"}");
        input.Complete();

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, shell.AbortCalls);
        var lines = ReadJsonLines(output);
        Assert.True(FindResponseById(lines, "a1").GetProperty("success").GetBoolean());
        var bash = FindResponseById(lines, "b1");
        Assert.True(bash.GetProperty("success").GetBoolean());
        var data = bash.GetProperty("data");
        Assert.Equal("partial", data.GetProperty("output").GetString());
        Assert.False(data.TryGetProperty("exitCode", out _));
        Assert.True(data.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_BashRequiresCommand()
    {
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            new FakeCodingAgentRunner((_, _) => EmptyRun()),
            new StringReader("{\"id\":\"b1\",\"type\":\"bash\"}\n"),
            output,
            shellRunner: new FakeShellRunner());

        await host.RunAsync();

        var response = FindResponse(ReadJsonLines(output), "bash");
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Contains("command", response.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RetriesRetryablePromptAndRecordsRetryEvents()
    {
        using var temp = TempDirectory.Create();
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var attempts = 0;
        FakeCodingAgentRunner? runnerRef = null;
        var runner = new FakeCodingAgentRunner((input, _) => RetryThenSucceed(runnerRef!, input, ++attempts));
        runnerRef = runner;
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"retry please\"}\n"),
            output,
            treeSessionController: treeController,
            retryOptions: new CodingAgentRetryOptions(2, 0));

        await host.RunAsync();

        Assert.Equal(["retry please", "retry please"], runner.Inputs);
        Assert.Equal(2, runner.Messages.Count);
        Assert.Equal("retry please", ReadText(runner.Messages[0]));
        Assert.Equal("recovered", ReadText(runner.Messages[1]));

        var lines = ReadJsonLines(output);
        Assert.True(FindResponse(lines, "prompt").GetProperty("success").GetBoolean());
        Assert.Contains(lines, line =>
            line.GetProperty("type").GetString() == "auto_retry_start" &&
            line.GetProperty("attempt").GetInt32() == 1);
        Assert.Contains(lines, line =>
            line.GetProperty("type").GetString() == "auto_retry_end" &&
            line.GetProperty("success").GetBoolean());

        var treeJsonl = File.ReadAllText(treePath);
        Assert.Contains("\"type\":\"auto_retry_start\"", treeJsonl, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"auto_retry_end\"", treeJsonl, StringComparison.Ordinal);
        Assert.Contains("retry please", treeJsonl, StringComparison.Ordinal);
        Assert.Contains("recovered", treeJsonl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AbortRetryCancelsPendingRetryDelay()
    {
        using var temp = TempDirectory.Create();
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        FakeCodingAgentRunner? runnerRef = null;
        var runner = new FakeCodingAgentRunner((input, _) => AlwaysRetryableFailure(runnerRef!, input));
        runnerRef = runner;
        var input = new AsyncLineReader();
        var output = new NotifyingStringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            input,
            output,
            treeSessionController: treeController,
            retryOptions: new CodingAgentRetryOptions(2, 30_000));

        var runTask = host.RunAsync();
        input.Enqueue("{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"retry later\"}");

        await output.RetryStarted.WaitAsync(TimeSpan.FromSeconds(5));
        input.Enqueue("{\"id\":\"a1\",\"type\":\"abort_retry\"}");
        input.Complete();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["retry later"], runner.Inputs);
        Assert.Empty(runner.Messages);

        var lines = ReadJsonLines(output);
        Assert.True(FindResponseById(lines, "a1").GetProperty("success").GetBoolean());
        Assert.Contains(lines, line =>
            line.GetProperty("type").GetString() == "auto_retry_end" &&
            !line.GetProperty("success").GetBoolean() &&
            line.GetProperty("finalError").GetString() == "Retry cancelled");

        var treeJsonl = File.ReadAllText(treePath);
        Assert.Contains("\"type\":\"auto_retry_start\"", treeJsonl, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"auto_retry_end\"", treeJsonl, StringComparison.Ordinal);
        Assert.DoesNotContain("retry later", treeJsonl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CompactUsesRunnerAndPersistsSession()
    {
        using var temp = TempDirectory.Create();
        var sessionPath = Path.Combine(temp.Path, "session.json");
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var sessionStore = new CodingAgentSessionStore(sessionPath);
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.MutableMessages.Add(new UserMessage("before"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("answer")]));
        runner.CompactHandler = (_, _) =>
        {
            runner.MutableMessages.Clear();
            runner.MutableMessages.Add(CodingAgentCompactionMessages.CreateSummaryMessage("rpc summary"));
            return Task.FromResult(new CodingAgentCompactionResult("rpc summary", 2, 1, 42));
        };
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"c1\",\"type\":\"compact\",\"customInstructions\":\"keep decisions\"}\n"),
            output,
            sessionStore,
            treeSessionController: treeController);

        await host.RunAsync();

        Assert.Equal("keep decisions", runner.LastCompactInstructions);
        var response = FindResponse(ReadJsonLines(output), "compact");
        Assert.True(response.GetProperty("success").GetBoolean());
        Assert.Equal("rpc summary", response.GetProperty("data").GetProperty("summary").GetString());

        var saved = sessionStore.LoadStrict();
        var summary = Assert.Single(saved.Messages);
        Assert.Contains("rpc summary", ReadText(summary), StringComparison.Ordinal);
        Assert.Contains("\"type\":\"compaction\"", File.ReadAllText(treePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_GetStateReportsCompactionWhileCompactCommandIsRunning()
    {
        using var temp = TempDirectory.Create();
        var sessionPath = Path.Combine(temp.Path, "session.json");
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var sessionStore = new CodingAgentSessionStore(sessionPath);
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var compactionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompaction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.MutableMessages.Add(new UserMessage("before"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("answer")]));
        runner.CompactHandler = async (_, ct) =>
        {
            compactionStarted.TrySetResult();
            await releaseCompaction.Task.WaitAsync(ct).ConfigureAwait(false);
            runner.MutableMessages.Clear();
            runner.MutableMessages.Add(CodingAgentCompactionMessages.CreateSummaryMessage("rpc summary"));
            return new CodingAgentCompactionResult("rpc summary", 2, 1, 42);
        };
        var input = new AsyncLineReader();
        var output = new JsonLineWriter();
        var host = new CodingAgentRpcHost(
            runner,
            input,
            output,
            sessionStore,
            treeSessionController: treeController);
        var hostTask = host.RunAsync();

        input.Enqueue("{\"id\":\"c1\",\"type\":\"compact\",\"customInstructions\":\"keep decisions\"}");
        await compactionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.Enqueue("{\"id\":\"state1\",\"type\":\"get_state\"}");

        var state = await output.WaitForJsonLineAsync(
            line => line.GetProperty("type").GetString() == "response" &&
                    line.GetProperty("id").GetString() == "state1",
            TimeSpan.FromSeconds(5));
        Assert.True(state.GetProperty("data").GetProperty("isCompacting").GetBoolean());

        releaseCompaction.TrySetResult();
        var compact = await output.WaitForJsonLineAsync(
            line => line.GetProperty("type").GetString() == "response" &&
                    line.GetProperty("id").GetString() == "c1",
            TimeSpan.FromSeconds(5));
        Assert.True(compact.GetProperty("success").GetBoolean());
        Assert.Equal("rpc summary", compact.GetProperty("data").GetProperty("summary").GetString());

        input.Complete();
        await hostTask;
    }

    [Fact]
    public async Task RunAsync_ExportHtmlWritesTranscriptAndReturnsPath()
    {
        using var temp = TempDirectory.Create();
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var exportPath = Path.Combine(temp.Path, "transcript.html");
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "RPC Utilities"
        };
        runner.MutableMessages.Add(new UserMessage("export this prompt"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("exported assistant text")]));
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader($"{{\"id\":\"e1\",\"type\":\"export_html\",\"outputPath\":\"{JsonEscaped(exportPath)}\"}}\n"),
            output,
            treeSessionController: treeController);

        await host.RunAsync();

        var response = FindResponse(ReadJsonLines(output), "export_html");
        Assert.True(response.GetProperty("success").GetBoolean());
        Assert.Equal(Path.GetFullPath(exportPath), response.GetProperty("data").GetProperty("path").GetString());
        Assert.True(File.Exists(exportPath));
        var html = File.ReadAllText(exportPath);
        Assert.Contains("RPC Utilities", html, StringComparison.Ordinal);
        Assert.Contains("export this prompt", html, StringComparison.Ordinal);
        Assert.Contains("exported assistant text", html, StringComparison.Ordinal);
        Assert.Contains("Download JSONL", html, StringComparison.Ordinal);
        Assert.Contains("&quot;type&quot;:&quot;message&quot;", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_GetLastAssistantTextReturnsLastAssistantTextOrNull()
    {
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("first answer")]));
        runner.MutableMessages.Add(new UserMessage("next prompt"));
        runner.MutableMessages.Add(new AssistantMessage(
        [
            new TextContent("second answer"),
            new TextContent("details")
        ]));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"t1\",\"type\":\"get_last_assistant_text\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output);

        await host.RunAsync();

        var response = FindResponse(ReadJsonLines(output), "get_last_assistant_text");
        Assert.True(response.GetProperty("success").GetBoolean());
        Assert.Equal(
            "second answer\n\ndetails",
            response.GetProperty("data").GetProperty("text").GetString());

        var emptyOutput = new StringWriter();
        var emptyHost = new CodingAgentRpcHost(
            new FakeCodingAgentRunner((_, _) => EmptyRun()),
            new StringReader("{\"id\":\"t2\",\"type\":\"get_last_assistant_text\"}\n"),
            emptyOutput);

        await emptyHost.RunAsync();

        var emptyResponse = FindResponse(ReadJsonLines(emptyOutput), "get_last_assistant_text");
        Assert.True(emptyResponse.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, emptyResponse.GetProperty("data").GetProperty("text").ValueKind);
    }

    [Fact]
    public async Task RunAsync_SetSessionNamePersistsFlatAndTreeSessionState()
    {
        using var temp = TempDirectory.Create();
        var sessionPath = Path.Combine(temp.Path, "session.json");
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var sessionStore = new CodingAgentSessionStore(sessionPath);
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.MutableMessages.Add(new UserMessage("named prompt"));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"n1\",\"type\":\"set_session_name\",\"name\":\"  RPC Named Session  \"}",
            "{\"id\":\"s1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            sessionStore,
            treeSessionController: treeController);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        Assert.True(FindResponse(lines, "set_session_name").GetProperty("success").GetBoolean());
        Assert.Equal("RPC Named Session", runner.SessionName);
        Assert.Equal("RPC Named Session", sessionStore.LoadStrict().Name);
        Assert.Equal(
            "RPC Named Session",
            FindResponse(lines, "get_state").GetProperty("data").GetProperty("sessionName").GetString());
        Assert.Contains("RPC Named Session", File.ReadAllText(treePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SwitchSessionRestoresJsonlSessionAndPersistsFlatState()
    {
        using var temp = TempDirectory.Create();
        var targetTreePath = Path.Combine(temp.Path, "target.jsonl");
        var currentTreePath = Path.Combine(temp.Path, "current.jsonl");
        var sessionPath = Path.Combine(temp.Path, "session.json");
        var targetRunner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "Target Session"
        };
        targetRunner.MutableMessages.Add(new UserMessage("target prompt"));
        targetRunner.MutableMessages.Add(new AssistantMessage([new TextContent("target answer")]));
        CodingAgentTreeSessionController.OpenOrCreate(targetTreePath).SyncFromRunner(targetRunner);

        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "Current Session"
        };
        runner.MutableMessages.Add(new UserMessage("current prompt"));
        var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentTreePath);
        currentTree.SyncFromRunner(runner);
        var sessionStore = new CodingAgentSessionStore(sessionPath);
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            $"{{\"id\":\"sw1\",\"type\":\"switch_session\",\"sessionPath\":\"{JsonEscaped(targetTreePath)}\"}}",
            "{\"id\":\"state1\",\"type\":\"get_state\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            sessionStore,
            treeSessionController: currentTree);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var switchResponse = FindResponse(lines, "switch_session");
        Assert.True(switchResponse.GetProperty("success").GetBoolean());
        Assert.False(switchResponse.GetProperty("data").GetProperty("cancelled").GetBoolean());
        Assert.Equal(targetTreePath, currentTree.Path);
        Assert.Equal("Target Session", runner.SessionName);
        Assert.Equal(2, runner.Messages.Count);
        Assert.Equal("target prompt", ReadText(runner.Messages[0]));
        Assert.Equal("target answer", ReadText(runner.Messages[1]));

        var state = FindResponse(lines, "get_state").GetProperty("data");
        Assert.Equal(targetTreePath, state.GetProperty("sessionFile").GetString());
        Assert.Equal("Target Session", state.GetProperty("sessionName").GetString());
        Assert.Equal(2, state.GetProperty("messageCount").GetInt32());

        var flatSnapshot = sessionStore.LoadStrict();
        Assert.Equal("Target Session", flatSnapshot.Name);
        Assert.Equal(2, flatSnapshot.Messages.Count);
        Assert.Equal("target prompt", ReadText(flatSnapshot.Messages[0]));
    }

    [Fact]
    public async Task RunAsync_SwitchSessionCanSummarizeCurrentBranchBeforeSwitching()
    {
        using var temp = TempDirectory.Create();
        var targetTreePath = Path.Combine(temp.Path, "target.jsonl");
        var currentTreePath = Path.Combine(temp.Path, "current.jsonl");
        var targetRunner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "Target Session"
        };
        targetRunner.MutableMessages.Add(new UserMessage("target prompt"));
        targetRunner.MutableMessages.Add(new AssistantMessage([new TextContent("target answer")]));
        CodingAgentTreeSessionController.OpenOrCreate(targetTreePath).SyncFromRunner(targetRunner);

        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "Current Session"
        };
        runner.MutableMessages.Add(new UserMessage("current prompt"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("current answer")]));
        runner.BranchSummaryHandler = (messages, instructions, replaceInstructions, _) =>
        {
            Assert.Equal("focus blockers", instructions);
            Assert.True(replaceInstructions);
            Assert.Equal(2, messages.Count);
            Assert.Equal("current prompt", ReadText(messages[0]));
            Assert.Equal("current answer", ReadText(messages[1]));
            return Task.FromResult(new CodingAgentBranchSummaryResult("rpc switch summary", messages.Count, 33));
        };

        var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentTreePath);
        currentTree.SyncFromRunner(runner);
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            $"{{\"id\":\"sw1\",\"type\":\"switch_session\",\"sessionPath\":\"{JsonEscaped(targetTreePath)}\",\"summarizeCurrentBranch\":true,\"customInstructions\":\"focus blockers\",\"replaceInstructions\":true}}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            treeSessionController: currentTree);

        await host.RunAsync();

        var switchResponse = FindResponse(ReadJsonLines(output), "switch_session");
        Assert.True(switchResponse.GetProperty("success").GetBoolean());
        var data = switchResponse.GetProperty("data");
        Assert.False(data.GetProperty("cancelled").GetBoolean());
        Assert.True(data.GetProperty("summarizedCurrentBranch").GetBoolean());
        var branchSummary = data.GetProperty("branchSummary");
        Assert.Equal(2, branchSummary.GetProperty("entryCount").GetInt32());
        Assert.Equal(33, branchSummary.GetProperty("tokensBefore").GetInt32());

        Assert.Equal(targetTreePath, currentTree.Path);
        Assert.Equal("Target Session", runner.SessionName);
        Assert.Equal(2, runner.Messages.Count);
        Assert.Equal("target prompt", ReadText(runner.Messages[0]));
        Assert.Equal("target answer", ReadText(runner.Messages[1]));

        var summarizedCurrent = CodingAgentTreeSessionController.OpenOrCreate(currentTreePath);
        var summarizedSnapshot = summarizedCurrent.LoadSnapshot();
        Assert.Single(summarizedSnapshot.Messages);
        Assert.Contains("rpc switch summary", ReadText(summarizedSnapshot.Messages[0]), StringComparison.Ordinal);
        Assert.Equal("Current Session", summarizedSnapshot.Name);
        Assert.Equal("openai", summarizedSnapshot.Provider);
        Assert.Equal("gpt-5.4", summarizedSnapshot.Model);
    }

    [Fact]
    public async Task RunAsync_SwitchSessionCanUseHookDecisionWithoutCommandFlag()
    {
        using var temp = TempDirectory.Create();
        var targetTreePath = Path.Combine(temp.Path, "target.jsonl");
        var currentTreePath = Path.Combine(temp.Path, "current.jsonl");
        var targetRunner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "Target Session"
        };
        targetRunner.MutableMessages.Add(new UserMessage("target prompt"));
        CodingAgentTreeSessionController.OpenOrCreate(targetTreePath).SyncFromRunner(targetRunner);

        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "Current Session"
        };
        runner.MutableMessages.Add(new UserMessage("current prompt"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("current answer")]));
        runner.BranchSummaryHandler = (messages, instructions, replaceInstructions, _) =>
        {
            Assert.Equal("hook-only focus", instructions);
            Assert.True(replaceInstructions);
            Assert.Equal(2, messages.Count);
            return Task.FromResult(new CodingAgentBranchSummaryResult("hook switch summary", messages.Count, 29));
        };

        var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentTreePath);
        currentTree.SyncFromRunner(runner);
        var output = new StringWriter();
        var input = $"{{\"id\":\"sw1\",\"type\":\"switch_session\",\"sessionPath\":\"{JsonEscaped(targetTreePath)}\"}}\n";
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            treeSessionController: currentTree,
            sessionSwitchHook: (_, _) => Task.FromResult<CodingAgentSessionSwitchHookResult?>(
                CodingAgentSessionSwitchHookResult.Continue(
                    CodingAgentTreeNavigationDecision.SummarizeWith(
                        "hook-only focus",
                        replaceInstructions: true,
                        label: "hook label"))));

        await host.RunAsync();

        var switchResponse = FindResponse(ReadJsonLines(output), "switch_session");
        Assert.True(switchResponse.GetProperty("success").GetBoolean());
        var data = switchResponse.GetProperty("data");
        Assert.False(data.GetProperty("cancelled").GetBoolean());
        Assert.True(data.GetProperty("summarizedCurrentBranch").GetBoolean());
        Assert.Equal(2, data.GetProperty("branchSummary").GetProperty("entryCount").GetInt32());
        Assert.Equal(29, data.GetProperty("branchSummary").GetProperty("tokensBefore").GetInt32());
        Assert.Equal(targetTreePath, currentTree.Path);
        Assert.Equal("Target Session", runner.SessionName);

        var summarizedCurrentJsonl = File.ReadAllText(currentTreePath);
        Assert.Contains("hook switch summary", summarizedCurrentJsonl, StringComparison.Ordinal);
        Assert.Contains("hook label", summarizedCurrentJsonl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SwitchSessionReturnsCancelledWhenSessionSwitchHookCancels()
    {
        using var temp = TempDirectory.Create();
        var targetTreePath = Path.Combine(temp.Path, "target.jsonl");
        var currentTreePath = Path.Combine(temp.Path, "current.jsonl");
        CodingAgentTreeSessionController.OpenOrCreate(targetTreePath).SyncFromRunner(
            new FakeCodingAgentRunner((_, _) => EmptyRun())
            {
                SessionName = "Target Session",
                MutableMessages = { new UserMessage("target prompt") }
            });

        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "Current Session"
        };
        runner.MutableMessages.Add(new UserMessage("current prompt"));
        var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentTreePath);
        currentTree.SyncFromRunner(runner);

        CodingAgentSessionSwitchHookState? capturedHookState = null;
        var output = new StringWriter();
        var input = $"{{\"id\":\"sw1\",\"type\":\"switch_session\",\"sessionPath\":\"{JsonEscaped(targetTreePath)}\"}}\n";
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            treeSessionController: currentTree,
            sessionSwitchHook: (state, _) =>
            {
                capturedHookState = state;
                return Task.FromResult<CodingAgentSessionSwitchHookResult?>(CodingAgentSessionSwitchHookResult.Cancel());
            });

        await host.RunAsync();

        var response = FindResponse(ReadJsonLines(output), "switch_session");
        Assert.True(response.GetProperty("success").GetBoolean());
        var data = response.GetProperty("data");
        Assert.True(data.GetProperty("cancelled").GetBoolean());
        Assert.False(data.GetProperty("summarizedCurrentBranch").GetBoolean());
        Assert.NotNull(capturedHookState);
        Assert.Equal(CodingAgentTreeNavigationReason.ResumeSession, capturedHookState!.Reason);
        Assert.Equal(Path.GetFullPath(currentTreePath), capturedHookState.CurrentSessionPath);
        Assert.Equal("Current Session", capturedHookState.CurrentSessionName);
        Assert.Equal("openai", capturedHookState.CurrentProvider);
        Assert.Equal("gpt-5.4", capturedHookState.CurrentModel);
        Assert.Equal(Path.GetFullPath(targetTreePath), capturedHookState.TargetSessionPath);
        Assert.NotNull(capturedHookState.TargetSession);
        Assert.Equal("Target Session", capturedHookState.TargetSession!.Name);
        Assert.Equal("openai", capturedHookState.TargetSession.Provider);
        Assert.Equal("gpt-5.4", capturedHookState.TargetSession.Model);
        Assert.Equal(1, capturedHookState.TargetSession.MessageCount);
        Assert.Equal(1, capturedHookState.EntryCount);
        Assert.Equal(currentTreePath, currentTree.Path);
        Assert.Equal("Current Session", runner.SessionName);
        Assert.Single(runner.Messages);
        Assert.Equal("current prompt", ReadText(runner.Messages[0]));
    }

    [Fact]
    public async Task RunAsync_SwitchSessionReturnsAlreadyCurrentForCurrentPath()
    {
        using var temp = TempDirectory.Create();
        var currentTreePath = Path.Combine(temp.Path, "current.jsonl");
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun())
        {
            SessionName = "Current Session"
        };
        runner.MutableMessages.Add(new UserMessage("current prompt"));
        var currentTree = CodingAgentTreeSessionController.OpenOrCreate(currentTreePath);
        currentTree.SyncFromRunner(runner);

        var output = new StringWriter();
        var input = $"{{\"id\":\"sw1\",\"type\":\"switch_session\",\"sessionPath\":\"{JsonEscaped(currentTreePath)}\",\"summarizeCurrentBranch\":true}}\n";
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            treeSessionController: currentTree);

        await host.RunAsync();

        var response = FindResponse(ReadJsonLines(output), "switch_session");
        Assert.True(response.GetProperty("success").GetBoolean());
        var data = response.GetProperty("data");
        Assert.False(data.GetProperty("cancelled").GetBoolean());
        Assert.False(data.GetProperty("summarizedCurrentBranch").GetBoolean());
        Assert.True(data.GetProperty("alreadyCurrent").GetBoolean());
        Assert.False(data.TryGetProperty("branchSummary", out _));
        Assert.Equal(currentTreePath, currentTree.Path);
        Assert.Equal("Current Session", runner.SessionName);
        Assert.Single(runner.Messages);
    }

    [Fact]
    public async Task RunAsync_SwitchSessionRejectsActivePrompt()
    {
        using var temp = TempDirectory.Create();
        var targetTreePath = Path.Combine(temp.Path, "target.jsonl");
        CodingAgentTreeSessionController.OpenOrCreate(targetTreePath);
        var runner = new FakeCodingAgentRunner((_, ct) => BlockingRun(ct));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"work\"}",
            $"{{\"id\":\"sw1\",\"type\":\"switch_session\",\"sessionPath\":\"{JsonEscaped(targetTreePath)}\"}}",
            "{\"id\":\"a1\",\"type\":\"abort\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader(input),
            output,
            treeSessionController: CodingAgentTreeSessionController.OpenOrCreate(Path.Combine(temp.Path, "current.jsonl")));

        await host.RunAsync();

        var switchResponse = FindResponseById(ReadJsonLines(output), "sw1");
        Assert.False(switchResponse.GetProperty("success").GetBoolean());
        Assert.Contains("agent is running", switchResponse.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ForkReturnsSelectedUserTextAndRestoresParentBranch()
    {
        using var temp = TempDirectory.Create();
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.MutableMessages.Add(new UserMessage("root prompt"));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("root answer")]));
        runner.MutableMessages.Add(new UserMessage(
        [
            new TextContent("fork "),
            new ImageContent("aGVsbG8=", "image/png"),
            new TextContent("request")
        ]));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("abandoned answer")]));
        treeController.SyncFromRunner(runner);
        var target = treeController
            .GetUserMessagesForForking()
            .Single(message => message.Text == "fork request");
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader($"{{\"id\":\"f1\",\"type\":\"fork\",\"entryId\":\"{target.EntryId}\"}}\n"),
            output,
            treeSessionController: treeController);

        await host.RunAsync();

        var response = FindResponse(ReadJsonLines(output), "fork");
        Assert.True(response.GetProperty("success").GetBoolean());
        var data = response.GetProperty("data");
        Assert.Equal("fork request", data.GetProperty("text").GetString());
        Assert.False(data.GetProperty("cancelled").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("leafId").GetString()));
        Assert.Equal(2, data.GetProperty("messageCount").GetInt32());
        Assert.Equal(runner.Model.Provider, data.GetProperty("provider").GetString());
        Assert.Equal(runner.Model.Id, data.GetProperty("model").GetString());
        Assert.Equal(2, runner.Messages.Count);
        Assert.Equal("root prompt", ReadText(runner.Messages[0]));
        Assert.Equal("root answer", ReadText(runner.Messages[1]));
    }

    [Fact]
    public async Task RunAsync_GetForkMessagesReturnsUserMessageEntryIdsAndText()
    {
        using var temp = TempDirectory.Create();
        var treePath = Path.Combine(temp.Path, "session.jsonl");
        var treeController = CodingAgentTreeSessionController.OpenOrCreate(treePath);
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        runner.MutableMessages.Add(new UserMessage(
        [
            new TextContent("first "),
            new ImageContent("aGVsbG8=", "image/png"),
            new TextContent("prompt")
        ]));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("answer")]));
        runner.MutableMessages.Add(new UserMessage("second prompt"));
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(
            runner,
            new StringReader("{\"id\":\"f1\",\"type\":\"get_fork_messages\"}\n"),
            output,
            treeSessionController: treeController);

        await host.RunAsync();

        var messages = FindResponse(ReadJsonLines(output), "get_fork_messages")
            .GetProperty("data")
            .GetProperty("messages")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, messages.Length);
        Assert.False(string.IsNullOrWhiteSpace(messages[0].GetProperty("entryId").GetString()));
        Assert.Equal("first prompt", messages[0].GetProperty("text").GetString());
        Assert.False(string.IsNullOrWhiteSpace(messages[1].GetProperty("entryId").GetString()));
        Assert.Equal("second prompt", messages[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task RunAsync_ExportHtmlAndSetSessionNameRejectActivePrompt()
    {
        using var temp = TempDirectory.Create();
        var exportPath = Path.Combine(temp.Path, "blocked.html");
        var runner = new FakeCodingAgentRunner((_, ct) => BlockingRun(ct));
        var output = new StringWriter();
        var input = string.Join(
            "\n",
            "{\"id\":\"p1\",\"type\":\"prompt\",\"message\":\"work\"}",
            $"{{\"id\":\"e1\",\"type\":\"export_html\",\"outputPath\":\"{JsonEscaped(exportPath)}\"}}",
            "{\"id\":\"n1\",\"type\":\"set_session_name\",\"name\":\"blocked\"}",
            "{\"id\":\"a1\",\"type\":\"abort\"}",
            string.Empty);
        var host = new CodingAgentRpcHost(runner, new StringReader(input), output);

        await host.RunAsync();

        var lines = ReadJsonLines(output);
        var export = FindResponse(lines, "export_html");
        Assert.False(export.GetProperty("success").GetBoolean());
        Assert.Contains("agent is running", export.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(exportPath));

        var setName = FindResponse(lines, "set_session_name");
        Assert.False(setName.GetProperty("success").GetBoolean());
        Assert.Contains("agent is running", setName.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(runner.SessionName);
    }

    [Fact]
    public async Task RunAsync_InvalidJsonReturnsErrorResponse()
    {
        var runner = new FakeCodingAgentRunner((_, _) => EmptyRun());
        var output = new StringWriter();
        var host = new CodingAgentRpcHost(runner, new StringReader("{not-json}\n"), output);

        await host.RunAsync();

        var response = Assert.Single(ReadJsonLines(output));
        Assert.Equal("response", response.GetProperty("type").GetString());
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Equal("unknown", response.GetProperty("command").GetString());
        Assert.Contains("Invalid JSON", response.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<AgentEvent> RunPrompt(FakeCodingAgentRunner runner, string input)
    {
        runner.MutableMessages.Add(new UserMessage(input));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("rpc ok")]));
        var partial = new AssistantMessage();
        yield return new AgentStartEvent();
        yield return new MessageUpdateEvent(new TextDeltaEvent(0, "rpc ok", partial));
        yield return new AgentEndEvent();
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<AgentEvent> ThrowBeforeFirstEvent()
    {
        await Task.Yield();
        if (ShouldThrowBeforeFirstEvent())
        {
            throw new InvalidOperationException("async preflight failed");
        }

        yield return new AgentStartEvent();
    }

    private static bool ShouldThrowBeforeFirstEvent() => true;

    private static async IAsyncEnumerable<AgentEvent> RunPromptWithAssistantMessageEvents()
    {
        var text = new AssistantMessage([new TextContent("hello")]);
        var thinking = new AssistantMessage([new ThinkingContent("plan")]);
        var tool = new AssistantMessage([
            new ToolCallContent("call_1", "bash", """{"command":"pwd"}""")
            {
                ThoughtSignature = "sig_1"
            }
        ]);
        var done = new AssistantMessage([
            new ToolCallContent("call_1", "bash", """{"command":"pwd"}""")
            {
                ThoughtSignature = "sig_1"
            }
        ])
        {
            StopReason = StopReason.ToolUse
        };

        yield return new AgentStartEvent();
        yield return new MessageUpdateEvent(new TextStartEvent(0, text), text);
        yield return new MessageUpdateEvent(new TextDeltaEvent(0, "he", text), text);
        yield return new MessageUpdateEvent(new TextEndEvent(0, text), text);
        yield return new MessageUpdateEvent(new ThinkingStartEvent(0, thinking), thinking);
        yield return new MessageUpdateEvent(new ThinkingDeltaEvent(0, "pl", thinking), thinking);
        yield return new MessageUpdateEvent(new ThinkingEndEvent(0, thinking), thinking);
        yield return new MessageUpdateEvent(new ToolCallStartEvent(0, tool), tool);
        yield return new MessageUpdateEvent(new ToolCallDeltaEvent(0, """{"command":""", tool), tool);
        yield return new MessageUpdateEvent(new ToolCallEndEvent(0, tool), tool);
        yield return new MessageUpdateEvent(new DoneEvent(done), done);
        yield return new AgentEndEvent(messages: [done]);
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<AgentEvent> BlockingRun(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AgentStartEvent();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<AgentEvent> RunPromptWithQueuedUserMessage(
        Task steeringSeen,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AgentStartEvent();
        await steeringSeen.WaitAsync(cancellationToken).ConfigureAwait(false);
        yield return new MessageStartEvent(new UserMessage("adjust now"));
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<AgentEvent> EmptyRun()
    {
        yield return new AgentEndEvent();
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<AgentEvent> RetryThenSucceed(
        FakeCodingAgentRunner runner,
        string input,
        int attempt)
    {
        yield return new AgentStartEvent();
        if (attempt == 1)
        {
            runner.MutableMessages.Add(new UserMessage(input));
            yield return new AgentEndEvent("429 rate limit");
            yield break;
        }

        runner.MutableMessages.Add(new UserMessage(input));
        runner.MutableMessages.Add(new AssistantMessage([new TextContent("recovered")]));
        yield return new MessageUpdateEvent(new TextDeltaEvent(0, "recovered", new AssistantMessage()));
        yield return new AgentEndEvent();
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<AgentEvent> AlwaysRetryableFailure(
        FakeCodingAgentRunner runner,
        string input)
    {
        yield return new AgentStartEvent();
        runner.MutableMessages.Add(new UserMessage(input));
        yield return new AgentEndEvent("timeout");
        await Task.CompletedTask;
    }

    private static IReadOnlyList<JsonElement> ReadJsonLines(StringWriter output)
    {
        return output
            .ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
    }

    private static JsonElement FindResponse(IReadOnlyList<JsonElement> lines, string command)
    {
        return lines.Single(line =>
            line.GetProperty("type").GetString() == "response" &&
            line.GetProperty("command").GetString() == command);
    }

    private static JsonElement FindResponseById(IReadOnlyList<JsonElement> lines, string id)
    {
        return lines.Single(line =>
            line.GetProperty("type").GetString() == "response" &&
            line.TryGetProperty("id", out var value) &&
            value.GetString() == id);
    }

    private static string JsonEscaped(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void AssertTextAndImage(IReadOnlyList<ContentBlock> blocks, string expectedText)
    {
        Assert.Equal(2, blocks.Count);
        var text = Assert.IsType<TextContent>(blocks[0]);
        Assert.Equal(expectedText, text.Text);
        var image = Assert.IsType<ImageContent>(blocks[1]);
        Assert.Equal("aGVsbG8=", image.Data);
        Assert.Equal("image/png", image.MimeType);
    }

    private static void WriteJavaScriptExtension(string extensionDirectory, string source)
    {
        File.WriteAllText(
            System.IO.Path.Combine(extensionDirectory, "package.json"),
            """
            {
              "type": "module",
              "pi": {
                "extensions": ["index.js"]
              }
            }
            """);
        File.WriteAllText(System.IO.Path.Combine(extensionDirectory, "index.js"), source);
    }

    private static bool IsNodeAvailable()
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("--version");
        try
        {
            process.Start();
            return process.WaitForExit(2000) && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string ReadText(ChatMessage message)
    {
        return message switch
        {
            UserMessage user => string.Join("\n", user.Content.OfType<TextContent>().Select(content => content.Text)),
            AssistantMessage assistant => string.Join("\n", assistant.Content.OfType<TextContent>().Select(content => content.Text)),
            ToolResultMessage tool => string.Join("\n", tool.Content.OfType<TextContent>().Select(content => content.Text)),
            _ => string.Empty
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tau-rpc-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class AsyncLineReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public void Enqueue(string line) => _lines.Writer.TryWrite(line);

        public void Complete() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (await _lines.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_lines.Reader.TryRead(out var line))
                {
                    return line;
                }
            }

            return null;
        }
    }

    private sealed class JsonLineWriter : StringWriter
    {
        private readonly Channel<JsonElement> _lines = Channel.CreateUnbounded<JsonElement>();
        private readonly object _gate = new();

        public async Task<JsonElement> WaitForJsonLineAsync(
            Func<JsonElement, bool> predicate,
            TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (await _lines.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
            {
                while (_lines.Reader.TryRead(out var line))
                {
                    if (predicate(line))
                    {
                        return line;
                    }
                }
            }

            throw new TimeoutException("Timed out waiting for a matching JSON line.");
        }

        public override Task WriteLineAsync(string? value)
        {
            lock (_gate)
            {
                base.WriteLine(value);
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                _lines.Writer.TryWrite(JsonDocument.Parse(value).RootElement.Clone());
            }

            return Task.CompletedTask;
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return base.ToString();
            }
        }
    }

    private sealed class NotifyingStringWriter : StringWriter
    {
        private readonly TaskCompletionSource _retryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RetryStarted => _retryStarted.Task;

        public override Task WriteLineAsync(string? value)
        {
            if (value?.Contains("\"auto_retry_start\"", StringComparison.Ordinal) == true)
            {
                _retryStarted.TrySetResult();
            }

            return base.WriteLineAsync(value);
        }
    }

    private sealed class FakeShellRunner : ICodingAgentShellRunner
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Commands { get; } = [];
        public Task Started => _started.Task;
        public int AbortCalls { get; private set; }
        public Func<string, CancellationToken, Task<CodingAgentShellResult>>? Handler { get; set; }
        public Func<string, IProgress<CodingAgentShellEvent>?, CancellationToken, Task<CodingAgentShellResult>>? ProgressHandler { get; set; }

        public Task<CodingAgentShellResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
            => ExecuteAsync(command, progress: null, cancellationToken);

        public Task<CodingAgentShellResult> ExecuteAsync(
            string command,
            IProgress<CodingAgentShellEvent>? progress,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            _started.TrySetResult();
            if (ProgressHandler is not null)
            {
                return ProgressHandler(command, progress, cancellationToken);
            }

            return Handler is null
                ? Task.FromResult(new CodingAgentShellResult(string.Empty, 0, Cancelled: false, Truncated: false))
                : Handler(command, cancellationToken);
        }

        public void Abort()
        {
            AbortCalls++;
        }
    }
}
