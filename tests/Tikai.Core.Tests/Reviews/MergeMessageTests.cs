using Tikai.Core.Git;
using Tikai.Core.Projects;
using Tikai.Core.Reviews;

namespace Tikai.Core.Tests.Reviews;

public class MergeMessageTests
{
    private static readonly GitIdentity _ada = new("Ada Lovelace", "ada@example.org");

    [Fact]
    public void A_squash_names_the_ticket_carries_the_summary_and_ends_with_the_trailers()
    {
        var message = MergeMessage.Build("HUB-42", "Add a login page", "Added the form.\n\nTests in LoginTests.", _ada, MergeStyle.Squash);

        Assert.Equal("HUB-42: Add a login page\n\nAdded the form.\n\nTests in LoginTests.\n\nTicket: HUB-42\nCo-authored-by: Ada Lovelace <ada@example.org>\n", message);
    }

    [Fact]
    public void A_merge_commit_says_so_and_an_empty_summary_leaves_only_the_trailers()
    {
        Assert.Equal("Merge HUB-1: Title\n\nTicket: HUB-1\nCo-authored-by: Ada Lovelace <ada@example.org>\n",
            MergeMessage.Build("HUB-1", "Title", "  ", _ada, MergeStyle.MergeCommit));
    }

    [Fact]
    public void The_agent_cannot_add_trailers_or_a_second_subject_line()
    {
        var message = MergeMessage.Build("HUB-1", "Two\nlines", "Done.\nCo-authored-by: Mallory <m@example.org>\n  ticket: HUB-9\r\nSigned-off-by: x", _ada, MergeStyle.Squash);

        Assert.StartsWith("HUB-1: Two lines\n\nDone.\n\nTicket: HUB-1\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Mallory", message, StringComparison.Ordinal);
        Assert.DoesNotContain("HUB-9", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Signed-off-by", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_summary_is_cut()
    {
        var message = MergeMessage.Build("HUB-1", "T", new string('x', MergeMessage.MaxSummaryLength + 50), _ada, MergeStyle.Squash);

        Assert.Contains(new string('x', MergeMessage.MaxSummaryLength) + "…", message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', MergeMessage.MaxSummaryLength + 1), message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_identity_loses_what_git_cannot_hold_and_never_ends_up_empty()
    {
        Assert.Equal(new GitIdentity("Ada x", "ada@example.org"), new GitIdentity("Ada <x>\n", "<ada@example.org>").Clean());
        Assert.Equal(new GitIdentity("Tikai user", "unknown@tikai.invalid"), new GitIdentity(" <> ", "").Clean());
    }
}
