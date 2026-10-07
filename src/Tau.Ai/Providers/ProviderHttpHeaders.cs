// 作者：xxx
using System.Runtime.InteropServices;

namespace Tau.Ai.Providers;

/// <summary>【AI】【HTTP 头部】提供与主线一致的运行环境标识及大小写无关覆盖。</summary>
internal static class ProviderHttpHeaders
{
    /// <summary>【AI】【客户端标识】使用当前平台、系统版本和进程架构生成主线用户代理。</summary>
    /// <returns>用于提供方请求的 User-Agent。</returns>
    internal static string UserAgent()
    {
        var platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture switch { Architecture.X86 => "ia32", _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() };
        return $"pi ({platform} {Environment.OSVersion.Version}; {architecture})";
    }

    /// <summary>【AI】【请求头覆盖】覆盖或删除请求头与内容头，不受字典键大小写影响。</summary>
    /// <param name="request">待发送请求。</param><param name="headers">覆盖值，运行时 null 表示删除。</param>
    internal static void Apply(HttpRequestMessage request, IDictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var (key, value) in headers)
        {
            if (request.Headers.Any(header => header.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) request.Headers.Remove(key);
            if (request.Content?.Headers.Any(header => header.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) == true) request.Content.Headers.Remove(key);
            if (value is not null && !request.Headers.TryAddWithoutValidation(key, value))
                request.Content?.Headers.TryAddWithoutValidation(key, value);
        }
    }
}
