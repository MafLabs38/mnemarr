using System.IO.Compression;
using System.Text;
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

    private string FolderOf(string machine, string session) => Path.Combine(Path.GetFullPath(_options.Value.DataFolder), "raw", machine, session);

    /// <summary>Keeps a batch that starts at the given position. Written beside and moved into place, so that a crash leaves no half file.</summary>
    public void Write(string machine, string session, long from, IReadOnlyList<string> lines)
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

        Write(machine, session, 0, [.. Read(machine, session, count)]);
        var first = Path.Combine(folder, $"{0:D12}.jsonl.gz");
        foreach (var file in files.Where(file => !string.Equals(file, first, StringComparison.Ordinal)))
        {
            File.Delete(file);
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
