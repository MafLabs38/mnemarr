using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Mnemarr.Embedder;

/// <summary>The key a client must present. Only its hash is kept in memory, and clients are compared in constant time.</summary>
public sealed class EmbedderKey
{
    private readonly byte[] _hash;

    public EmbedderKey(IOptions<EmbedderOptions> options)
    {
        var key = File.ReadAllText(options.Value.KeyFile!).Trim();
        if (key.Length < 16)
        {
            throw new InvalidOperationException("The key file of the embedding server must hold a key of at least 16 characters.");
        }

        _hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    public bool Accepts(string? presented) =>
        presented is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), _hash);
}
