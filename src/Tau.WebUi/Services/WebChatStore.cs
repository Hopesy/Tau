using System.Text.Json;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public sealed class WebChatStore
{
    private readonly string _path;

    public WebChatStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public IReadOnlyList<WebChatSessionDto> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var json = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var document = JsonSerializer.Deserialize(json, WebUiJsonContext.Default.WebChatStoreDocument);
        return document?.Sessions ?? [];
    }

    public void Save(IReadOnlyList<WebChatSessionDto> sessions)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new WebChatStoreDocument(sessions);
        var json = JsonSerializer.Serialize(document, WebUiJsonContext.Default.WebChatStoreDocument);
        File.WriteAllText(_path, json);
    }
}
