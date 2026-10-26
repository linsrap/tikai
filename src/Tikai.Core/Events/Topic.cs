using System.Text.RegularExpressions;

namespace Tikai.Core.Events;

public static partial class Topic
{
    /// <summary>Throws unless the topic is a valid channel name: lowercase letters and underscores, at most 63 characters.</summary>
    public static string Validate(string topic)
    {
        if (topic.Length is 0 or > 63 || !ValidName().IsMatch(topic))
        {
            throw new ArgumentException($"Invalid event topic '{topic}': use lowercase letters and underscores, at most 63 characters.", nameof(topic));
        }
        return topic;
    }

    [GeneratedRegex("^[a-z_]+$")]
    private static partial Regex ValidName();
}
