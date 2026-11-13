using Microsoft.AspNetCore.Authorization;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Api.V1.Security;

/// <summary>The policies that are a rule of who the caller is, besides the ones of the permissions (one for each, ADR 0016).</summary>
public static class AccessPolicies
{
    /// <summary>
    /// Who knows which environment this is (ADR 0044): a person who is signed in, and a key that may read the status of the system. A key that does other work (a collector's, an assistant's) may
    /// be held by a machine on the Internet and has no reason to learn the name, the picture or the version.
    /// </summary>
    public const string EnvironmentRead = "environment.read";

    public static void Add(AuthorizationOptions options) =>
        options.AddPolicy(EnvironmentRead, policy => policy.RequireAssertion(context =>
            context.User.HasClaim(MnemarrClaims.Permission, Permissions.SystemRead) || context.User.FindFirst(MnemarrClaims.Kind)?.Value == nameof(AccountKind.User)));
}
