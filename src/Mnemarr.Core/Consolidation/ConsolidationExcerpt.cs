using System.Globalization;
using System.Text;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Core.Consolidation;

/// <summary>What one run reads of a conversation: the turns after those already consolidated, as many as fit, and where that leaves the reading.</summary>
public sealed record ConsolidationExcerpt(string Text, int Taken, int Total)
{
    /// <summary>The turns that are left for a later run.</summary>
    public int Remaining(int done) => Math.Max(0, Total - done - Taken);
}

public static class ConsolidationExcerpts
{
    /// <summary>A single turn is cut here: a pasted file or a long answer is not worth more of the budget than this.</summary>
    public const int MaxTurnChars = 4000;

    /// <summary>
    /// The turns after the first <paramref name="done"/>, from the start of the next one, until <paramref name="maxChars"/> is reached. At least one turn is taken when there is one, so that a run
    /// always moves forward. A turn with no text (a call of a tool and nothing said) is counted as read and not written.
    /// </summary>
    public static ConsolidationExcerpt Take(IReadOnlyList<Turn> turns, int done, int maxChars)
    {
        var text = new StringBuilder();
        var taken = 0;
        for (var index = Math.Max(0, done); index < turns.Count; index++)
        {
            var piece = Render(turns[index]);
            if (taken > 0 && text.Length + piece.Length > maxChars)
            {
                break;
            }

            text.Append(piece);
            taken++;
        }

        return new ConsolidationExcerpt(text.ToString(), taken, turns.Count);
    }

    private static string Render(Turn turn)
    {
        if (string.IsNullOrWhiteSpace(turn.Text))
        {
            return string.Empty;
        }

        var body = turn.Text.Length > MaxTurnChars ? turn.Text[..MaxTurnChars] + "\n[... cut]" : turn.Text;
        var when = turn.At is { } at ? string.Create(CultureInfo.InvariantCulture, $" · {at:yyyy-MM-dd HH:mm}") : string.Empty;
        return $"[{turn.Role}{when}]\n{body.Trim()}\n\n";
    }
}
