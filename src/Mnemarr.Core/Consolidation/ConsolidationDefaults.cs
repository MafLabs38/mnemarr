using Mnemarr.Core.Conversations;

namespace Mnemarr.Core.Consolidation;

/// <summary>
/// Whether the conversations of a machine are turned into notes of the memory (ADR 0041). A machine can say so explicitly; when it does not, the format of its conversations decides:
/// the conversations of a command-line assistant that works on projects are consolidated, and the ones of a satellite (a web assistant, an idea repository) are kept as they were said
/// and found, not turned into notes, until a person says otherwise.
/// </summary>
public static class ConsolidationDefaults
{
    /// <summary>The formats whose conversations are consolidated when the machine has not said.</summary>
    public static readonly IReadOnlyList<string> Formats = [ConversationFormats.ClaudeCode];

    public static bool ByFormat(string format) => Formats.Contains(format);

    /// <summary>Whether a machine's conversations are consolidated: what it says, else the default of the formats it sent. A machine that sent nothing yet is taken for the default.</summary>
    public static bool For(bool? explicitly, IReadOnlyList<string>? formats) => explicitly ?? (formats is null or [] || formats.Any(ByFormat));
}
