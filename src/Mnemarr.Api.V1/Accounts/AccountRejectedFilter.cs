using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Api.V1.Accounts;

/// <summary>Turns a refused account operation into a problem document with the right status, instead of a 500.</summary>
public sealed class AccountRejectedFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not AccountRejectedException rejected)
        {
            return;
        }

        var status = rejected.Problem switch
        {
            AccountProblem.Invalid => StatusCodes.Status400BadRequest,
            AccountProblem.Conflict => StatusCodes.Status409Conflict,
            AccountProblem.NotFound => StatusCodes.Status404NotFound,
            AccountProblem.Locked or AccountProblem.Busy => StatusCodes.Status429TooManyRequests,
            _ => throw new InvalidOperationException($"No status is chosen for {rejected.Problem}."),
        };
        if (rejected.RetryAfter is { } retryAfter)
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = rejected.Message, Type = $"urn:mnemarr:problem:{ProblemKind(rejected.Problem)}" }) { StatusCode = status, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }

    /// <summary>The stable name of a refusal, for clients that must tell "locked for a while" from "busy, try in a moment" without reading English.</summary>
    private static string ProblemKind(AccountProblem problem) => problem switch
    {
        AccountProblem.Invalid => "invalid",
        AccountProblem.Conflict => "conflict",
        AccountProblem.NotFound => "not-found",
        AccountProblem.Locked => "locked",
        AccountProblem.Busy => "busy",
        _ => throw new InvalidOperationException($"No name is chosen for {problem}."),
    };
}
