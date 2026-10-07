// 作者：xxx
namespace Tau.Ai.Providers;

/// <summary>【AI】【分类调用】分类协议的执行接口。</summary>
public interface IClassifierProvider
{
    /// <summary>实现的协议标识。</summary>
    string Api { get; }

    /// <summary>对结构化状态执行一组分类问题。</summary>
    /// <param name="model">目标分类模型。</param>
    /// <param name="context">状态与问题。</param>
    /// <param name="options">请求配置。</param>
    /// <returns>分类答案或错误结果。</returns>
    Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions options);
}
