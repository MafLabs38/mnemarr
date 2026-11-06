using Microsoft.OpenApi;
using Mnemarr.Api.V1;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.SystemInfo;
using Mnemarr.Host.Health;
using Mnemarr.Host.Http;
using Mnemarr.Host.Security;

// The content root is the binary's folder, whatever the working directory (like the *arr apps).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Settings come from appsettings.json, then environment variables prefixed with MNEMARR__.
builder.Configuration.AddEnvironmentVariables(prefix: "MNEMARR__");

builder.Services.AddMnemarrCore(builder.Configuration);
builder.Services.AddMnemarrHealthChecks();
builder.Services.AddMnemarrSecurity();
builder.Services.AddMnemarrApiV1();
builder.Services.AddMnemarrRateLimits();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ClientErrorExceptionHandler>();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info = new OpenApiInfo
    {
        Title = "Mnemarr API",
        Version = "v1",
        Description = "The REST API of Mnemarr, a long-term memory for people and AI assistants. The interface, the agents and the MCP server all use it.",
    };
    return Task.CompletedTask;
}));

var app = builder.Build();

// Singletons are created on first use: resolve the status service now, so that "started at" is the start of
// the application and not the time of the first request for its status.
_ = app.Services.GetRequiredService<ISystemStatusService>();
app.Services.GetRequiredService<AdminAccountSeeder>().Run();

app.UseExceptionHandler();
app.UseMnemarrSecurityHeaders();
app.UseStatusCodePages();
app.UseMnemarrFrontendFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseMnemarrSecurity();
app.MapControllers();
app.MapOpenApi();
app.MapMnemarrHealthChecks();
app.MapMnemarrFrontendFallback();

await app.RunAsync();

/// <summary>Entry point, exposed for integration tests (WebApplicationFactory).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1118:Utility classes should not have public constructors", Justification = "WebApplicationFactory<Program> needs a public type to start the host; it is not a utility class.")]
public partial class Program;
