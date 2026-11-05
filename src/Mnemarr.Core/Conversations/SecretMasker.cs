using System.Text.RegularExpressions;

namespace Mnemarr.Core.Conversations;

/// <summary>
/// Hides what looks like a secret in a text that is about to be stored and indexed (D-10): the collector does it before it sends, and this does
/// it again, because the server must not trust what it is sent. The rules are the same on both sides, and a file of samples
/// (<c>tests/masking/samples.json</c>) is run by the tests of both, so that two engines written by hand do not drift (Q-27).
/// A text is masked as it is written in JSON too (the quotes and line breaks of a conversation line are escaped).
/// </summary>
public static class SecretMasker
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        Rule(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----", "[masked:private-key]", RegexOptions.Singleline),
        Rule(@"\bgithub_pat_[A-Za-z0-9_]{40,}", "[masked:github-token]"),
        Rule(@"\bgh[pousr]_[A-Za-z0-9]{30,}", "[masked:github-token]"),
        Rule(@"\bsk-[A-Za-z0-9_-]{20,}", "[masked:api-key]"),
        Rule(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b", "[masked:aws-key]"),
        Rule(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", "[masked:slack-token]"),
        Rule(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", "[masked:jwt]"),
        Rule(@"\bBearer\s+[A-Za-z0-9._~+/=-]{20,}", "Bearer [masked:token]", RegexOptions.IgnoreCase),
        Rule(@"(https?://)[^/\s:@]+:[^/\s@]+@", "$1[masked:credentials]@"),
        Rule(
            @"\b([A-Z0-9_]*(?:PASSWORD|PASSWD|SECRET|TOKEN|API_?KEY|ACCESS_?KEY|PRIVATE_?KEY)[A-Z0-9_]*)(?:\\?[""'])?\s*[=:]\s*(?:\\?[""'])?[^\s""',;\\]{8,}",
            "$1=[masked]",
            RegexOptions.IgnoreCase),
    ];

    /// <summary>The text with every secret it contains replaced by a word that says what was there.</summary>
    public static string Mask(string text)
    {
        foreach (var (pattern, replacement) in Rules)
        {
            text = pattern.Replace(text, replacement);
        }

        return text;
    }

    private static (Regex, string) Rule(string pattern, string replacement, RegexOptions options = RegexOptions.None) =>
        (new Regex(pattern, options | RegexOptions.CultureInvariant, Limit), replacement);
}
