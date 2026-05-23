namespace Tau.Ai.Tests;

internal sealed class EnvironmentVariableScope : IDisposable
{
    private static readonly SemaphoreSlim SyncRoot = new(1, 1);

    private readonly Dictionary<string, string?> _originalValues = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private EnvironmentVariableScope()
    {
        SyncRoot.Wait();
    }

    public static EnvironmentVariableScope Acquire() => new();

    public void Set(string name, string? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_originalValues.ContainsKey(name))
        {
            _originalValues[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var (name, value) in _originalValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _disposed = true;
        SyncRoot.Release();
    }
}
