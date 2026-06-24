namespace Tau.Ai.Auth;

public sealed class ProviderAuthException : Exception
{
    public ProviderAuthException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
