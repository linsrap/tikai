using System.Text;
using Tikai.Core.Git;
using Tikai.Core.Projects;

namespace Tikai.Core.Reviews;

/// <summary>The message of the commit that brings an approved change into the default branch.</summary>
public static class MergeMessage
{
    public const int MaxSummaryLength = 4_000;

    // Trailers the hub writes itself; such lines from the agent's summary are left out, so it cannot add a co-author.
    private static readonly string[] _ownTrailers = ["ticket:", "co-authored-by:", "signed-off-by:"];

    /// <summary>
    /// "HUB-42: Title" (with "Merge " in front for a merge commit), the agent's summary, then the Ticket and
    /// Co-authored-by trailers as the last paragraph.
    /// </summary>
    public static string Build(string ticketKey, string title, string? summary, GitIdentity approver, MergeStyle style)
    {
        var subject = $"{ticketKey}: {OneLine(title)}";
        var text = new StringBuilder(style == MergeStyle.MergeCommit ? "Merge " + subject : subject).Append("\n\n");
        var body = Body(summary);
        if (body.Length > 0)
        {
            text.Append(body).Append("\n\n");
        }
        var who = approver.Clean();
        text.Append("Ticket: ").Append(ticketKey).Append('\n');
        text.Append("Co-authored-by: ").Append(who.Name).Append(" <").Append(who.Email).Append(">\n");
        return text.ToString();
    }

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Body(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "";
        }
        var text = summary.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (text.Length > MaxSummaryLength)
        {
            text = text[..MaxSummaryLength].TrimEnd() + "…";
        }
        var lines = text.Split('\n')
            .Where(line => !_ownTrailers.Any(t => line.TrimStart().StartsWith(t, StringComparison.OrdinalIgnoreCase)))
            .Select(line => line.TrimEnd());
        return string.Join('\n', lines).Trim();
    }
}
