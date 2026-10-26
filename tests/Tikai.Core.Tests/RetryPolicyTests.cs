using Tikai.Core.Jobs;

namespace Tikai.Core.Tests;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    public void Backoff_doubles_from_the_base_delay(int attempts, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RetryPolicy.Backoff(attempts, TimeSpan.FromSeconds(5), TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(1_000)]
    [InlineData(int.MaxValue)]
    public void Backoff_is_capped_without_overflow(int attempts)
    {
        Assert.Equal(TimeSpan.FromMinutes(10), RetryPolicy.Backoff(attempts, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Backoff_uses_defaults()
    {
        Assert.Equal(RetryPolicy.DefaultBase, RetryPolicy.Backoff(1));
    }

    [Fact]
    public void Backoff_rejects_zero_attempts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.Backoff(0));
    }
}
