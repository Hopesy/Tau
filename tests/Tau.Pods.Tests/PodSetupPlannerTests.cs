using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public class PodSetupPlannerTests
{
    [Fact]
    public void Plan_BuildsPlanOnlySetupCommandAndRedactsEnvValues()
    {
        var planner = new PodSetupPlanner(name => name switch
        {
            "HF_TOKEN" => "hf_real_secret",
            "PI_API_KEY" => "pi_real_secret",
            _ => null
        });
        var pod = new PodDefinition
        {
            Id = "ssh-pod",
            Provider = "ssh",
            Model = "llama",
            Region = "lab",
            SshHost = "pods.example.internal",
            SshPort = 2222,
            ModelsPath = "/mnt/models",
            VllmVersion = "nightly"
        };

        var plan = planner.Plan(
            pod,
            new PodSetupPlanOptions(
                MountCommand: "mount -t nfs store:/models /mnt/models",
                ModelsPath: "/data/hf-cache",
                VllmVersion: "gpt-oss"));

        Assert.True(plan.IsPlanOnly);
        Assert.Equal("ssh-pod", plan.PodId);
        Assert.Equal("pods.example.internal", plan.SshHost);
        Assert.Equal(2222, plan.SshPort);
        Assert.Equal("/data/hf-cache", plan.ModelsPath);
        Assert.Equal("mount -t nfs store:/models /mnt/models", plan.MountCommand);
        Assert.Equal("gpt-oss", plan.VllmVersion);
        Assert.True(plan.HfTokenConfigured);
        Assert.True(plan.PiApiKeyConfigured);
        Assert.Equal("/tmp/pod_setup.sh", plan.ScriptRemotePath);
        Assert.Contains("--hf-token \"$HF_TOKEN\"", plan.SetupCommand, StringComparison.Ordinal);
        Assert.Contains("--vllm-api-key \"$PI_API_KEY\"", plan.SetupCommand, StringComparison.Ordinal);
        Assert.Contains("--models-path '/data/hf-cache'", plan.SetupCommand, StringComparison.Ordinal);
        Assert.Contains("--vllm 'gpt-oss'", plan.SetupCommand, StringComparison.Ordinal);
        Assert.Contains("--mount 'mount -t nfs store:/models /mnt/models'", plan.SetupCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("hf_real_secret", plan.SetupCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("pi_real_secret", plan.SetupCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_DefaultsVllmVersionFromPodOrRelease()
    {
        var planner = new PodSetupPlanner(_ => null);
        var pod = new PodDefinition
        {
            Id = "ssh-pod",
            Provider = "ssh",
            Model = "llama",
            Region = "lab",
            SshHost = "pods.example.internal",
            VllmVersion = "nightly"
        };

        var configuredPlan = planner.Plan(pod, new PodSetupPlanOptions());
        pod.VllmVersion = null;
        var defaultPlan = planner.Plan(pod, new PodSetupPlanOptions());

        Assert.Equal("nightly", configuredPlan.VllmVersion);
        Assert.Equal("release", defaultPlan.VllmVersion);
        Assert.Equal("$HOME/.cache/huggingface/hub", defaultPlan.ModelsPath);
    }

    [Fact]
    public void Plan_RejectsInvalidVllmVersion()
    {
        var planner = new PodSetupPlanner(_ => null);
        var pod = new PodDefinition
        {
            Id = "ssh-pod",
            Provider = "ssh",
            Model = "llama",
            Region = "lab",
            SshHost = "pods.example.internal"
        };

        var error = Assert.Throws<ArgumentException>(() =>
            planner.Plan(pod, new PodSetupPlanOptions(VllmVersion: "source")));

        Assert.Contains("Unsupported vLLM version", error.Message, StringComparison.Ordinal);
    }
}
