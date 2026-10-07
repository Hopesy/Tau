// 作者：xxx
using System.Text.Json;

namespace Tau.CodingAgent.Runtime.Mcp;

public sealed partial class CodingAgentMcpService
{
    private sealed partial class Connection
    {
        internal IReadOnlyList<JsonElement> Resources { get; private set; } = [];
        internal IReadOnlyList<JsonElement> ResourceTemplates { get; private set; } = [];

        /// <summary>【CodingAgent】【MCP 资源目录】并行读取资源和模板，列表单独失败时采用空目录，服务器仍可提供其他能力。</summary>
        /// <param name="client">已协商协议的客户端。</param><param name="token">连接生命周期。</param><returns>过滤界面资源后的两个目录。</returns>
        private static async Task<ResourceCatalog> FetchResourceCatalogAsync(CodingAgentMcpClient client, CancellationToken token)
        {
            var resources = FetchResourceListAsync(client, false, token);
            var templates = FetchResourceListAsync(client, true, token);
            await Task.WhenAll(resources, templates).ConfigureAwait(false);
            return new(await resources.ConfigureAwait(false), await templates.ConfigureAwait(false));
        }

        /// <summary>【CodingAgent】【MCP 资源快照】读取完整分页目录，普通失败不导致连接失败，关闭取消继续传播。</summary>
        /// <param name="client">客户端。</param><param name="templates">是否读取模板。</param><param name="token">取消。</param><returns>可供模型访问的资源元数据。</returns>
        private static async Task<IReadOnlyList<JsonElement>> FetchResourceListAsync(CodingAgentMcpClient client, bool templates, CancellationToken token)
        {
            try
            {
                var listed = await client.ListResourcesAsync(templates, token).ConfigureAwait(false);
                return listed.Where(item => !CodingAgentMcpResourceTool.IsAppResource(item)).ToArray();
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { return []; }
        }

        /// <summary>【CodingAgent】【MCP 资源通知】串行排队资源目录刷新，与工具目录共享关闭屏障。</summary>
        /// <param name="client">通知来源。</param>
        private void QueueResourceRefresh(CodingAgentMcpClient client)
        { lock (_tasksGate) { if (!_closed) _refresh = RefreshResourcesAfterAsync(_refresh, client); } }

        /// <summary>【CodingAgent】【MCP 资源更新】只发布当前连接的目录，旧连接或关闭后的响应不得覆盖新状态。</summary>
        /// <param name="previous">前一项目录刷新。</param><param name="client">通知来源。</param><returns>刷新任务。</returns>
        private async Task RefreshResourcesAfterAsync(Task previous, CodingAgentMcpClient client)
        {
            await Task.Yield(); await previous.ConfigureAwait(false);
            try
            {
                var catalog = await FetchResourceCatalogAsync(client, _lifetime.Token).ConfigureAwait(false);
                lock (_owner._gate)
                {
                    if (_closed || !ReferenceEquals(client, _client)) return;
                    Resources = catalog.Resources; ResourceTemplates = catalog.Templates;
                    Interlocked.Increment(ref _owner._version);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }

        /// <summary>【CodingAgent】【MCP 资源集合】一次完整读取的资源和模板快照。</summary>
        /// <param name="Resources">普通资源。</param><param name="Templates">URI 模板。</param>
        private sealed record ResourceCatalog(IReadOnlyList<JsonElement> Resources, IReadOnlyList<JsonElement> Templates);
    }
}
