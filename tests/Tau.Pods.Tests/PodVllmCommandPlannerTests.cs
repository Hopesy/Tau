using System.Text.Json;
using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public sealed class PodVllmCommandPlannerTests
{
    [Fact]
    public void PlanServe_BuildsQuotedVllmServeCommandAndMetadata()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            ModelsPath = "/srv/hf cache"
        };

        var plan = planner.PlanServe(
            pod,
            new PodVllmServeOptions(
                "meta-llama/Llama-3.1-8B-Instruct",
                DeploymentName: "llama 8b; rm",
                Port: 8081,
                ServedModelName: "llama-8b",
                Environment: new Dictionary<string, string>
                {
                    ["CUDA_VISIBLE_DEVICES"] = "0",
                    ["bad-key"] = "value with spaces"
                },
                ExtraArgs: ["--tensor-parallel-size", "2"]));

        Assert.Equal("llama-8b--rm", plan.DeploymentName);
        Assert.Equal("/srv/hf cache/models--meta-llama--Llama-3.1-8B-Instruct", plan.ModelPath);
        Assert.Equal("tau-pod-llama-8b--rm.service", plan.UnitName);
        Assert.Equal("~/.vllm_logs/llama-8b--rm.log", plan.LogPath);
        Assert.Equal("~/.tau_pods/model_run_llama-8b--rm.sh", plan.RunnerScriptPath);
        Assert.Equal("~/.tau_pods/model_wrapper_llama-8b--rm.sh", plan.WrapperScriptPath);
        Assert.True(plan.UsesPseudoTtyWrapper);
        Assert.Contains("CUDA_VISIBLE_DEVICES='0'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("bad_key='value with spaces'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("model_cache_path='/srv/hf cache/models--meta-llama--Llama-3.1-8B-Instruct'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("vllm serve \"$resolved_model_path\"", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("--port 8081", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("--api-key \"$PI_API_KEY\"", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("--served-model-name 'llama-8b'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("'--tensor-parallel-size' '2'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("MODEL_ID='meta-llama/Llama-3.1-8B-Instruct'", plan.RunnerScript, StringComparison.Ordinal);
        Assert.Contains("VLLM_ARGS='--tensor-parallel-size 2'", plan.RunnerScript, StringComparison.Ordinal);
        Assert.Contains("VLLM_CMD=", plan.RunnerScript, StringComparison.Ordinal);
        Assert.Contains("HF_HUB_ENABLE_HF_TRANSFER=1 hf download \"$MODEL_ID\"", plan.RunnerScript, StringComparison.Ordinal);
        Assert.Contains("ERROR: Failed to download model", plan.RunnerScript, StringComparison.Ordinal);
        Assert.Contains("Model download complete", plan.RunnerScript, StringComparison.Ordinal);
        Assert.Contains("Model runner exiting with code", plan.RunnerScript, StringComparison.Ordinal);
        Assert.Contains("bash -c \"$VLLM_CMD\"", plan.RunnerScript, StringComparison.Ordinal);
        Assert.Contains("script -q -f -c \"$HOME/.tau_pods/model_run_llama-8b--rm.sh\" \"$HOME/.vllm_logs/llama-8b--rm.log\"", plan.WrapperScript, StringComparison.Ordinal);
        Assert.Contains("Script exited with code $exit_code", plan.WrapperScript, StringComparison.Ordinal);
        Assert.Contains("ExecStartPre=/usr/bin/env mkdir -p %h/.vllm_logs", plan.SystemdUnit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/usr/bin/env bash -lc 'exec ~/.tau_pods/model_wrapper_llama-8b--rm.sh >/dev/null 2>&1'", plan.SystemdUnit, StringComparison.Ordinal);
        Assert.Contains("StandardOutput=null", plan.SystemdUnit, StringComparison.Ordinal);
        Assert.Contains("StandardError=null", plan.SystemdUnit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target", plan.SystemdUnit, StringComparison.Ordinal);
        Assert.Contains("cat > ~/.tau_pods/model_run_llama-8b--rm.sh <<'EOF'", plan.RemoteCommand, StringComparison.Ordinal);
        Assert.Contains("cat > ~/.tau_pods/model_wrapper_llama-8b--rm.sh <<'EOF'", plan.RemoteCommand, StringComparison.Ordinal);
        Assert.Contains("cat > ~/.tau_pods/llama-8b--rm.service <<'EOF'", plan.RemoteCommand, StringComparison.Ordinal);
        Assert.Contains("planned llama-8b--rm", plan.RemoteCommand, StringComparison.Ordinal);

        using var metadata = JsonDocument.Parse(plan.MetadataJson);
        Assert.Equal("planned-vllm", metadata.RootElement.GetProperty("status").GetString());
        Assert.Equal("/srv/hf cache/models--meta-llama--Llama-3.1-8B-Instruct", metadata.RootElement.GetProperty("modelPath").GetString());
        Assert.Equal(8081, metadata.RootElement.GetProperty("port").GetInt32());
        Assert.Equal("~/.vllm_logs/llama-8b--rm.log", metadata.RootElement.GetProperty("logPath").GetString());
        Assert.Equal("~/.tau_pods/model_run_llama-8b--rm.sh", metadata.RootElement.GetProperty("runnerScriptPath").GetString());
        Assert.Equal("~/.tau_pods/model_wrapper_llama-8b--rm.sh", metadata.RootElement.GetProperty("wrapperScriptPath").GetString());
        Assert.True(metadata.RootElement.GetProperty("usesPseudoTtyWrapper").GetBoolean());
    }

    [Fact]
    public void PlanServe_DefaultsPortServedNameAndModelsPath()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var plan = planner.PlanServe(pod, new PodVllmServeOptions("org/model"));

        Assert.Equal("org-model", plan.DeploymentName);
        Assert.Equal(8000, plan.Port);
        Assert.Equal("org-model", plan.ServedModelName);
        Assert.Equal("$HOME/.cache/huggingface/hub/models--org--model", plan.ModelPath);
    }

    [Fact]
    public void PlanServe_ShellQuotesSingleQuotesInsideExtraArgs()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var plan = planner.PlanServe(
            pod,
            new PodVllmServeOptions(
                "org/model",
                ExtraArgs: ["--chat-template", "it's fine"]));

        Assert.Contains("'it'\"'\"'s fine'", plan.ServeCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanServe_WithRevision_BuildsRevisionAwareDiscoveryCommand()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com", ModelsPath = "/models/hf" };

        var plan = planner.PlanServe(pod, new PodVllmServeOptions("org/model", Revision: "rev-b"));

        Assert.Equal("rev-b", plan.Revision);
        Assert.True(plan.UsesSnapshotDiscovery);
        Assert.Contains("requested_revision='rev-b'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("ref_file=\"$model_cache_path/refs/$requested_revision\"", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("snapshots/$requested_revision", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("exit 16", plan.ServeCommand, StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(plan.MetadataJson);
        Assert.Equal("rev-b", metadata.RootElement.GetProperty("revision").GetString());
    }

    [Fact]
    public void PlanServe_UsesKnownModelConfigWhenExtraArgsAreNotExplicit()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            Gpus = [new PodGpuInfo(0, "NVIDIA B200", "180000 MiB")]
        };

        var plan = planner.PlanServe(pod, new PodVllmServeOptions("openai/gpt-oss-20b"));

        Assert.Equal("GPT-OSS-20B", plan.KnownModelName);
        Assert.Equal(1, plan.KnownModelGpuCount);
        Assert.Contains("--async-scheduling", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("VLLM_USE_TRTLLM_ATTENTION='1'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.NotNull(plan.KnownModelEnvironment);
        Assert.Equal("1", plan.KnownModelEnvironment["VLLM_USE_FLASHINFER_MXFP4_MOE"]);
        using var metadata = JsonDocument.Parse(plan.MetadataJson);
        var knownModel = metadata.RootElement.GetProperty("knownModel");
        Assert.Equal("GPT-OSS-20B", knownModel.GetProperty("name").GetString());
        Assert.Equal(1, knownModel.GetProperty("gpuCount").GetInt32());
        Assert.Equal("1", knownModel.GetProperty("env").GetProperty("VLLM_USE_TRTLLM_ATTENTION").GetString());
        Assert.Contains("CUDA_VISIBLE_DEVICES='0'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Equal(new[] { 0 }, plan.SelectedGpuIds!);
    }

    [Fact]
    public void PlanServe_WithKnownModelGpuMemoryAndContextOptions_AppliesPlannerOverrides()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            Gpus =
            [
                new PodGpuInfo(0, "NVIDIA H100", "80000 MiB"),
                new PodGpuInfo(1, "NVIDIA H100", "80000 MiB"),
                new PodGpuInfo(2, "NVIDIA H100", "80000 MiB")
            ]
        };

        var plan = planner.PlanServe(
            pod,
            new PodVllmServeOptions(
                "openai/gpt-oss-120b",
                RequestedGpuCount: 2,
                Memory: "50%",
                Context: "32k"));

        Assert.Equal("GPT-OSS-120B", plan.KnownModelName);
        Assert.Equal(2, plan.KnownModelGpuCount);
        Assert.Equal(2, plan.RequestedGpuCount);
        Assert.Equal(new[] { 0, 1 }, plan.SelectedGpuIds!);
        Assert.Equal("50%", plan.MemoryOverride);
        Assert.Equal(0.5d, plan.MemoryUtilization);
        Assert.Equal("32k", plan.ContextOverride);
        Assert.Equal(32768, plan.ContextTokens);
        Assert.Contains("'--tensor-parallel-size' '2'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("'--gpu-memory-utilization' '0.5'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("'--max-model-len' '32768'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("'0.94'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("CUDA_VISIBLE_DEVICES", plan.ServeCommand, StringComparison.Ordinal);

        using var metadata = JsonDocument.Parse(plan.MetadataJson);
        Assert.Equal(2, metadata.RootElement.GetProperty("requestedGpuCount").GetInt32());
        Assert.Equal(2, metadata.RootElement.GetProperty("selectedGpus").GetArrayLength());
        Assert.Equal(0.5d, metadata.RootElement.GetProperty("memoryUtilization").GetDouble());
        Assert.Equal(32768, metadata.RootElement.GetProperty("contextTokens").GetInt32());
    }

    [Fact]
    public void PlanServe_UsesLeastUsedGpuFromConfiguredModelState()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            Gpus =
            [
                new PodGpuInfo(7, "NVIDIA H100", "80000 MiB"),
                new PodGpuInfo(8, "NVIDIA H100", "80000 MiB"),
                new PodGpuInfo(9, "NVIDIA H100", "80000 MiB")
            ],
            Models =
            {
                ["alpha"] = new PodConfiguredModel { Model = "org/alpha", Port = 8001, Gpu = [7], Pid = 101 },
                ["beta"] = new PodConfiguredModel { Model = "org/beta", Port = 8002, Gpu = [7], Pid = 102 },
                ["gamma"] = new PodConfiguredModel { Model = "org/gamma", Port = 8003, Gpu = [8], Pid = 103 }
            }
        };

        var plan = planner.PlanServe(pod, new PodVllmServeOptions("org/model"));

        Assert.Equal(new[] { 9 }, plan.SelectedGpuIds!);
        Assert.Contains("CUDA_VISIBLE_DEVICES='9'", plan.ServeCommand, StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(plan.MetadataJson);
        var selected = metadata.RootElement.GetProperty("selectedGpus");
        Assert.Equal(1, selected.GetArrayLength());
        Assert.Equal(9, selected[0].GetInt32());
    }

    [Fact]
    public void PlanServe_KnownModelGpuCountUsesLeastUsedGpuSet()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            Gpus =
            [
                new PodGpuInfo(0, "NVIDIA H100", "80000 MiB"),
                new PodGpuInfo(1, "NVIDIA H100", "80000 MiB"),
                new PodGpuInfo(2, "NVIDIA H100", "80000 MiB")
            ],
            Models =
            {
                ["alpha"] = new PodConfiguredModel { Model = "org/alpha", Port = 8001, Gpu = [0], Pid = 101 },
                ["beta"] = new PodConfiguredModel { Model = "org/beta", Port = 8002, Gpu = [0], Pid = 102 }
            }
        };

        var plan = planner.PlanServe(
            pod,
            new PodVllmServeOptions("openai/gpt-oss-120b", RequestedGpuCount: 2));

        Assert.Equal(new[] { 1, 2 }, plan.SelectedGpuIds!);
        Assert.Equal(2, plan.RequestedGpuCount);
        Assert.DoesNotContain("CUDA_VISIBLE_DEVICES", plan.ServeCommand, StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(plan.MetadataJson);
        var selected = metadata.RootElement.GetProperty("selectedGpus");
        Assert.Equal(1, selected[0].GetInt32());
        Assert.Equal(2, selected[1].GetInt32());
    }

    [Fact]
    public void PlanServe_UnknownModelWithGpuCountThrows()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            Gpus = [new PodGpuInfo(0, "NVIDIA H100", "80000 MiB")]
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            planner.PlanServe(pod, new PodVllmServeOptions("org/model", RequestedGpuCount: 1)));

        Assert.Contains("--gpus can only be used with predefined models", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanServe_KnownModelWithTooManyRequestedGpusThrows()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            Gpus = [new PodGpuInfo(0, "NVIDIA H100", "80000 MiB")]
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            planner.PlanServe(pod, new PodVllmServeOptions("openai/gpt-oss-120b", RequestedGpuCount: 2)));

        Assert.Contains("Requested 2 GPUs but pod only has 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanServe_UnknownModelDefaultsToSingleGpuWhenPodHasGpuInventory()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            Gpus =
            [
                new PodGpuInfo(7, "NVIDIA H100", "80000 MiB"),
                new PodGpuInfo(8, "NVIDIA H100", "80000 MiB")
            ]
        };

        var plan = planner.PlanServe(pod, new PodVllmServeOptions("org/model"));

        Assert.Equal(new[] { 7 }, plan.SelectedGpuIds!);
        Assert.Contains("CUDA_VISIBLE_DEVICES='7'", plan.ServeCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanServe_ExplicitVllmArgsOverrideKnownModelConfig()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition
        {
            Id = "gpu-1",
            SshHost = "host.example.com",
            Gpus = [new PodGpuInfo(0, "NVIDIA B200", "180000 MiB")]
        };

        var plan = planner.PlanServe(
            pod,
            new PodVllmServeOptions(
                "openai/gpt-oss-20b",
                ExtraArgs: ["--manual-arg"],
                RequestedGpuCount: 1,
                Memory: "50%",
                Context: "32k"));

        Assert.Null(plan.KnownModelName);
        Assert.Null(plan.SelectedGpuIds);
        Assert.Null(plan.RequestedGpuCount);
        Assert.Null(plan.MemoryOverride);
        Assert.Null(plan.ContextOverride);
        Assert.Contains("'--manual-arg'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("VLLM_USE_TRTLLM_ATTENTION", plan.ServeCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("CUDA_VISIBLE_DEVICES", plan.ServeCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("gpu-memory-utilization", plan.ServeCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("max-model-len", plan.ServeCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanServe_NormalizesEnvironmentKeysToShellAssignments()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var plan = planner.PlanServe(
            pod,
            new PodVllmServeOptions(
                "org/model",
                Environment: new Dictionary<string, string>
                {
                    ["1BAD"] = "digit",
                    ["***"] = "empty"
                }));

        Assert.Contains("ENV_1BAD='digit'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("ENV='empty'", plan.ServeCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanServe_RemoteCommandIsPlanOnly()
    {
        var planner = new PodVllmCommandPlanner();
        var pod = new PodDefinition { Id = "gpu-1", SshHost = "host.example.com" };

        var plan = planner.PlanServe(pod, new PodVllmServeOptions("org/model"));

        Assert.DoesNotContain("ssh ", plan.RemoteCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl start", plan.RemoteCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl enable", plan.RemoteCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cat > ~/.tau_pods/model_run_org-model.sh <<'EOF'", plan.RemoteCommand, StringComparison.Ordinal);
        Assert.Contains("cat > ~/.tau_pods/model_wrapper_org-model.sh <<'EOF'", plan.RemoteCommand, StringComparison.Ordinal);
        Assert.Contains("planned org-model", plan.RemoteCommand, StringComparison.Ordinal);
    }
}
