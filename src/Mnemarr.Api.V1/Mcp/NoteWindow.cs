namespace Mnemarr.Api.V1.Mcp;

/// <summary>
/// A part of the text of a note, for a tool that must not hand a model more than it can read: a conversation can hold hundreds of thousands of characters, and the part
/// that matters is often its end or the place where some words are. The answer says how long the note is and where to read on, so that nothing is cut without saying so.
/// </summary>
public sealed record NoteWindow(string Text, int Total, int Offset, bool Truncated, int? NextOffset)
{
    public const int MaxLength = 20_000;

    /// <summary>A window on the text: from <paramref name="offset"/>, or the end (<paramref name="tail"/>), or around the first <paramref name="around"/>; at most <paramref name="length"/> characters.</summary>
    public static NoteWindow Of(string text, int? offset, int? length, bool tail, string? around)
    {
        var size = Math.Clamp(length ?? MaxLength, 1, MaxLength);
        if (text.Length <= size && offset is null or 0 && !tail && around is null)
        {
            return new NoteWindow(text, text.Length, 0, false, null);
        }

        var start = Start(text, offset, size, tail, around);
        start = Math.Clamp(start, 0, text.Length);
        var count = Math.Min(size, text.Length - start);
        // Do not cut a character in two (a surrogate pair is one character for a person).
        if (count > 0 && start + count < text.Length && char.IsHighSurrogate(text[start + count - 1]))
        {
            count--;
        }

        var end = start + count;
        return new NoteWindow(text.Substring(start, count), text.Length, start, start > 0 || end < text.Length, end < text.Length ? end : null);
    }

    private static int Start(string text, int? offset, int size, bool tail, string? around)
    {
        if (tail)
        {
            return text.Length - size;
        }

        if (!string.IsNullOrWhiteSpace(around))
        {
            var found = text.IndexOf(around.Trim(), StringComparison.OrdinalIgnoreCase);
            if (found >= 0)
            {
                return found - (size / 4);
            }
        }

        return offset ?? 0;
    }
}
