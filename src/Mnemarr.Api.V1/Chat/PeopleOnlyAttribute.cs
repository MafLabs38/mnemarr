using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Api.V1.Chat;

/// <summary>
/// The chat is for people (ADR 0035): the model runs on the operator's subscription and acts with the token of a person. A key of a service whose role holds <c>chat.use</c> (Operator and Reader
/// may be given to services) passes the permission but is refused here: the token would not be given to its model, and its turns would cost the subscription for nothing.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PeopleOnlyAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.HttpContext.User.Person().Kind != AccountKind.User)
        {
            context.Result = new ObjectResult(new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = "The chat is for people: a key of a service cannot use it." }) { StatusCode = StatusCodes.Status403Forbidden };
        }
    }
}
