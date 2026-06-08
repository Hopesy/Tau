namespace Tau.WebUi.Services;

public sealed class WebUiOptions
{
    public string SessionsPath { get; set; } = "output/webui-sessions.json";

    public string ArtifactsPath { get; set; } = "output/webui-artifacts.json";
}
