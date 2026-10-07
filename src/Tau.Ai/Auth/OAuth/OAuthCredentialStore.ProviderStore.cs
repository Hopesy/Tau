// 作者：xxx
using System.Text.Json;

namespace Tau.Ai.Auth.OAuth;

public sealed partial class OAuthCredentialStore
{
    /// <summary>【AI】【统一文件凭据】严格读取指定提供方，损坏文件交由调用方报告，不静默当作未登录。</summary>
    /// <param name="providerId">提供方标识。</param><param name="cancellationToken">取消信号。</param><returns>完整凭据或空值。</returns>
    public Task<ProviderCredential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = _searchPaths.FirstOrDefault(File.Exists);
        var entries = path is null ? null : ReadCredentialDocument(path);
        var credential = entries is not null && entries.TryGetValue(providerId, out var entry) ? ParseProviderCredential(entry) : null;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(credential);
    }

    /// <summary>【AI】【凭据元数据】只向调用方返回可识别凭据的提供方与类型，不返回密钥内容。</summary>
    /// <param name="cancellationToken">取消信号。</param><returns>已存凭据元数据。</returns>
    public Task<IReadOnlyList<ProviderCredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = _searchPaths.FirstOrDefault(File.Exists);
        IReadOnlyList<ProviderCredentialInfo> result = path is null ? [] : ReadCredentialDocument(path)
            .Select(pair => (pair.Key, Credential: ParseProviderCredential(pair.Value)))
            .Where(pair => pair.Credential is not null)
            .Select(pair => new ProviderCredentialInfo(pair.Key, pair.Credential is ProviderCredential.OAuth ? "oauth" : "api_key")).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    /// <summary>【AI】【凭据原子修改】在跨进程文件锁内重读、变更并提交，空返回表示保留原值。</summary>
    /// <param name="providerId">提供方标识。</param><param name="mutation">异步修改委托。</param>
    /// <param name="cancellationToken">等待、执行及提交取消信号。</param><returns>提交后的凭据或保留的原值。</returns>
    public async Task<ProviderCredential?> ModifyAsync(string providerId, Func<ProviderCredential?, Task<ProviderCredential?>> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId); ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveWritePath();
        await using var lease = await AcquireCredentialLockAsync(path, cancellationToken).ConfigureAwait(false);
        // 1. 【AI】【凭据原子修改】读取锁内最新值，避免轮换、退出或改用密钥后复活旧凭据
        var entries = ReadCredentialDocument(path);
        var current = entries.TryGetValue(providerId, out var entry) ? ParseProviderCredential(entry) : null;
        var next = await mutation(current).WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (next is null) return current;
        // 2. 【AI】【凭据原子修改】取消或非协作委托迟到时不能写入，提交沿用现有原子文件替换
        if (next is ProviderCredential.OAuth oauth) SaveCore(path, providerId, oauth.Value.ToOAuth());
        else if (next is ProviderCredential.ApiKey apiKey) SaveApiKeyCore(path, providerId, apiKey.Value);
        return next;
    }

    /// <summary>【AI】【凭据退出】在同一文件锁内删除提供方，尚无文件时不创建目录或锁文件。</summary>
    /// <param name="providerId">提供方标识。</param><param name="cancellationToken">取消信号。</param><returns>删除完成任务。</returns>
    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId); cancellationToken.ThrowIfCancellationRequested();
        // 1. 【AI】【退出排序】首次登录尚未提交 auth.json 时也等待已有文件锁，避免注销后出现迟到凭据
        var path = _searchPaths.FirstOrDefault(File.Exists) ?? _searchPaths.FirstOrDefault(candidate => File.Exists(candidate + ".lock"));
        if (path is null) return;
        await using var lease = await AcquireCredentialLockAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RemoveCore(path, providerId, strict: true);
    }

    /// <summary>【AI】【凭据类型映射】保留原生密钥空值和环境字段，兼容旧隐式类型但不解释未来类型。</summary>
    /// <param name="entry">单个 JSON 条目。</param><returns>统一凭据；未知或无效类型为空。</returns>
    private static ProviderCredential? ParseProviderCredential(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        var type = entry.TryGetProperty("type", out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        if (type is not null && !type.Equals("api_key", StringComparison.OrdinalIgnoreCase) && !type.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
            && !type.Equals("oauth", StringComparison.OrdinalIgnoreCase)) return null;
        var parsed = ParseEntry(entry);
        if (parsed is null) return null;
        if (parsed.OAuth is { } oauth) return new ProviderCredential.OAuth(ProviderOAuthCredential.FromOAuth(oauth));
        var key = entry.TryGetProperty("key", out var rawKey) && rawKey.ValueKind == JsonValueKind.String ? rawKey.GetString()
            : entry.TryGetProperty("apiKey", out rawKey) && rawKey.ValueKind == JsonValueKind.String ? rawKey.GetString() : parsed.ApiKey;
        var environment = entry.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object
            ? env.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal) : null;
        return new ProviderCredential.ApiKey(new(key, environment));
    }
}
