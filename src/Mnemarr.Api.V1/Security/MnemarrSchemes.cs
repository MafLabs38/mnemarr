namespace Mnemarr.Api.V1.Security;

/// <summary>The names of the authentication schemes: part of the security wiring, shared by the host that adds them and the calls that sign in.</summary>
public static class MnemarrSchemes
{
    /// <summary>The session cookie of a person.</summary>
    public const string Cookie = "Mnemarr.Cookie";
}
