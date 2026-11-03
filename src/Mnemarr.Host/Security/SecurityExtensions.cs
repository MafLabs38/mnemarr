using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
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
                ? new FileSystemXmlRepository(PrivateFolder(Path.Combine(mnemarr.Value.DataFolder, "keys")), logger)
                : new MemoryXmlRepository();
        });

        services.AddAuthentication(PolicyScheme)
            .AddPolicyScheme(PolicyScheme, displayName: null, options =>
                options.ForwardDefaultSelector = context => ApiKeyHeader.Read(context.Request) is null ? AuthController.CookieScheme : ApiKeyHeader.Scheme)
            .AddCookie(AuthController.CookieScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyHeader.Scheme, _ => { });

        services.AddOptions<CookieAuthenticationOptions>(AuthController.CookieScheme).Configure<IOptions<AuthOptions>>((cookie, auth) => ConfigureCookie(cookie, auth.Value));

        services.AddHttpContextAccessor();
        services.AddSingleton<IClaimsTransformation, AccountClaimsTransformation>();
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            foreach (var permission in Permissions.All)
            {
                options.AddPolicy(permission, policy => policy.RequireClaim(MnemarrClaims.Permission, permission));
            }
        });
        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions cookie, AuthOptions auth)
    {
        // The __Host- prefix makes the browser refuse a cookie of that name set by a sibling host or with a Domain: it needs Secure.
        cookie.Cookie.Name = auth.CookieSecurity == CookieSecurityMode.Always ? "__Host-mnemarr.session" : "mnemarr.session";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Strict;
        cookie.Cookie.SecurePolicy = auth.CookieSecurity == CookieSecurityMode.Always ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(auth.SessionHours);
        cookie.SlidingExpiration = true;
        cookie.Events.OnValidatePrincipal = ValidateSession;
        // An API answers with a status, never with a redirect to a login page.
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
    }

    /// <summary>
    /// A cookie is valid while its session is: signing out, a new password and the end of the absolute lifetime end the session on the
    /// server, and a cookie made from a copy of the key ring has no session behind it. An account that is gone or disabled has none either.
    /// </summary>
    private static Task ValidateSession(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var accountId = context.Principal?.AccountId();
        var sessionId = context.Principal?.FindFirst(MnemarrClaims.Session)?.Value;
        var account = accountId is { } id ? services.GetRequiredService<IAccountStore>().Get(id) : null;
        if (account is not { Enabled: true, Kind: AccountKind.User } || sessionId is null
            || services.GetRequiredService<SessionService>().AccountOf(sessionId) != account.Id)
        {
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        context.HttpContext.Items[AccountClaimsTransformation.AccountItem] = account;
        return Task.CompletedTask;
    }

    /// <summary>The folder of the key ring: a credential (a copy of it, with an account id, is a cookie), so only its owner may read it.</summary>
    private static DirectoryInfo PrivateFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Directory.CreateDirectory(path);
        }

        var folder = Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // A folder that was already there (a restored backup, a mounted volume) keeps the mode it had unless it is set again.
        folder.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        return folder;
    }

    public static WebApplication UseMnemarrSecurity(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseMiddleware<RequestIntegrityMiddleware>();
        app.UseAuthorization();
        return app;
    }
}
