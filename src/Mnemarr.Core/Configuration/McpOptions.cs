namespace Mnemarr.Core.Configuration;

/// <summary>Settings of the MCP server, from the <c>Mcp</c> section (see docs/configuration.md and ADR 0023).</summary>
public sealed class McpOptions
{
    /// <summary>
    /// Whether the tools that write notes (create, change, move, remove) are offered to assistants. Off by default, and a setting of the
    /// environment, not of the database or of the API: the door that decides whether an assistant may write must not be one the assistant can open.
    /// </summary>
    public bool WriteTools { get; set; }
}
