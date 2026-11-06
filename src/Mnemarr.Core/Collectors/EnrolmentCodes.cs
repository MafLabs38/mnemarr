using System.Security.Cryptography;

namespace Mnemarr.Core.Collectors;

/// <summary>
/// The code that joins a machine: 12 characters of an alphabet without look-alikes (about 60 bits), written in three groups so that a person can
/// read it out and type it. Kept as a hash; the form a person types (lower case, spaces, dashes) is brought back to the one that was hashed.
/// </summary>
public static class EnrolmentCodes
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int Length = 12;

    public static string New()
    {
        var characters = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            characters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var raw = new string(characters);
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
    }

    /// <summary>The code as it is hashed, or null when what was typed cannot be a code.</summary>
    public static string? Normalise(string? typed)
    {
        if (typed is null)
        {
            return null;
        }

        var raw = new string([.. typed.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant)]);
        return raw.Length == Length && raw.All(c => Alphabet.Contains(c, StringComparison.Ordinal)) ? raw : null;
    }

    public static string Hash(string normalised) => Mnemarr.Core.Accounts.SecretHash.Of(normalised);
}
