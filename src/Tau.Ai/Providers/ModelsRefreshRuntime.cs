// 作者：xxx
using System.Collections.Concurrent;
using System.Text.Json;
using Tau.Ai.Auth;
using Tau.Ai.Registry;
using Tau.Ai.Serialization;

namespace Tau.Ai.Providers;

public sealed partial class Models
{
    private readonly Registry.ModelsStore _modelsStore;
    private readonly Dictionary<string, long> _refreshGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RefreshCall> _refreshCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<bool>> _publicationTails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>【AI】【刷新代次】在注册锁内废弃旧刷新，取消回调异步执行以允许提供方重入。</summary>
    /// <param name="id">提供方标识。</param><returns>新的代次。</returns>
    private long SupersedeRefreshLocked(string id)
    {
        var generation = _refreshGenerations.GetValueOrDefault(id) + 1;
        _refreshGenerations[id] = generation;
        if (_refreshCalls.Remove(id, out var previous)) _ = ObserveCancellationAsync(previous.Source);
        return generation;
    }

    /// <summary>【AI】【刷新取消】观察取消回调异常，避免旧刷新影响新的注册或刷新。</summary>
    /// <param name="source">旧刷新取消源。</param><returns>取消通知完成任务。</returns>
    private static async Task ObserveCancellationAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { }
    }

    /// <summary>【AI】【刷新有效性】在注册锁内检查提供方实例、代次及取消状态。</summary>
    /// <param name="call">刷新身份。</param><returns>是否仍可提交结果。</returns>
    private bool IsCurrentRefreshLocked(RefreshCall call) => !call.Token.IsCancellationRequested
        && _refreshGenerations.GetValueOrDefault(call.Provider.Id) == call.Generation
        && _providers.TryGetValue(call.Provider.Id, out var registered) && ReferenceEquals(registered, call.Provider);

    /// <summary>【AI】【目录刷新】并行刷新提供方；原生定义先恢复缓存，再准备认证并执行联网阶段。</summary>
    /// <param name="providerIds">目标提供方。</param><param name="allowNetwork">联网策略。</param><param name="token">调用方取消。</param>
    /// <param name="force">强制获取选项。</param><returns>调用方取消状态及各提供方错误。</returns>
    private async Task<ModelsRefreshResult> RefreshCoreAsync(IEnumerable<string>? providerIds, bool allowNetwork, CancellationToken token, bool? force)
    {
        var errors = new ConcurrentDictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
        if (token.IsCancellationRequested) return new(true, errors);
        var selected = providerIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var providers = GetProviders().Where(provider => provider.IsDynamic && (selected is null || selected.Contains(provider.Id))).ToArray();
        var tasks = providers.Select(async provider =>
        {
            RefreshCall call;
            lock (_gate)
            {
                if (!_providers.TryGetValue(provider.Id, out var registered) || !ReferenceEquals(registered, provider)) return;
                var generation = SupersedeRefreshLocked(provider.Id);
                var source = CancellationTokenSource.CreateLinkedTokenSource(token);
                call = new(provider, generation, source, source.Token);
                _refreshCalls[provider.Id] = call;
            }
            try { await RunRefreshAsync(call, allowNetwork, force).WaitAsync(call.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (call.Token.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!call.Token.IsCancellationRequested) errors[provider.Id] = error;
            }
            finally
            {
                lock (_gate)
                {
                    if (_refreshCalls.TryGetValue(provider.Id, out var current) && ReferenceEquals(current, call)) _refreshCalls.Remove(provider.Id);
                }
                call.Source.Dispose();
            }
        }).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        return new(token.IsCancellationRequested, new Dictionary<string, Exception>(errors, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>【AI】【提供方刷新】兼容旧快照委托，原生回调按离线、认证、联网顺序执行。</summary>
    /// <param name="call">刷新身份。</param><param name="allowNetwork">联网策略。</param><param name="force">强制获取。</param><returns>刷新任务。</returns>
    private async Task RunRefreshAsync(RefreshCall call, bool allowNetwork, bool? force)
    {
        var provider = call.Provider;
        if (!provider.HasContextRefresh)
        {
            if (!allowNetwork) return;
            var models = await provider.FetchLegacyModelsAsync(call.Token).WaitAsync(call.Token).ConfigureAwait(false);
            lock (_gate) if (IsCurrentRefreshLocked(call)) provider.SetModels(models);
            return;
        }

        // 1. 【AI】【离线目录】凭据读取失败也先尝试恢复缓存，保留可以离线使用的目录
        ProviderCredential? storedCredential = null;
        Exception? credentialError = null;
        try { storedCredential = await _credentialStore.ReadAsync(provider.Id, call.Token).WaitAsync(call.Token).ConfigureAwait(false); }
        catch (Exception error) { credentialError = new ModelsError("auth", $"Credential store read failed for {provider.Id}.", error); }
        await RunRefreshPhaseAsync(call, storedCredential, false, null).ConfigureAwait(false);
        if (credentialError is not null) throw credentialError;
        if (!allowNetwork || call.Token.IsCancellationRequested) return;

        // 2. 【AI】【联网目录】认证准备失败或无有效认证时不调用联网阶段
        var credential = await ResolveCatalogCredentialAsync(provider, storedCredential, call.Token).ConfigureAwait(false);
        if (credential is not null) await RunRefreshPhaseAsync(call, credential, true, force).ConfigureAwait(false);
    }

    /// <summary>【AI】【目录刷新认证】保留凭据类型，目录发现仅刷新已过期 OAuth，API key 由提供方解析。</summary>
    /// <param name="provider">提供方。</param><param name="stored">原始凭据。</param><param name="token">取消信号。</param><returns>联网有效凭据或空值。</returns>
    private async Task<ProviderCredential?> ResolveCatalogCredentialAsync(ProviderDefinition provider, ProviderCredential? stored, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stored is ProviderCredential.OAuth oauth)
        {
            if (provider.Auth?.OAuth is not { } definition) return null;
            if (!oauth.Value.ToOAuth().IsExpired(TimeSpan.Zero)) return stored;
            var post = await _credentialStore.ModifyAsync(provider.Id, async current =>
            {
                token.ThrowIfCancellationRequested();
                if (current is not ProviderCredential.OAuth candidate || !candidate.Value.ToOAuth().IsExpired(TimeSpan.Zero)) return null;
                var refreshed = await definition.RefreshAsync(candidate.Value, token).WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new ProviderCredential.OAuth(refreshed);
            }, token).WaitAsync(token).ConfigureAwait(false);
            return post is ProviderCredential.OAuth ? post : null;
        }
        if (provider.Auth?.ApiKey is not { } apiKey) return null;
        var input = stored is ProviderCredential.ApiKey key ? key.Value : null;
        var result = await apiKey.ResolveAsync(new(provider.Id, input, null, token)).WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result is null ? null : new ProviderCredential.ApiKey(new(result.ApiKey, result.Env));
    }

    /// <summary>【AI】【刷新阶段】每个阶段重新读取并复制缓存，提供方只能通过发布入口修改共享状态。</summary>
    /// <param name="call">刷新身份。</param><param name="credential">此阶段凭据。</param><param name="allowNetwork">联网策略。</param>
    /// <param name="force">联网强制选项。</param><returns>阶段任务。</returns>
    private async Task RunRefreshPhaseAsync(RefreshCall call, ProviderCredential? credential, bool allowNetwork, bool? force)
    {
        var stored = await _modelsStore.ReadAsync(call.Provider.Id, new(call.Token)).WaitAsync(call.Token).ConfigureAwait(false);
        if (stored is not null) stored = CloneCatalog(stored with { Models = stored.Models.Where(ModelTypes.IsKnown).ToArray() });
        call.Token.ThrowIfCancellationRequested();
        await call.Provider.RefreshModelsAsync(new RefreshModelsContext(credential, stored, allowNetwork, allowNetwork ? force : null,
            call.Token, publication => PublishModelsAsync(call, publication))).WaitAsync(call.Token).ConfigureAwait(false);
    }

    /// <summary>【AI】【目录发布队列】同一提供方持久化串行执行，取消调用方等待不允许后续写入越过尚未完成的旧写入。</summary>
    /// <param name="call">刷新身份。</param><param name="publication">持久化策略与同步更新。</param><returns>是否完成有效发布。</returns>
    private Task<bool> PublishModelsAsync(RefreshCall call, ModelsPublication publication)
    {
        if (call.Token.IsCancellationRequested) return Task.FromCanceled<bool>(call.Token);
        Task<bool> queued;
        lock (_gate)
        {
            var previous = _publicationTails.GetValueOrDefault(call.Provider.Id);
            queued = RunPublicationAsync(previous, call, publication);
            _publicationTails[call.Provider.Id] = queued;
        }
        _ = ObservePublicationAsync(call.Provider.Id, queued);
        if (call.Token.IsCancellationRequested) return Task.FromCanceled<bool>(call.Token);
        return queued.WaitAsync(call.Token);
    }

    /// <summary>【AI】【发布提交】先等待前次发布，再检查代次、执行持久化、重新检查并同步更新内存。</summary>
    /// <param name="previous">前次发布。</param><param name="call">刷新身份。</param><param name="publication">发布内容。</param><returns>是否提交。</returns>
    private async Task<bool> RunPublicationAsync(Task<bool>? previous, RefreshCall call, ModelsPublication publication)
    {
        await Task.Yield();
        if (previous is not null) { try { await previous.ConfigureAwait(false); } catch (Exception) { } }
        lock (_gate) if (!IsCurrentRefreshLocked(call)) return false;
        if (publication.HasPersistence)
        {
            if (publication.Persist is { } persisted) await _modelsStore.WriteAsync(call.Provider.Id, CloneCatalog(persisted), new(call.Token)).ConfigureAwait(false);
            else await _modelsStore.DeleteAsync(call.Provider.Id, new(call.Token)).ConfigureAwait(false);
        }
        lock (_gate)
        {
            if (!IsCurrentRefreshLocked(call)) return false;
            publication.Update?.Invoke();
            return true;
        }
    }

    /// <summary>【AI】【发布清理】观察失败并只删除仍指向本任务的队尾，保留后续发布顺序。</summary>
    /// <param name="providerId">提供方。</param><param name="publication">发布任务。</param><returns>清理任务。</returns>
    private async Task ObservePublicationAsync(string providerId, Task<bool> publication)
    {
        try { await publication.ConfigureAwait(false); } catch (Exception) { }
        lock (_gate) if (ReferenceEquals(_publicationTails.GetValueOrDefault(providerId), publication)) _publicationTails.Remove(providerId);
    }

    /// <summary>【AI】【目录隔离】使用生成的 JSON 元数据复制嵌套配置，避免调用方修改缓存或发布输入。</summary>
    /// <param name="entry">目录条目。</param><returns>独立副本。</returns>
    private static ModelsStoreEntry CloneCatalog(ModelsStoreEntry entry) => JsonSerializer.Deserialize(
        JsonSerializer.SerializeToUtf8Bytes(entry, TauAiJsonContext.Default.ModelsStoreEntry), TauAiJsonContext.Default.ModelsStoreEntry)!;

    /// <summary>【AI】【刷新身份】提供方实例、代次及与取消源生命周期分离的信号快照。</summary>
    private sealed record RefreshCall(ProviderDefinition Provider, long Generation, CancellationTokenSource Source, CancellationToken Token);
}
