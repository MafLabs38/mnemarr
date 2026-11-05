namespace Mnemarr.Core.Notes;

/// <summary>The id of a heading, so that a link can lead to it: the same rule on the server (links) and in the interface (the ids of the headings it draws).</summary>
public static class Headings
{
    /// <summary>Lower case, spaces as hyphens, letters, digits, hyphens and underscores kept, the rest dropped (the rule of GitHub's anchors).</summary>
    public static string Slug(string heading)
    {
        var slug = new System.Text.StringBuilder(heading.Length);
        foreach (var c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                slug.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                slug.Append('-');
            }
        }

        return slug.ToString();
    }
}
