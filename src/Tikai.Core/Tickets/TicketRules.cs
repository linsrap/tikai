using System.Globalization;

namespace Tikai.Core.Tickets;

/// <summary>Checks for what people enter in a ticket. Each returns null when valid, otherwise a message for people.</summary>
public static class TicketRules
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 50_000;
    public const int MaxCriterionLength = 1_000;
    public const int MaxCriteria = 50;

    public static string? TitleError(string? title) =>
        string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength || title.Any(char.IsControl)
            ? $"Enter a title of at most {MaxTitleLength} characters on one line."
            : null;

    public static string? DescriptionError(string? description) =>
        description is not null && description.Length > MaxDescriptionLength
            ? $"The description can have at most {MaxDescriptionLength} characters."
            : null;

    /// <summary>Criteria are single lines; the agent and the review panel show them as a numbered list.</summary>
    public static string? CriterionError(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.Trim().Length > MaxCriterionLength || text.Any(char.IsControl)
            ? $"Each criterion needs a text of at most {MaxCriterionLength} characters on one line."
            : null;

    public static string? CriteriaCountError(int count) =>
        count > MaxCriteria ? $"A ticket can have at most {MaxCriteria} criteria." : null;
}

/// <summary>The ticket key people see, in branch names and in commit trailers: project key and number, e.g. "HUB-42".</summary>
public static class TicketKey
{
    public static string Format(string projectKey, int number) =>
        projectKey + "-" + number.ToString(CultureInfo.InvariantCulture);
}
