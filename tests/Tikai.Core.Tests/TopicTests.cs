using Tikai.Core.Events;

namespace Tikai.Core.Tests;

public class TopicTests
{
    [Theory]
    [InlineData("attention_items")]
    [InlineData("runs")]
    public void Valid_topics_pass(string topic)
    {
        Assert.Equal(topic, Topic.Validate(topic));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Runs")]
    [InlineData("run-events")]
    [InlineData("runs\"; DROP TABLE jobs; --")]
    [InlineData("a_very_long_topic_name_that_goes_past_the_postgres_identifier_limit")]
    public void Invalid_topics_are_rejected(string topic)
    {
        Assert.Throws<ArgumentException>(() => Topic.Validate(topic));
    }
}
