namespace Tau.Tui.Runtime;

public interface IInputHistoryStore
{
    IEnumerable<string> Load();
    void Append(string entry);
}

public sealed class FileInputHistoryStore : IInputHistoryStore
{
    private readonly string _path;
    private readonly int _maxEntries;

    public FileInputHistoryStore(string path, int maxEntries = 5000)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("History file path must be non-empty.", nameof(path));
        }

        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        _path = path;
        _maxEntries = maxEntries;
    }

    public string Path => _path;

    public IEnumerable<string> Load()
    {
        if (!File.Exists(_path))
        {
            yield break;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(_path);
        }
        catch
        {
            // Best-effort: history is non-essential, swallow read errors so the
            // editor still works on a fresh process.
            yield break;
        }

        var start = lines.Length > _maxEntries ? lines.Length - _maxEntries : 0;
        for (var i = start; i < lines.Length; i++)
        {
            var entry = lines[i];
            if (!string.IsNullOrEmpty(entry))
            {
                yield return entry;
            }
        }
    }

    public void Append(string entry)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch
            {
                // ignore; the AppendAllLines below will surface real errors
            }
        }

        try
        {
            File.AppendAllText(_path, entry + Environment.NewLine);
            TrimIfNeeded();
        }
        catch
        {
            // Best-effort persistence; don't crash the editor if the history
            // file isn't writable.
        }
    }

    private void TrimIfNeeded()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var lines = File.ReadAllLines(_path);
            if (lines.Length <= _maxEntries)
            {
                return;
            }

            var truncated = lines[(lines.Length - _maxEntries)..];
            File.WriteAllLines(_path, truncated);
        }
        catch
        {
            // ignore
        }
    }
}
