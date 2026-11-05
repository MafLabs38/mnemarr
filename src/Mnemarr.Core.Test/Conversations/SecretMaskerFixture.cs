using System.Text.Json;
using System.Text.RegularExpressions;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Core.Test.Conversations;

/// <summary>The masking gives the answers of the file of samples that the collector's masking is run against too: the two engines must not drift (Q-27).</summary>
public sealed partial class SecretMaskerFixture
{
    [GeneratedRegex(@"<([AUD]):(\d+)>")]
    private static partial Regex Marker();

    /// <summary>A marker stands for a run of characters, so that the file holds nothing a secret scanner would take for a secret.</summary>
    private static string Expand(string text) =>
        Marker().Replace(text, match => new string(match.Groups[1].Value switch { "A" => 'a', "U" => 'A', _ => '-' }, int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));

    public static TheoryData<string> Names()
    {
        var names = new TheoryData<string>();
        foreach (var sample in Samples().EnumerateArray())
        {
            names.Add(sample.GetProperty("name").GetString()!);
        }

        return names;
    }

    private static JsonElement Samples()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "masking", "samples.json")));
        return document.RootElement.GetProperty("samples").Clone();
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void should_give_the_answer_of_the_shared_samples(string name)
    {
        var sample = Samples().EnumerateArray().Single(candidate => candidate.GetProperty("name").GetString() == name);

        SecretMasker.Mask(Expand(sample.GetProperty("input").GetString()!)).Should().Be(Expand(sample.GetProperty("expected").GetString()!));
    }

    [Fact]
    public void should_keep_a_json_line_valid_when_it_hides_a_secret_in_a_string()
    {
        var line = "{\"type\":\"user\",\"message\":{\"content\":\"export PASSWORD=hunter2hunter2 and ghp_" + new string('a', 36) + "\"}}";

        using var document = JsonDocument.Parse(SecretMasker.Mask(line));

        document.RootElement.GetProperty("message").GetProperty("content").GetString().Should().Be("export PASSWORD=[masked] and [masked:github-token]");
    }
}
