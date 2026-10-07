// 作者：xxx
namespace Tau.Ai.Auth.OAuth;

/// <summary>【AI】【OAuth 登录选项】按需取得宿主安装标识，不让普通登录创建持久化设备数据。</summary>
/// <param name="GetDeviceId">返回本安装稳定 UUID 的委托。</param>
public sealed record OAuthLoginOptions(Func<string>? GetDeviceId = null);
