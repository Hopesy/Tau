// 作者：xxx
namespace Tau.Ai;

/// <summary>【AI】【模型元数据】新图片进入会话前采用的稳定缩放配置。</summary>
public sealed record ModelImageResizeOptions
{
    public int? MaxWidth { get; init; }
    public int? MaxHeight { get; init; }
    public long? MaxBytes { get; init; }
    public int? JpegQuality { get; init; }
}

/// <summary>【AI】【模型元数据】图片缩放策略及单消息、单请求数量上限。</summary>
public sealed record ModelImageInputLimits
{
    public ModelImageResizeOptions? Resize { get; init; }
    public int? MaxPerMessage { get; init; }
    public int? MaxPerRequest { get; init; }
}

/// <summary>【AI】【模型元数据】序列化请求大小及图片输入限制。</summary>
public sealed record ModelInputLimits
{
    public long? MaxRequestBytes { get; init; }
    public ModelImageInputLimits? Images { get; init; }
}

/// <summary>【AI】【缓存元数据】短期及长期提示缓存的预计寿命，单位秒；空值表示未知。</summary>
public sealed record ModelPromptCache
{
    public double? Short { get; init; }
    public double? Long { get; init; }
}
