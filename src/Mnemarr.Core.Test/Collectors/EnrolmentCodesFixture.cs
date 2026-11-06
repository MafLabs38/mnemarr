using Mnemarr.Core.Collectors;

namespace Mnemarr.Core.Test.Collectors;

public sealed class EnrolmentCodesFixture
{
    [Fact]
    public void should_make_codes_of_twelve_characters_in_three_groups_from_an_alphabet_without_look_alikes()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => EnrolmentCodes.New()).ToList();

        codes.Should().OnlyContain(code => code.Length == 14 && code[4] == '-' && code[9] == '-');
        codes.SelectMany(code => code.Where(c => c != '-')).Should().OnlyContain(c => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c, StringComparison.Ordinal));
        codes.Distinct().Should().HaveCount(codes.Count, "a code is not one that was made before");
    }

    [Theory]
    [InlineData("ABCD-EFGH-JKLM", "ABCDEFGHJKLM")]
    [InlineData("abcd efgh jklm", "ABCDEFGHJKLM")]
    [InlineData("  abcd-efgh-jklm \n", "ABCDEFGHJKLM")]
    [InlineData("ABCDEFGHJKLM", "ABCDEFGHJKLM")]
    [InlineData("ABCD-EFGH-JKL", null)]
    [InlineData("ABCD-EFGH-JKLMN", null)]
    [InlineData("ABCD-EFGH-JKL0", null)]
    [InlineData("ABCD-EFGH-JKLI", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void should_bring_what_a_person_typed_back_to_the_code_or_refuse_it(string? typed, string? expected) =>
        EnrolmentCodes.Normalise(typed).Should().Be(expected);

    [Fact]
    public void should_hash_a_code_so_that_the_same_code_gives_the_same_hash_and_the_hash_is_not_the_code()
    {
        var hash = EnrolmentCodes.Hash("ABCDEFGHJKLM");

        hash.Should().Be(EnrolmentCodes.Hash("ABCDEFGHJKLM")).And.NotContain("ABCD").And.NotBe(EnrolmentCodes.Hash("ABCDEFGHJKLN"));
    }

    [Theory]
    [InlineData("pc", true)]
    [InlineData("WIN-PC_1.local", true)]
    [InlineData("a", true)]
    [InlineData("-pc", false)]
    [InlineData("my pc", false)]
    [InlineData("pc/../x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void should_accept_as_a_machine_name_only_what_the_ingest_accepts(string? name, bool valid) =>
        MachineNames.IsValid(name).Should().Be(valid);

    [Fact]
    public void should_refuse_a_machine_name_too_long_to_name_its_account()
    {
        MachineNames.IsValid(new string('a', MachineNames.MaxLength)).Should().BeTrue();
        MachineNames.IsValid(new string('a', MachineNames.MaxLength + 1)).Should().BeFalse();
    }
}

public sealed class EnrolmentThrottleFixture
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void should_refuse_after_ten_failures_in_a_minute_and_forget_them_when_the_minute_is_over()
    {
        var throttle = new EnrolmentThrottle();
        for (var i = 0; i < 10; i++)
        {
            throttle.Check(Now.AddSeconds(i));
            throttle.Failed(Now.AddSeconds(i));
        }

        var refused = Assert.Throws<RejectedException>(() => throttle.Check(Now.AddSeconds(20)));
        refused.Problem.Should().Be(Problem.Busy);
        refused.RetryAfter.Should().Be(TimeSpan.FromMinutes(1));
        throttle.Check(Now.AddSeconds(75));
    }
}
