using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Configuration;

namespace Mnemarr.Core.Conversations;

/// <summary>
/// The lines of the conversations as they were received (after masking), compressed, in the data folder and not in Git (D-06, ADR 0004). A batch
/// is one file named by the position of its first line, so that sending the same batch again writes the same file: nothing is doubled, and what
/// the notes show can always be read again from these.
/// </summary>
public sealed class RawConversations
{
    private readonly IOptions<MnemarrOptions> _options;

    public RawConversations(IOptions<MnemarrOptions> options)
    {
        _options = options;
    }

    private const string InfoFile = "info.json";

    private string FolderOf(string machine, string session) => Path.Combine(Path.GetFullPath(_options.Value.DataFolder), "raw", machine, session);

    /// <summary>Keeps a batch that starts at the given position. Written beside and moved into place, so that a crash leaves no half file.</summary>
    public void Write(string machine, string session, long from, IReadOnlyList<string> lines) => Write(machine, session, from, (IEnumerable<string>)lines);

    private void Write(string machine, string session, long from, IEnumerable<string> lines)
    {
        var folder = FolderOf(machine, session);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"{from:D12}.jsonl.gz");
        var temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = File.Create(temporary))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            {
                foreach (var line in lines)
                {
                    writer.Write(line);
                    writer.Write('\n');
                }
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Says what a folder of lines is: the format that reads them and the application they come from. Without it a lost database could not tell which
    /// parser reads a folder, which a second format would make impossible (D-06). Written once, with the first batch; writing it again changes nothing.
    /// </summary>
    public void WriteInfo(string machine, string session, RawInfo info)
    {
        var folder = FolderOf(machine, session);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, InfoFile);
        var temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(info), new UTF8Encoding(false));
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>What <see cref="WriteInfo"/> kept, or null when the folder has none (or it cannot be read).</summary>
    public RawInfo? ReadInfo(string machine, string session)
    {
        var target = Path.Combine(FolderOf(machine, session), InfoFile);
        try
        {
            return File.Exists(target) ? JsonSerializer.Deserialize<RawInfo>(File.ReadAllText(target)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A conversation kept in more batches than this is made into one file: reading it again stays cheap.</summary>
    public const int CompactAfter = 64;

    /// <summary>
    /// Makes the batches of a conversation one file when there are many (one is written at each pass of a collector, and a long conversation has
    /// thousands): the first file takes in all the lines, and the others are removed. A crash in between leaves files that
    /// <see cref="Read"/> ignores.
    /// </summary>
    public void CompactIfNeeded(string machine, string session, long count)
    {
        var folder = FolderOf(machine, session);
        var files = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.jsonl.gz").Order(StringComparer.Ordinal).ToList() : [];
        if (files.Count < CompactAfter)
        {
            return;
        }

        // Written as it is read, one line at a time: a long conversation is never held whole in memory.
        Write(machine, session, 0, Read(machine, session, count));
        var first = Path.Combine(folder, $"{0:D12}.jsonl.gz");
        foreach (var file in files.Where(file => !string.Equals(file, first, StringComparison.Ordinal)))
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Files the lines of a conversation under another machine. Nothing is done when there are none. Lines under both names are not for this code to choose
    /// between (a move that was stopped half way is finished by running it again, which finds them under the new name only): it says so, and nothing moves.
    /// </summary>
    public void Move(string machine, string session, string toMachine)
    {
        var from = FolderOf(machine, session);
        var to = FolderOf(toMachine, session);
        if (!Directory.Exists(from))
        {
            return;
        }

        if (Directory.Exists(to))
        {
            throw new IOException($"The lines of conversation {session} are kept under {machine} and under {toMachine}: nothing was moved.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Directory.Move(from, to);
        RemoveIfEmpty(Path.GetDirectoryName(from)!);
    }

    /// <summary>Deletes the lines of a conversation, and the folder of the machine when it is left empty.</summary>
    public void Delete(string machine, string session)
    {
        var folder = FolderOf(machine, session);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        RemoveIfEmpty(Path.GetDirectoryName(folder)!);
    }

    private static void RemoveIfEmpty(string folder)
    {
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
        }
    }

    /// <summary>The first <paramref name="count"/> lines of a conversation, in order. A batch that was written for a position that was never taken is not read.</summary>
    public IEnumerable<string> Read(string machine, string session, long count)
    {
        var folder = FolderOf(machine, session);
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        long position = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.jsonl.gz").Order(StringComparer.Ordinal))
        {
            if (position >= count || !long.TryParse(Path.GetFileName(file).Split('.')[0], out var start) || start > position)
            {
                yield break;
            }

            if (start < position)
            {
                // A batch that a compaction already took in: it is left over, and said nothing new.
                continue;
            }

            using var stream = File.OpenRead(file);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            while (position < count && reader.ReadLine() is { } line)
            {
                position++;
                yield return line;
            }
        }
    }
}

/// <summary>What the raw lines of a conversation are: <see cref="Format"/> names the parser that reads them.</summary>
public sealed record RawInfo(string Format, string Application, DateTimeOffset StartedAt);
