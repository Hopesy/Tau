namespace Tau.Mom;

public interface IMomChannelTransport
{
    IMomChannelResponder Responder { get; }

    IAsyncEnumerable<MomChannelMessage> ReadMessagesAsync(CancellationToken cancellationToken = default);
}

public interface IMomChannelResponder
{
    Task<string?> RespondAsync(
        MomChannelMessage message,
        string text,
        CancellationToken cancellationToken = default);

    Task<string?> RespondInThreadAsync(
        MomChannelMessage message,
        string text,
        CancellationToken cancellationToken = default);

    Task SetTypingAsync(
        MomChannelMessage message,
        bool isTyping,
        CancellationToken cancellationToken = default);

    Task UploadFileAsync(
        MomChannelMessage message,
        string filePath,
        string? title = null,
        CancellationToken cancellationToken = default);
}

public interface IMomChannelMessageRuntimeResponder : IMomChannelResponder
{
    Task<string?> StartResponseAsync(
        MomChannelMessage message,
        string text,
        CancellationToken cancellationToken = default);

    Task UpdateResponseAsync(
        MomChannelMessage message,
        string responseTs,
        string text,
        CancellationToken cancellationToken = default);

    Task DeleteResponseAsync(
        MomChannelMessage message,
        string responseTs,
        CancellationToken cancellationToken = default);
}
