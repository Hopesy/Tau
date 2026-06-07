using Tau.Pods.Models;
using Tau.Pods.Services;

namespace Tau.Pods.Tests;

public sealed class PodKnownModelRegistryTests
{
    [Fact]
    public void GetKnownModels_LoadsBundledUpstreamModelsJson()
    {
        var registry = new PodKnownModelRegistry();

        var models = registry.GetKnownModels();

        Assert.Contains("openai/gpt-oss-120b", models);
        Assert.True(registry.IsKnownModel("Qwen/Qwen3-Coder-30B-A3B-Instruct-FP8"));
        Assert.Equal("GPT-OSS-120B", registry.GetModelName("openai/gpt-oss-120b"));
    }

    [Fact]
    public void GetBestConfig_PrefersHighestCompatibleGpuCount()
    {
        var registry = new PodKnownModelRegistry();
        var gpus = new[]
        {
            new PodGpuInfo(0, "NVIDIA H100 80GB HBM3", "81559 MiB"),
            new PodGpuInfo(1, "NVIDIA H100 80GB HBM3", "81559 MiB")
        };

        var config = registry.GetBestConfig("openai/gpt-oss-120b", gpus);

        Assert.NotNull(config);
        Assert.Equal(2, config.GpuCount);
        Assert.Contains("--tensor-parallel-size", config.Args);
        Assert.Contains("2", config.Args);
        Assert.Contains("Tools/function calls only via /v1/responses endpoint.", config.Notes);
    }

    [Fact]
    public void GetConfig_FallsBackToGpuCountWhenGpuTypeDoesNotMatch()
    {
        var registry = new PodKnownModelRegistry();
        var gpus = new[] { new PodGpuInfo(0, "NVIDIA RTX 6000 Ada", "49140 MiB") };

        var config = registry.GetConfig("openai/gpt-oss-20b", gpus, requestedGpuCount: 1);

        Assert.NotNull(config);
        Assert.Equal(1, config.GpuCount);
        Assert.Contains("--async-scheduling", config.Args);
    }

    [Fact]
    public void GetConfig_ReturnsEnvironmentForGpuSpecificConfig()
    {
        var registry = new PodKnownModelRegistry();
        var gpus = new[] { new PodGpuInfo(0, "NVIDIA B200", "180000 MiB") };

        var config = registry.GetConfig("openai/gpt-oss-20b", gpus, requestedGpuCount: 1);

        Assert.NotNull(config);
        Assert.NotNull(config.Environment);
        Assert.Equal("1", config.Environment["VLLM_USE_TRTLLM_ATTENTION"]);
        Assert.Equal("1", config.Environment["VLLM_USE_FLASHINFER_MXFP4_MOE"]);
    }

    [Fact]
    public void GetBestConfig_ReturnsNullForUnknownModel()
    {
        var registry = new PodKnownModelRegistry();

        var config = registry.GetBestConfig("org/custom-model", [new PodGpuInfo(0, "NVIDIA H100", "80 GB")]);

        Assert.Null(config);
        Assert.False(registry.IsKnownModel("org/custom-model"));
        Assert.Equal("org/custom-model", registry.GetModelName("org/custom-model"));
    }
}
