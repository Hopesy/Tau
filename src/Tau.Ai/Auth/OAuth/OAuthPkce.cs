using System.Security.Cryptography;
using System.Text;

namespace Tau.Ai.Auth.OAuth;

public static class OAuthPkce
{
    public static (string Verifier, string Challenge) Generate()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64UrlEncode(verifierBytes);

        var challengeBytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        var challenge = Base64UrlEncode(challengeBytes);

        return (verifier, challenge);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
