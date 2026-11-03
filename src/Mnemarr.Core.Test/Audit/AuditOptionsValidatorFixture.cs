using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Test.Audit;

public sealed class AuditOptionsValidatorFixture
{
    private static string? Failure(AuditOptions options) => new AuditOptionsValidator().Validate(null, options).FailureMessage;

    [Fact]
    public void should_accept_the_default_retention()
    {
        Failure(new AuditOptions()).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AuditOptions.MaxRetentionDays + 1)]
    public void should_refuse_a_retention_out_of_range(int days)
    {
        Failure(new AuditOptions { RetentionDays = days }).Should().Contain("MNEMARR__AUDIT__RETENTIONDAYS");
    }
}
