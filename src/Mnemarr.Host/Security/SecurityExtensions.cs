using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using Mnemarr.Api.V1.Accounts;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Configuration;

namespace Mnemarr.Host.Security;

public static class SecurityExtensions
{
    private const string PolicyScheme = "Mnemarr";

    /// <summary>
    /// Sign-in by cookie for people and by API key for services and assistants, one policy per permission, and a fallback policy that
    /// requires a sign-in everywhere else: an endpoint nobody thought about is closed (ADR 0016).
    /// </summary>
    public static IServiceCollection AddMnemarrSecurity(this IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName("Mnemarr");
        services.AddOptions<KeyManagementOptions>().Configure<IOptions<MnemarrOptions>, ILoggerFactory>((keys, mnemarr, logger) =>
        {
            // Creating the data folder at start would hide a volume that was never mounted from /health/ready: without the folder,
            // the keys stay in memory.
            keys.XmlRepository = Directory.Exists(mnemarr.Value.DataFolder)
                ? new FileSystemXmlRepository(new DirectoryInfo(Path.Combine(mnemarr.Value.DataFolder, "keys")), logger)
                : new MemoryXmlRepository();
        });

        services.AddAuthentication(PolicyScheme)
            .AddPolicyScheme(PolicyScheme, displayName: null, options =>
                options.ForwardDefaultSelector = context => ApiKeyHeader.Read(context.Request) is null ? AuthController.CookieScheme : ApiKeyHeader.Scheme)
            .AddCookie(AuthController.CookieScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyHeader.Scheme, _ => { });

        services.AddOptions<CookieAuthenticationOptions>(AuthController.CookieScheme).Configure<IOptions<AuthOptions>>((cookie, auth) =>
        {
            cookie.Cookie.Name = "mnemarr.session";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = auth.Value.CookieSecurity == "Always" ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            cookie.ExpireTimeSpan = TimeSpan.FromHours(auth.Value.SessionHours);
            cookie.SlidingExpiration = true;
            // An API answers with a status, never with a redirect to a login page.
            // A session that belongs to an account that is gone or disabled is no session: a 401, at once.
            cookie.Events.OnValidatePrincipal = context =>
            {
                var accounts = context.HttpContext.RequestServices.GetRequiredService<IAccountStore>();
                var id = context.Principal?.AccountId();
                if (id is null || accounts.Get(id.Value) is not { Enabled: true, Kind: AccountKind.User })
                {
                    context.RejectPrincipal();
                }

                return Task.CompletedTask;
            };
            cookie.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            cookie.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        services.AddSingleton<IClaimsTransformation, AccountClaimsTransformation>();
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            foreach (var permission in Permissions.All)
            {
                options.AddPolicy(permission, policy => policy.RequireClaim(Mnemarr.Api.V1.Security.MnemarrClaims.Permission, permission));
            }
        });
        return services;
    }

    public static WebApplication UseMnemarrSecurity(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseMiddleware<RequestIntegrityMiddleware>();
        app.UseAuthorization();
        return app;
    }
}
