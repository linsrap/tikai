using Tikai.Core.Tickets;

namespace Tikai.Core.Tests.Tickets;

public class TicketRulesTests
{
    [Theory]
    [InlineData("Add a login page")]
    [InlineData("  Trimmed  ")]
    [InlineData("Ümlaut — and dashes")]
    public void Valid_titles_pass(string title)
    {
        Assert.Null(TicketRules.TitleError(title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two\nlines")]
    [InlineData("tab\tinside")]
    public void Invalid_titles_are_rejected(string? title)
    {
        Assert.NotNull(TicketRules.TitleError(title));
    }

    [Fact]
    public void Titles_are_limited_after_trimming()
    {
        Assert.Null(TicketRules.TitleError("  " + new string('a', TicketRules.MaxTitleLength) + "  "));
        Assert.NotNull(TicketRules.TitleError(new string('a', TicketRules.MaxTitleLength + 1)));
    }

    [Fact]
    public void Descriptions_may_be_empty_or_multiline_up_to_the_limit()
    {
        Assert.Null(TicketRules.DescriptionError(null));
        Assert.Null(TicketRules.DescriptionError(""));
        Assert.Null(TicketRules.DescriptionError("# Heading\n\n- item\r\n- item\t"));
        Assert.Null(TicketRules.DescriptionError(new string('a', TicketRules.MaxDescriptionLength)));
        Assert.NotNull(TicketRules.DescriptionError(new string('a', TicketRules.MaxDescriptionLength + 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("line one\nline two")]
    public void Invalid_criteria_are_rejected(string? text)
    {
        Assert.NotNull(TicketRules.CriterionError(text));
    }

    [Fact]
    public void Criteria_are_limited_in_length_and_number()
    {
        Assert.Null(TicketRules.CriterionError("The login page rejects wrong passwords"));
        Assert.Null(TicketRules.CriterionError(new string('a', TicketRules.MaxCriterionLength)));
        Assert.NotNull(TicketRules.CriterionError(new string('a', TicketRules.MaxCriterionLength + 1)));
        Assert.Null(TicketRules.CriteriaCountError(TicketRules.MaxCriteria));
        Assert.NotNull(TicketRules.CriteriaCountError(TicketRules.MaxCriteria + 1));
    }

    [Theory]
    [InlineData("HUB", 1, "HUB-1")]
    [InlineData("TIKAI2", 1042, "TIKAI2-1042")]
    public void Ticket_keys_join_project_key_and_number(string projectKey, int number, string expected)
    {
        Assert.Equal(expected, TicketKey.Format(projectKey, number));
    }
}
