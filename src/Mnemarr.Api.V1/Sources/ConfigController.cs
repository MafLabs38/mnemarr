using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Declarative;

namespace Mnemarr.Api.V1.Sources;

/// <summary>
/// Configuration as code (ADR 0020): the sources as a YAML document, to read and to apply. The same document can be a file that is applied
/// at start. What a document owns is marked as managed, and is changed by changing the document.
/// </summary>
[ApiController]
[Route("api/v1/config")]
[Authorize(Policy = Permissions.SourcesManage)]
public sealed class ConfigController : ControllerBase
{
    private const string Yaml = "application/yaml";
    private readonly ConfigurationApplier _applier;
    private readonly IOptions<ConfigurationOptions> _options;

    public ConfigController(ConfigurationApplier applier, IOptions<ConfigurationOptions> options)
    {
        _applier = applier;
        _options = options;
    }

    /// <summary>
    /// The current state as a configuration document: what to keep in a repository, and what applying it back leaves unchanged once every source is managed (a source made by hand is taken over by it). A key
    /// limited to some sources gets no export: a document with only some of the sources would remove the others when it is applied back.
    /// </summary>
    [HttpGet]
    [Produces(Yaml)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult GetConfig() =>
        User.Scope().Sources is not null
            ? Problem(statusCode: StatusCodes.Status403Forbidden, title: "A key limited to some sources cannot export the configuration: it is global.")
            : Content(_applier.Export(User.Scope()), Yaml);

    /// <summary>
    /// Applies a configuration document, or with <c>dryRun=true</c> only says what it would do. A document with an error changes nothing and
    /// is answered 400 with every error. A configuration is global: a key limited to some sources cannot apply one. When Mnemarr is given a
    /// configuration file (<c>Config:File</c>), the file is the one document: it cannot be applied here (409), only previewed, so that the
    /// file never stops describing the system.
    /// </summary>
    [HttpPut]
    [RequestSizeLimit(ConfigurationYaml.MaxBytes + 4096)]
    [ProducesResponseType<ConfigPlanResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<IActionResult> PutConfig([FromQuery] bool dryRun = false, CancellationToken cancellationToken = default)
    {
        if (Request.ContentType is { } type && !type.StartsWith(Yaml, StringComparison.OrdinalIgnoreCase) && !type.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "A configuration is sent as application/yaml (or text/plain).");
        }

        if (User.Scope().Sources is not null)
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "A key limited to some sources cannot apply a configuration: it is global.");
        }

        if (!dryRun && !string.IsNullOrWhiteSpace(_options.Value.File))
        {
            throw new RejectedException(Core.Problem.Conflict, "The configuration is given to Mnemarr as a file: change the file instead. A preview (dryRun=true) is still possible.");
        }

        using var reader = new StreamReader(Request.Body);
        var document = ConfigurationYaml.Parse(await reader.ReadToEndAsync(cancellationToken));
        var plan = dryRun ? _applier.Plan(document) : _applier.Apply(document, User.ActorName());
        if (!plan.Valid)
        {
            var problem = new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = "The configuration cannot be applied: nothing was changed.", Type = "urn:mnemarr:problem:invalid" };
            problem.Extensions["errors"] = plan.Errors;
            return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest, ContentTypes = { "application/problem+json" } };
        }

        return Ok(ConfigPlanResource.From(plan, applied: !dryRun));
    }
}
