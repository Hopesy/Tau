using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tau.Tui.Runtime;

public static class TuiConsoleRawMode
{
    public static IDisposable Enter()
    {
        if (Console.IsInputRedirected)
            return NullScope.Instance;

        if (OperatingSystem.IsWindows())
            return EnterWindowsRawMode();

        return EnterUnixRawMode();
    }

    private static IDisposable EnterWindowsRawMode()
    {
        var handle = GetStdHandle(StdInputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return NullScope.Instance;

        if (!GetConsoleMode(handle, out var originalMode))
            return NullScope.Instance;

        var rawMode = originalMode;
        rawMode &= ~(EnableEchoInput | EnableLineInput | EnableProcessedInput);
        rawMode |= EnableVirtualTerminalInput;

        if (!SetConsoleMode(handle, rawMode))
            return NullScope.Instance;

        return new RestoreWindowsConsoleMode(handle, originalMode);
    }

    private static IDisposable EnterUnixRawMode()
    {
        var original = RunStty("-g");
        if (string.IsNullOrWhiteSpace(original))
            return NullScope.Instance;

        if (RunStty("-echo -icanon min 1 time 0") is null)
            return NullScope.Instance;

        return new RestoreUnixConsoleMode(original.Trim());
    }

    private static string? RunStty(string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("stty", arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!process.Start())
                return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class RestoreWindowsConsoleMode(IntPtr handle, uint originalMode) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            SetConsoleMode(handle, originalMode);
        }
    }

    private sealed class RestoreUnixConsoleMode(string originalMode) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            RunStty(originalMode);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private const int StdInputHandle = -10;
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableVirtualTerminalInput = 0x0200;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
