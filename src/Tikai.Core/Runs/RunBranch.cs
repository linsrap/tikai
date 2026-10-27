using System.Text;
using Tikai.Core.Tickets;

namespace Tikai.Core.Runs;

/// <summary>The branch of a run: tikai/&lt;KEY-N&gt;-&lt;run8&gt;, created from the repository's default branch.</summary>
public static class RunBranch
{
    /// <summary>
    /// Uses the last 8 hex digits of the run id: a version 7 GUID starts with its timestamp, so its first digits are the
    /// same for runs created within about a minute.
    /// </summary>
    public static string Name(string ticketKey, Guid runId) => $"tikai/{ticketKey}-{runId.ToString("N")[^8..]}";
}

/// <summary>The ticket as the agent finds it in its clone, .tikai/ticket.md (read-only, excluded from git).</summary>
public static class TicketDocument
{
    public static string Render(string ticketKey, string title, string description, IEnumerable<AcceptanceCriterion> criteria)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(ticketKey).Append(": ").Append(title).Append("\n\n");
        text.Append("<!-- A read-only copy for this run. The ticket in Tikai (MCP tool get_ticket) is the source of truth. -->\n\n");
        if (!string.IsNullOrWhiteSpace(description))
        {
            text.Append(description.Replace("\r\n", "\n", StringComparison.Ordinal).Trim()).Append("\n\n");
        }
        text.Append("## Acceptance criteria\n\n");
        var any = false;
        foreach (var criterion in criteria.OrderBy(c => c.Position))
        {
            text.Append("- ").Append(criterion.Text).Append(" (id `").Append(criterion.Id.ToString("D")).Append("`)\n");
            any = true;
        }
        if (!any)
        {
            text.Append("None yet.\n");
        }
        return text.ToString();
    }
}
