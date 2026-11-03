using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Mnemarr.Api.V1.Accounts;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Integration.Test;

/// <summary>The contract a client outside the container reads: a status, and a stable name for the reason.</summary>
public sealed class AccountRejectedFilterFixture
{
    private static ExceptionContext Handle(AccountRejectedException exception)
    {
        var http = new DefaultHttpContext();
        var context = new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), [])
        {
            Exception = exception,
        };

        new AccountRejectedFilter().OnException(context);
        return context;
    }

    [Theory]
    [InlineData(AccountProblem.Invalid, 400, "invalid")]
    [InlineData(AccountProblem.Conflict, 409, "conflict")]
    [InlineData(AccountProblem.NotFound, 404, "not-found")]
    [InlineData(AccountProblem.Locked, 429, "locked")]
    [InlineData(AccountProblem.Busy, 429, "busy")]
    public void should_answer_each_refusal_with_its_status_and_a_stable_type(AccountProblem problem, int status, string kind)
    {
        var context = Handle(new AccountRejectedException(problem, "words that may change"));

        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(status);
        result.Value.Should().BeOfType<ProblemDetails>().Which.Type.Should().Be($"urn:mnemarr:problem:{kind}");
        context.ExceptionHandled.Should().BeTrue();
    }

    [Fact]
    public void should_tell_when_to_come_back_only_for_a_refusal_that_is_for_a_while()
    {
        var busy = Handle(new AccountRejectedException(AccountProblem.Busy, "busy", TimeSpan.FromSeconds(4.2)));
        var invalid = Handle(new AccountRejectedException(AccountProblem.Invalid, "no"));

        busy.HttpContext.Response.Headers.RetryAfter.ToString().Should().Be("5");
        invalid.HttpContext.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
    }

    [Fact]
    public void should_leave_every_other_exception_alone()
    {
        var context = new ExceptionContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), []) { Exception = new InvalidOperationException() };

        new AccountRejectedFilter().OnException(context);

        context.ExceptionHandled.Should().BeFalse();
    }

    [Fact]
    public void should_have_a_status_and_a_stable_name_for_every_refusal_that_exists()
    {
        foreach (var problem in Enum.GetValues<AccountProblem>())
        {
            var (status, kind) = AccountRejectedFilter.Describe(problem);

            status.Should().BeInRange(400, 499);
            kind.Should().MatchRegex("^[a-z-]+$");
        }
    }
}
