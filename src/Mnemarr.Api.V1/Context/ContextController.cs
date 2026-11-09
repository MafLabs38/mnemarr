using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Context;

namespace Mnemarr.Api.V1.Context;

/// <summary>The context of a new session: <c>Text</c> is what an assistant is told (empty when the memory knows nothing of the application), and whether it holds the thread of the work and the end of the latest conversation.</summary>
public sealed record ContextResource(string Text, bool Thread, bool Tail);

/// <summary>What an assistant is told when it opens a session in an application (D-12, D-59, ADR 0038): the thread of the work, then the end of the latest conversation, within a budget.</summary>
[ApiController]
[Route("api/v1/context")]
public sealed class ContextController : ControllerBase
{
    private const int MaxApplicationChars = 100;

    private readonly ContextPackBuilder _builder;

    public ContextController(ContextPackBuilder builder)
    {
        _builder = builder;
    }

    /// <summary>The context for an application, as far as the caller's key reaches: a key that may not read conversations is told the thread only, and a key limited to some sources gets only those.</summary>
    /// <param name="application">The name of the application: the last folder of the working directory.</param>
    /// <param name="budget">At most this many characters (1000 to 20000, default 6000).</param>
    /// <param name="exclude">The session that is starting (the hook gives it): it is not told its own beginning.</param>
    [HttpGet]
    [Authorize(Policy = Permissions.NotesRead)]
    [ProducesResponseType(typeof(ContextResource), StatusCodes.Status200OK)]
    public IActionResult Get([FromQuery] string? application, [FromQuery] int? budget, [FromQuery] string? exclude)
    {
        if (string.IsNullOrWhiteSpace(application) || application.Length > MaxApplicationChars || application.Any(char.IsControl))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"The application is a name of 1 to {MaxApplicationChars} characters.");
        }

        if (exclude is { Length: > MaxApplicationChars } || exclude?.Any(char.IsControl) == true)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"The session to leave out is a name of at most {MaxApplicationChars} characters.");
        }

        var pack = _builder.Build(application.Trim(), budget ?? ContextPackBuilder.DefaultBudget, User.Scope(), string.IsNullOrWhiteSpace(exclude) ? null : exclude.Trim());
        return Ok(new ContextResource(pack.Text, pack.Thread, pack.Tail));
    }
}
