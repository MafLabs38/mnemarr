using System.Text;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Core.Test.Collectors;

public sealed class EmbeddedSettingFixture
{
    private static readonly EmbeddedSetting Setting = new("https://mnemarr.example", "pc", "ABCD-EFGH-JKLM", Insecure: false);
    private const string Json = """{"server":"https://mnemarr.example","machine":"pc","code":"ABCD-EFGH-JKLM","insecure":false}""";

    [Fact]
    public void should_write_the_setting_after_the_program_with_its_length_and_a_mark_at_the_very_end()
    {
        var program = Encoding.ASCII.GetBytes("PROG\0ÿ");

        var bytes = Setting.AppendTo(program);

        var json = Encoding.UTF8.GetBytes(Json);
        var expected = program.Concat(json).Concat(BitConverter.GetBytes((uint)json.Length)).Concat(Encoding.ASCII.GetBytes("MNMRCFG1")).ToArray();
        bytes.Should().Equal(expected, "the collector reads exactly this: the same bytes are read by its own tests");
        bytes.Take(program.Length).Should().Equal(program, "the program is untouched");
    }

    [Fact]
    public void should_say_whether_plain_http_was_accepted()
    {
        var bytes = (Setting with { Insecure = true }).AppendTo([1, 2, 3]);

        Encoding.UTF8.GetString(bytes).Should().Contain("\"insecure\":true");
    }
}
