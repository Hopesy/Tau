namespace Tau.AgentCore.Tests;

internal sealed class EnvironmentVariableScope : IDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);
    private bool _disposed;

    private EnvironmentVariableScope()
    {
        Gate.Wait();
    }

    public static EnvironmentVariableScope Acquire()
    {
        return new EnvironmentVariableScope();
    }

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

        foreach (var pair in _originalValues)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        _disposed = true;
        Gate.Release();
    }
}
