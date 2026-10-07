// 作者：xxx
using System.Text.Json;
using Tau.Ai.Registry;

namespace Tau.Ai.Tests;

public sealed class ModelCatalogCapabilitiesTests
{
    /// <summary>【AI】【虚拟覆盖】物理刷新可出现同名模型，虚拟注销及会话复制不会破坏物理基线。</summary>
    [Fact]
    public void VirtualOverlayKeepsPhysicalModelsAcrossRefreshAndCopy()
    {
        var catalog = new ModelCatalog(configurationStore: new ModelConfigurationStore([]));
        var routed = new Model { Id = "same", Provider = "mixed", Name = "Auto", Api = "pi-virtual", BaseUrl = "" };
        catalog.SetVirtualModels([routed]);
        catalog.RegisterModel(CreateModel(ModelTypes.Chat));
        catalog.RegisterModel(CreateModel(ModelTypes.Image));
        Assert.Equal("pi-virtual", catalog.GetModel("mixed", "same").Api);
        Assert.Equal("chat-api", catalog.GetPhysicalModels("mixed").Single(model => ModelTypes.GetModelType(model) == ModelTypes.Chat).Api);
        Assert.Equal(2, catalog.GetAllModels("mixed").Count);
        catalog.ReloadConfiguration();
        var copy = catalog.CreateSessionCopy();
        Assert.Equal("pi-virtual", copy.GetModel("mixed", "same").Api);
        copy.SetVirtualModels([]);
        Assert.Equal("chat-api", copy.GetModel("mixed", "same").Api);
        Assert.Equal("pi-virtual", catalog.GetModel("mixed", "same").Api);
    }

    /// <summary>【AI】【能力目录】混合注册允许相同模型标识，注销与会话复制恢复三种能力。</summary>
    [Fact]
    public void MixedRegistrationAndCopyRestoreEveryCapability()
    {
        var catalog = new ModelCatalog(configurationStore: new ModelConfigurationStore([]));
        catalog.RegisterModel(CreateModel(ModelTypes.Chat));
        catalog.RegisterModel(CreateModel(ModelTypes.Image));
        catalog.RegisterModel(CreateModel(ModelTypes.Classifier));
        using var config = JsonDocument.Parse("""{"models":[{"id":"same"},{"id":"same","type":"image"},{"id":"same","type":"classifier"}],"baseUrl":"http://override.test"}""");
        catalog.SetRuntimeProviders(new Dictionary<string, JsonElement> { ["mixed"] = config.RootElement });
        var copy = catalog.CreateSessionCopy();
        Assert.Equal(3, copy.GetAllModels("mixed").Count);
        Assert.IsType<ImagesModel>(copy.GetModelOfType(ModelTypes.Image, "mixed", "same"));
        Assert.IsType<ClassifierModel>(copy.GetModelOfType(ModelTypes.Classifier, "mixed", "same"));
        Assert.All(copy.GetAllModels("mixed"), model => Assert.Equal("http://override.test", model.BaseUrl));
        copy.SetRuntimeProviders(new Dictionary<string, JsonElement>());
        Assert.All(copy.GetAllModels("mixed"), model => Assert.Equal("http://baseline.test", model.BaseUrl));
        Assert.All(catalog.GetAllModels("mixed"), model => Assert.Equal("http://override.test", model.BaseUrl));
    }

    /// <summary>【AI】【能力目录】覆盖期间登记 SDK 模型，重载或注销后仍保留完整基线。</summary>
    [Fact]
    public void RegistrationDuringOverrideSurvivesReloadAndRemoval()
    {
        var catalog = new ModelCatalog(configurationStore: new ModelConfigurationStore([]));
        using var config = JsonDocument.Parse("""{"models":[{"id":"replacement","api":"custom","baseUrl":"http://override.test"}]}""");
        catalog.SetRuntimeProviders(new Dictionary<string, JsonElement> { ["mixed"] = config.RootElement });
        foreach (var type in new[] { ModelTypes.Chat, ModelTypes.Image, ModelTypes.Classifier }) catalog.RegisterModel(CreateModel(type));
        Assert.Single(catalog.GetAllModels("mixed"));
        catalog.ReloadConfiguration();
        Assert.Single(catalog.GetAllModels("mixed"));
        catalog.SetRuntimeProviders(new Dictionary<string, JsonElement>());
        Assert.Equal(3, catalog.GetAllModels("mixed").Count);
        Assert.Equal("images-api", catalog.GetModelOfType(ModelTypes.Image, "mixed", "same")!.Api);
    }

    /// <summary>【AI】【能力目录】非聊天提供方完整暴露，错误类型及缺少协议不得借用聊天默认值。</summary>
    [Theory]
    [InlineData("image")]
    [InlineData("unsupported")]
    public void InvalidCapabilityRegistrationIsAtomic(string type)
    {
        var catalog = new ModelCatalog(configurationStore: new ModelConfigurationStore([]));
        catalog.RegisterModel(CreateModel(ModelTypes.Chat));
        using var config = JsonDocument.Parse("{\"models\":[{\"id\":\"same\",\"type\":\"" + type + "\"}]}");
        Assert.Throws<ArgumentException>(() => catalog.SetRuntimeProviders(new Dictionary<string, JsonElement> { ["mixed"] = config.RootElement }));
        Assert.Single(catalog.GetAllModels("mixed"));
        Assert.NotEmpty(catalog.GetModelsOfType(ModelTypes.Image));
        Assert.Contains("typesafe", catalog.GetAllProviders());
    }

    /// <summary>【AI】【能力测试】创建相同标识、不同协议的三种模型。</summary>
    /// <param name="type">能力类型。</param><returns>独立模型。</returns>
    private static Model CreateModel(string type) => type switch
    {
        ModelTypes.Image => new ImagesModel { Id = "same", Name = "image", Provider = "mixed", Api = "images-api", BaseUrl = "http://baseline.test" },
        ModelTypes.Classifier => new ClassifierModel { Id = "same", Name = "classifier", Provider = "mixed", Api = "classifier-api", BaseUrl = "http://baseline.test" },
        _ => new Model { Id = "same", Name = "chat", Provider = "mixed", Api = "chat-api", BaseUrl = "http://baseline.test" }
    };
}
