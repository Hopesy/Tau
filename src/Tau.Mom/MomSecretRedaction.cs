using Tau.Ai;

namespace Tau.Mom;

internal static class MomSecretRedaction
{
    public const string EnvironmentVariable = "TAU_MOM_REDACT_SECRETS";

    public static TauSecretRedactor CreateRedactor()
    {
        return TauSecretRedactor.ForEnvironmentVariable(EnvironmentVariable);
    }

    public static string RedactText(string? value, TauSecretRedactor redactor)
    {
        return redactor.Redact(value);
    }

    public static string RedactJson(string? json)
    {
        var redactor = CreateRedactor();
        return JsonlSecretRedactor.RedactLine(json, redactor);
    }
}
