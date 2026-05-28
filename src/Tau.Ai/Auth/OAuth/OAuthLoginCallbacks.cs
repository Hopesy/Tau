namespace Tau.Ai.Auth.OAuth;

public interface IOAuthLoginCallbacks
{
    void OnAuth(string url, string? instructions = null);
    Task<string> OnPromptAsync(string message, string? placeholder = null, bool allowEmpty = false);
    void OnProgress(string message);
    Task<string>? OnManualCodeInputAsync();
}

public interface IOAuthManualCodeInputController
{
    void CancelManualCodeInput();
}
