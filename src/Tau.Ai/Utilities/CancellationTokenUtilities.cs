namespace Tau.Ai;

public sealed class CombinedCancellationToken : IDisposable
{
    private readonly CancellationTokenSource? _source;

    internal CombinedCancellationToken(CancellationToken token, CancellationTokenSource? source)
    {
        Token = token;
        _source = source;
    }

    public CancellationToken Token { get; }

    public void Dispose()
    {
        _source?.Dispose();
    }
}

public static class CancellationTokenUtilities
{
    public static CombinedCancellationToken Combine(params CancellationToken[] tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        return Combine((IEnumerable<CancellationToken>)tokens);
    }

    public static CombinedCancellationToken Combine(IEnumerable<CancellationToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var activeTokens = tokens.Where(static token => token.CanBeCanceled).ToArray();
        return activeTokens.Length switch
        {
            0 => new CombinedCancellationToken(CancellationToken.None, null),
            1 => new CombinedCancellationToken(activeTokens[0], null),
            _ => CreateLinkedToken(activeTokens)
        };
    }

    private static CombinedCancellationToken CreateLinkedToken(CancellationToken[] tokens)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(tokens);
        return new CombinedCancellationToken(source.Token, source);
    }
}
