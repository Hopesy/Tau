using System.Text.Json;
using Tau.WebUi.Contracts;

namespace Tau.WebUi.Services;

public sealed class WebArtifactStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public WebArtifactStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public IReadOnlyList<WebArtifactSessionDocument> Load()
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

            var document = JsonSerializer.Deserialize(json, WebUiJsonContext.Default.WebArtifactStoreDocument);
            return document?.Sessions ?? [];
        }
    }

    public void Save(IReadOnlyList<WebArtifactSessionDocument> sessions)
    {
        lock (_gate)
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new WebArtifactStoreDocument(sessions);
            var json = JsonSerializer.Serialize(document, WebUiJsonContext.Default.WebArtifactStoreDocument);
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

internal sealed record WebArtifactStoreDocument(IReadOnlyList<WebArtifactSessionDocument> Sessions);

public sealed record WebArtifactSessionDocument(
    string SessionId,
    IReadOnlyList<WebArtifactDto> Artifacts);
