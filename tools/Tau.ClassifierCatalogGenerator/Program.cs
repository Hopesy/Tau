// 作者：xxx
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tau.ClassifierCatalogGenerator;

/// <summary>【AI】【分类目录生成】从固定来源快照生成可离线复现的分类模型目录。</summary>
public static class Program
{
    public const string VercelCatalogUrl = "https://ai-gateway.vercel.sh/v1/models";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>运行目录生成命令，错误输出到标准错误并返回非零状态。</summary>
    /// <param name="args">来源文件、输出文件及可选 --check 或 --refresh-vercel。</param>
    /// <returns>成功返回 0，输入错误或生成结果过期返回 1。</returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] is not ("--check" or "--refresh-vercel")))
                throw new ArgumentException("Usage: <sources.json> <seed.json> [--check | --refresh-vercel]");
            var sourcePath = Path.GetFullPath(args[0]);
            var outputPath = Path.GetFullPath(args[1]);
            if (sourcePath.Equals(outputPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Input and output paths must differ.");
            var source = await File.ReadAllTextAsync(sourcePath, Encoding.UTF8).ConfigureAwait(false);
            if (args.Length == 3 && args[2] == "--refresh-vercel")
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                source = RefreshVercel(source, await client.GetStringAsync(VercelCatalogUrl).ConfigureAwait(false), DateTimeOffset.UtcNow);
            }

            // 1. 【AI】【分类目录生成】完成所有校验后才写文件，失败不会以空目录替换旧数据
            var generated = Generate(source);
            if (args.Length == 3 && args[2] == "--check")
            {
                if (!File.Exists(outputPath) || await File.ReadAllTextAsync(outputPath, Encoding.UTF8).ConfigureAwait(false) != generated)
                    throw new InvalidOperationException("Classifier catalog is stale; regenerate it from the checked-in sources.");
            }
            else
            {
                await WriteAtomicAsync(outputPath, generated).ConfigureAwait(false);
                if (args.Length == 3) await WriteAtomicAsync(sourcePath, source).ConfigureAwait(false);
            }
            Console.WriteLine("【AI】【分类目录生成】完成目录生成或一致性检查");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"【AI】【分类目录生成】{ex.Message}");
            return 1;
        }
    }

    /// <summary>验证来源快照、筛选 evaluation 模型并统一生成 .NET 模型字段。</summary>
    /// <param name="source">固定来源快照 JSON。</param>
    /// <returns>按 provider 和 id 排序的确定性 JSON，包含输入 SHA-256。</returns>
    public static string Generate(string source)
    {
        var root = JsonNode.Parse(source)?.AsObject() ?? throw new JsonException("Sources must be an object.");
        if (root["schemaVersion"]?.GetValue<int>() != 1) throw new JsonException("Unsupported classifier sources version.");
        var models = new List<JsonObject>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var staticModels = root["staticModels"]?.AsArray() ?? throw new JsonException("Missing staticModels.");
        foreach (var item in staticModels)
        {
            var model = item?.DeepClone().AsObject() ?? throw new JsonException("Invalid static model.");
            ValidateModel(model, keys);
            models.Add(model);
        }
        var vercel = root["vercel"]?["data"]?.AsArray() ?? throw new JsonException("Missing Vercel catalog data.");
        var evaluationCount = 0;
        foreach (var item in vercel)
        {
            if (item is not JsonObject value || value["type"]?.GetValue<string>() != "evaluation") continue;
            evaluationCount++;
            var id = RequiredString(value, "id");
            var contextWindow = value["context_window"]?.GetValue<int>() ?? 4096;
            var model = new JsonObject
            {
                ["type"] = "classifier", ["provider"] = "vercel-ai-gateway", ["api"] = "typesafe-system-one",
                ["id"] = id, ["name"] = value["name"]?.GetValue<string>() is { Length: > 0 } name ? name : id,
                ["baseUrl"] = "https://ai-gateway.vercel.sh/typesafe/v1", ["inputModalities"] = new JsonArray("text"),
                ["contextWindow"] = contextWindow,
                ["cost"] = new JsonObject
                {
                    ["inputPerMillion"] = Price(value["pricing"]?["input"]) * 1_000_000m,
                    ["outputPerMillion"] = Price(value["pricing"]?["output"]) * 1_000_000m,
                    ["cacheReadPerMillion"] = 0, ["cacheWritePerMillion"] = 0
                }
            };
            ValidateModel(model, keys);
            models.Add(model);
        }
        if (staticModels.Count == 0 || evaluationCount == 0) throw new JsonException("Classifier sources must include static and Vercel evaluation models.");
        var output = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["sourceSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
            ["sources"] = root["sources"]?.DeepClone() ?? throw new JsonException("Missing source provenance."),
            ["models"] = new JsonArray(models.OrderBy(model => RequiredString(model, "provider"), StringComparer.Ordinal)
                .ThenBy(model => RequiredString(model, "id"), StringComparer.Ordinal).Select(model => (JsonNode)model).ToArray())
        };
        return output.ToJsonString(JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>从公开 Vercel 目录提取生成所需字段，保留静态模型来源。</summary>
    /// <param name="source">已有来源快照。</param>
    /// <param name="response">Vercel /v1/models 的完整 JSON。</param>
    /// <param name="retrievedAt">获取时间。</param>
    /// <returns>更新后的来源快照。</returns>
    public static string RefreshVercel(string source, string response, DateTimeOffset retrievedAt)
    {
        var root = JsonNode.Parse(source)?.AsObject() ?? throw new JsonException("Invalid sources.");
        var data = JsonNode.Parse(response)?["data"]?.AsArray() ?? throw new JsonException("Vercel catalog has no data array.");
        var selected = new JsonArray();
        foreach (var item in data)
        {
            if (item?["type"]?.GetValue<string>() != "evaluation") continue;
            var model = new JsonObject();
            foreach (var field in new[] { "id", "name", "type", "context_window", "pricing" }) model[field] = item[field]?.DeepClone();
            selected.Add((JsonNode)model);
        }
        if (selected.Count == 0) throw new JsonException("Vercel returned no evaluation models; previous snapshot was retained.");
        root["vercel"] = new JsonObject { ["data"] = selected };
        var sources = root["sources"]?.AsObject() ?? throw new JsonException("Missing source provenance.");
        sources["vercel"] = new JsonObject
        {
            ["url"] = VercelCatalogUrl, ["retrievedAt"] = retrievedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["responseSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(response)))
        };
        var updated = root.ToJsonString(JsonOptions) + "\n";
        _ = Generate(updated);
        return updated;
    }

    /// <summary>检查生成条目的类型、唯一性、上下文和计费字段，拒绝不支持的协议。</summary>
    /// <param name="model">待检查模型。</param>
    /// <param name="keys">已出现的 provider/id 集合。</param>
    private static void ValidateModel(JsonObject model, HashSet<string> keys)
    {
        if (RequiredString(model, "type") != "classifier") throw new JsonException("Only classifier models are accepted.");
        if (RequiredString(model, "api") is not ("typesafe-system-one" or "cloudflare-workers-ai-system-one")) throw new JsonException("Unsupported classifier API.");
        var provider = RequiredString(model, "provider");
        var id = RequiredString(model, "id");
        _ = RequiredString(model, "name");
        if (!keys.Add(provider + "\0" + id)) throw new JsonException($"Duplicate classifier model: {provider}/{id}");
        if (!Uri.TryCreate(RequiredString(model, "baseUrl"), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) throw new JsonException("Invalid classifier base URL.");
        if (model["contextWindow"]?.GetValue<int>() is not > 0) throw new JsonException("Classifier contextWindow must be positive.");
        if (model["inputModalities"] is not JsonArray input || input.Count == 0 || input.Any(value => value?.GetValue<string>() is not ("text" or "image"))) throw new JsonException("Invalid classifier input modalities.");
        var cost = model["cost"]?.AsObject() ?? throw new JsonException("Missing classifier cost.");
        foreach (var field in new[] { "inputPerMillion", "outputPerMillion", "cacheReadPerMillion", "cacheWritePerMillion" }) _ = Price(cost[field]);
    }

    /// <summary>读取非空字符串字段。</summary>
    /// <param name="value">源对象。</param>
    /// <param name="field">字段名称。</param>
    /// <returns>非空字段值。</returns>
    private static string RequiredString(JsonObject value, string field) =>
        value[field]?.GetValue<string>() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) ? text : throw new JsonException($"Missing {field}.");

    /// <summary>读取字符串或数值价格，不把损坏或缺失的价格当作免费。</summary>
    /// <param name="node">价格节点。</param>
    /// <returns>有限非负十进制价格。</returns>
    private static decimal Price(JsonNode? node)
    {
        var text = node is JsonValue value && value.TryGetValue<string>(out var stringValue) ? stringValue : node?.ToJsonString();
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var price) || price < 0)
            throw new JsonException("Classifier price must be a finite nonnegative number.");
        return price;
    }

    /// <summary>在完整生成后写入临时文件并替换目标。</summary>
    /// <param name="path">目标文件。</param>
    /// <param name="content">完整内容。</param>
    /// <returns>写入完成任务。</returns>
    private static async Task WriteAtomicAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
