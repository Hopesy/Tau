// 作者：xxx
namespace Tau.CodingAgent.Runtime;

public sealed partial class CodingAgentSettingsStore
{
    /// <summary>【CodingAgent】【安装标识】仅在认证需要时，在全局设置的同一文件锁内读取或生成稳定 UUID。</summary>
    /// <returns>此安装已有或新建的设备标识。</returns>
    public string GetOrCreateDeviceId()
    {
        using var fileLock = CodingAgentSettingsFileLock.Acquire(_path);
        var document = ReadSettingsFile(_path);
        // 1. 【CodingAgent】【安装标识】直接读取全局文件，禁止项目覆盖影响安装身份
        if (document["deviceId"] is { } existing && existing.GetValue<string>() is { Length: > 0 } value) return value;
        var id = Guid.NewGuid().ToString();
        document["deviceId"] = id;
        WriteSettingsFile(_path, document);
        return id;
    }

    /// <summary>【CodingAgent】【安装设置】为没有会话设置注入的认证入口选择用户级文件。</summary>
    /// <returns>安装级设置存储。</returns>
    internal static CodingAgentSettingsStore ForInstallation()
    {
        var configured = Environment.GetEnvironmentVariable("TAU_CODING_AGENT_SETTINGS_FILE");
        if (!string.IsNullOrWhiteSpace(configured)) return new(configured);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(System.IO.Path.Combine(string.IsNullOrWhiteSpace(home) ? Environment.CurrentDirectory : home, ".tau", "coding-agent-settings.json"));
    }
}
