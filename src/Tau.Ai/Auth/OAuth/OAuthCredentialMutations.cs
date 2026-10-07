// 作者：xxx
using System.Text.Json;

namespace Tau.Ai.Auth.OAuth;

public sealed partial class OAuthCredentialStore
{
    /// <summary>【AI】【文件凭据刷新】单次网络刷新时限，测试可缩短以覆盖迟到结果。</summary>
    internal TimeSpan RefreshTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>【AI】【API key 保存】在同一凭据锁内保存密钥及提供方环境字段。</summary>
    /// <param name="providerId">提供方。</param><param name="credential">API key 凭据。</param>
    public void SaveApiKey(string providerId, ApiKeyCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var path = ResolveWritePath();
        using var lease = AcquireCredentialLockAsync(path, default).GetAwaiter().GetResult();
        SaveApiKeyCore(path, providerId, credential);
    }

    /// <summary>【AI】【密钥提交】在调用方持有文件锁时保留其他提供方和未知字段，原子写入密钥条目。</summary>
    /// <param name="path">已锁定的文件路径。</param><param name="providerId">提供方。</param><param name="credential">密钥及环境字段。</param>
    private static void SaveApiKeyCore(string path, string providerId, ApiKeyCredential credential)
    {
        var existing = ReadCredentialDocument(path);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in existing)
                if (!key.Equals(providerId, StringComparison.OrdinalIgnoreCase)) { writer.WritePropertyName(key); value.WriteTo(writer); }
            writer.WriteStartObject(providerId);
            writer.WriteString("type", "api_key");
            if (credential.Key is not null) writer.WriteString("key", credential.Key);
            if (credential.Env is { } env)
            {
                writer.WriteStartObject("env");
                foreach (var (key, value) in env) writer.WriteString(key, value);
                writer.WriteEndObject();
            }
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        WriteAuthFile(path, stream.ToArray());
    }

    /// <summary>【AI】【凭据刷新】在跨进程文件锁内重读并刷新，确保旋转令牌只使用一次。</summary>
    /// <param name="providerId">提供方。</param><param name="provider">令牌刷新实现。</param>
    /// <param name="token">等待、网络和提交的取消信号。</param><param name="refreshSkew">提前刷新余量，默认五分钟；目录刷新传零。</param>
    /// <param name="refreshTimeout">刷新时限；默认使用请求时限，目录刷新可传无限时限并由调用方取消。</param>
    /// <returns>锁内读取的新凭据；已注销或变更类型时为空。</returns>
    public async Task<OAuthCredentials?> RefreshAsync(string providerId, IOAuthProvider provider, CancellationToken token = default, TimeSpan? refreshSkew = null,
        TimeSpan? refreshTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(provider);
        token.ThrowIfCancellationRequested();
        var path = ResolveWritePath();
        await using var lease = await AcquireCredentialLockAsync(path, token).ConfigureAwait(false);
        var entries = ReadCredentialDocument(path);
        var credentials = entries.TryGetValue(providerId, out var entry) ? ParseEntry(entry)?.OAuth : null;
        if (credentials is null || !credentials.IsExpired(refreshSkew)) return credentials;
        // 1. 【AI】【凭据刷新】持锁调用提供方，后续请求只使用已经提交的最新凭据
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(token);
        refresh.CancelAfter(refreshTimeout ?? RefreshTimeout);
        OAuthCredentials updated;
        try
        {
            updated = await provider.RefreshTokenAsync(credentials, refresh.Token).WaitAsync(refresh.Token).ConfigureAwait(false);
            refresh.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && refresh.IsCancellationRequested)
        {
            throw new TimeoutException($"OAuth refresh timed out for {providerId}.", error);
        }
        // 2. 【AI】【凭据刷新】取消或超时不等待非协作提供方，也不接受之后返回的轮换令牌
        SaveCore(path, providerId, updated);
        return updated;
    }

    /// <summary>【AI】【凭据路径】确定本次写入目标，后续读改写始终使用同一路径。</summary>
    /// <returns>规范绝对路径。</returns>
    private string ResolveWritePath() => _searchPaths.FirstOrDefault(File.Exists) ?? _searchPaths.FirstOrDefault()
        ?? throw new InvalidOperationException("No credential storage path is configured.");

    /// <summary>【AI】【凭据锁】获取跨实例、跨进程文件锁，等待竞争期间响应取消。</summary>
    /// <param name="path">凭据文件。</param><param name="token">取消信号。</param><returns>释放即解锁的文件句柄。</returns>
    private static async Task<FileStream> AcquireCredentialLockAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
            { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }

    /// <summary>【AI】【凭据读取】写入前严格读取现有对象，拒绝用新内容覆盖损坏的文件。</summary>
    /// <param name="path">凭据文件。</param><returns>保留所有未知条目的独立快照。</returns>
    private static Dictionary<string, JsonElement> ReadCredentialDocument(string path)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Credential storage must contain an object.");
            foreach (var property in document.RootElement.EnumerateObject()) result[property.Name] = property.Value.Clone();
        }
        catch (FileNotFoundException) { }
        return result;
    }
}
