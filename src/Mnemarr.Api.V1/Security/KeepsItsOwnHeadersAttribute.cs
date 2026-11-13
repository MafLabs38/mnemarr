namespace Mnemarr.Api.V1.Security;

/// <summary>
/// Marks an endpoint whose successful answer says its own cache and content policy, and keeps them (ADR 0044): the logo of the identity, an image that is the same until its hash changes.
/// The host gives every other answer, and an error of this endpoint, its general headers (no cache, the application's policy).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class KeepsItsOwnHeadersAttribute : Attribute;
