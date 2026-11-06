using System.Text;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Conversations;

/// <summary>Writing the note of a conversation: shared by what takes a batch and what changes a title, so that a note is only ever written one way.</summary>
internal static class NoteFiles
{
    /// <summary>Writes a whole note beside its place and moves it in, so that a crash leaves the old one. Returns the length it was left at.</summary>
    public static long Write(Source source, string relativePath, string markdown)
    {
        var full = Path.Combine(source.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = Path.Combine(Path.GetDirectoryName(full)!, $".mnemarr-{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(markdown);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, full, overwrite: true);
            return bytes.Length;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Adds text to the end of a note, after the first <paramref name="expectedLength"/> bytes: the length it was left at. Null when the note is not there, or is shorter.</summary>
    public static long? AppendTo(Source source, string relativePath, long expectedLength, string text)
    {
        var full = Path.Combine(source.Path, relativePath);
        if (!File.Exists(full) || new FileInfo(full).Length < expectedLength)
        {
            return null;
        }

        using var stream = new FileStream(full, FileMode.Open, FileAccess.Write);
        stream.SetLength(expectedLength);
        stream.Seek(0, SeekOrigin.End);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes);
        return expectedLength + bytes.Length;
    }
}
