using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Context;

namespace Mnemarr.Api.V1.Context;

/// <summary>The context of a new session: <c>Text</c> is what an assistant is told (empty when the memory knows nothing of the application), with how many notes and conversations it draws on.</summary>
public sealed record ContextResource(string Text, int Notes, int Conversations);

/// <summary>What an assistant is told when it opens a session in an application (D-12, ADR 0037): the consolidated notes of the memory vault, then the latest conversations, within a budget.</summary>
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

    /// <summary>The context for an application, as far as the caller's key reaches: a key that may not read conversations is not told of them, and a key limited to some sources gets only those.</summary>
    /// <param name="application">The name of the application: the last folder of the working directory.</param>
    /// <param name="budget">At most this many characters (500 to 20000, default 6000).</param>
    [HttpGet]
    [Authorize(Policy = Permissions.NotesRead)]
    [ProducesResponseType(typeof(ContextResource), StatusCodes.Status200OK)]
    public IActionResult Get([FromQuery] string? application, [FromQuery] int? budget)
    {
        if (string.IsNullOrWhiteSpace(application) || application.Length > MaxApplicationChars || application.Any(char.IsControl))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"The application is a name of 1 to {MaxApplicationChars} characters.");
        }

        var pack = _builder.Build(application.Trim(), budget ?? ContextPackBuilder.DefaultBudget, User.Scope());
        return Ok(new ContextResource(pack.Text, pack.Notes, pack.Conversations));
    }
}
