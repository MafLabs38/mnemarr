namespace Mnemarr.Core.Configuration;

/// <summary>Settings of the MCP server, from the <c>Mcp</c> section (see docs/configuration.md and ADR 0023).</summary>
public sealed class McpOptions
{
    /// <summary>
    /// Whether the tools that write notes (create, change, move, remove) are offered to assistants. Off by default, and a setting of the
    /// environment, not of the database or of the API: the door that decides whether an assistant may write must not be one the assistant can open.
    /// </summary>
    public bool WriteTools { get; set; }

    /// <summary>
    /// Whether the tools that look after sources (declare, change, forget, read again, pull) are offered as well, when <see cref="WriteTools"/> is on (ADR 0035, D-20). Off by default and
    /// apart from the notes' switch: an operator who let assistants write notes has not thereby let them declare or forget sources.
    /// </summary>
    public bool SourceTools { get; set; }
}
