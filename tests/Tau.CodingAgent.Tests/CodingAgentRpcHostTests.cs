using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text.Json;
using Tau.Agent;
using Tau.Agent.Runtime;
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

        var textDelta = lines.Single(line =>
            line.GetProperty("type").GetString() == "message_update" &&
            line.GetProperty("streamEvent").GetProperty("type").GetString() == "text_delta");
        Assert.Equal("rpc ok", textDelta.GetProperty("streamEvent").GetProperty("delta").GetString());

        Assert.Contains(lines, line => line.GetProperty("type").GetString() == "agent_end");
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
        Assert.Contains("compact", commands);
        Assert.Contains("fork", commands);
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
            Theme: "reload-theme"));
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
            "{\"id\":\"us1\",\"type\":\"update_settings\",\"settings\":{\"model\":{\"provider\":\"google\",\"modelId\":\"gemini-2.5-pro\"},\"treeFilterMode\":\"labeled-only\",\"retry\":{\"enabled\":true,\"maxAttempts\":5,\"baseDelayMilliseconds\":250},\"defaultThinkingLevel\":\"xhigh\",\"enabledModels\":[\"google/gemini-2.5-pro\",\"openai/gpt-5.4\",\"google/gemini-2.5-pro\"],\"steeringMode\":\"all\",\"followUpMode\":\"all\",\"autoCompactionEnabled\":true,\"theme\":\"light\"}}",
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

        var active = FindResponseById(lines, "us1");
        Assert.False(active.GetProperty("success").GetBoolean());
        Assert.Contains("agent is running", active.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(settingsStore.Load().SteeringMode);
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

    private static async IAsyncEnumerable<AgentEvent> BlockingRun(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AgentStartEvent();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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

        public Task<CodingAgentShellResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            _started.TrySetResult();
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
