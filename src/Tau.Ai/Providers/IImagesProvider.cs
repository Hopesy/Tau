namespace Tau.Ai.Providers;

public interface IImagesProvider
{
    string Api { get; }

    Task<AssistantImages> GenerateImagesAsync(
        ImagesModel model,
        ImagesContext context,
        ImagesOptions options);
}
