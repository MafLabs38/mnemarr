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

        var (status, kind) = Describe(rejected.Problem);
        if (rejected.RetryAfter is { } retryAfter)
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = rejected.Message, Type = $"urn:mnemarr:problem:{kind}" }) { StatusCode = status, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }

    /// <summary>
    /// The status of a refusal, and its stable name for clients that must tell "locked for a while" from "busy, try in a moment" without
    /// reading English. One table: a refusal added to <see cref="AccountProblem"/> and not here is caught by a test over every value.
    /// </summary>
    public static (int Status, string Kind) Describe(AccountProblem problem) => problem switch
    {
        AccountProblem.Invalid => (StatusCodes.Status400BadRequest, "invalid"),
        AccountProblem.Conflict => (StatusCodes.Status409Conflict, "conflict"),
        AccountProblem.NotFound => (StatusCodes.Status404NotFound, "not-found"),
        AccountProblem.Locked => (StatusCodes.Status429TooManyRequests, "locked"),
        AccountProblem.Busy => (StatusCodes.Status429TooManyRequests, "busy"),
        _ => throw new InvalidOperationException($"No status is chosen for {problem}."),
    };
}
