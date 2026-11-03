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
            AccountProblem.Locked => StatusCodes.Status429TooManyRequests,
            _ => throw new InvalidOperationException($"No status is chosen for {rejected.Problem}."),
        };
        if (rejected.RetryAfter is { } retryAfter)
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = rejected.Message }) { StatusCode = status, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }
}
