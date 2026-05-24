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
        Assert.Contains("CUDA_VISIBLE_DEVICES='0'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("bad_key='value with spaces'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("vllm serve '/srv/hf cache/models--meta-llama--Llama-3.1-8B-Instruct'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("--port 8081", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("--served-model-name 'llama-8b'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("'--tensor-parallel-size' '2'", plan.ServeCommand, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/usr/bin/env bash -lc", plan.SystemdUnit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target", plan.SystemdUnit, StringComparison.Ordinal);
        Assert.Contains("cat > ~/.tau_pods/llama-8b--rm.service <<'EOF'", plan.RemoteCommand, StringComparison.Ordinal);
        Assert.Contains("planned llama-8b--rm", plan.RemoteCommand, StringComparison.Ordinal);

        using var metadata = JsonDocument.Parse(plan.MetadataJson);
        Assert.Equal("planned-vllm", metadata.RootElement.GetProperty("status").GetString());
        Assert.Equal("/srv/hf cache/models--meta-llama--Llama-3.1-8B-Instruct", metadata.RootElement.GetProperty("modelPath").GetString());
        Assert.Equal(8081, metadata.RootElement.GetProperty("port").GetInt32());
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
        Assert.Contains("planned org-model", plan.RemoteCommand, StringComparison.Ordinal);
    }
}
