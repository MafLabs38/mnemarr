using System.Text;
using System.Text.Json;

namespace Mnemarr.Core.Collectors;

/// <summary>What a program downloaded from Mnemarr carries: where to join, as whom, with which code (ADR 0027 §15).</summary>
public sealed record EmbeddedSetting(string Server, string Machine, string Code, bool Insecure)
{
    /// <summary>The last eight bytes of a program that carries a setting.</summary>
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MNMRCFG1");

    /// <summary>
    /// The program with the setting written after it: <c>[program][json][length: uint32, little endian]["MNMRCFG1"]</c>. The program does not look at
    /// what follows it, so it runs as it was; opened with nothing to say, it reads the setting and sets itself up.
    /// </summary>
    public byte[] AppendTo(byte[] program) => [.. program, .. Trailer()];

    /// <summary>What follows the program: the setting, its length and the mark. Written after a program that is streamed, so that the program is never held whole in memory.</summary>
    public byte[] Trailer()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { server = Server, machine = Machine, code = Code, insecure = Insecure });
        var length = BitConverter.GetBytes((uint)json.Length);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(length);
        }

        return [.. json, .. length, .. Magic];
    }
}
