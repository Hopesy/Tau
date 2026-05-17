using System.Text.Json;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public sealed class WebChatStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public WebChatStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public IReadOnlyList<WebChatSessionDto> Load()
    {
        lock (_gate)
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
    }

    public void Save(IReadOnlyList<WebChatSessionDto> sessions)
    {
        lock (_gate)
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new WebChatStoreDocument(sessions);
            var json = JsonSerializer.Serialize(document, WebUiJsonContext.Default.WebChatStoreDocument);
            var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                if (File.Exists(_path))
                {
                    File.Replace(tempPath, _path, null);
                }
                else
                {
                    File.Move(tempPath, _path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
    }
}
