using System.Security.Cryptography;
using System.Text;

namespace Mnemarr.Core.Accounts;

/// <summary>What the database keeps of a secret that is 256 random bits (a session id, an API key): a hash that cannot be guessed back, not the secret.</summary>
internal static class SecretHash
{
    public static string Of(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
