using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tikai.Core.Events;
using Tikai.Data;
using Tikai.Data.Events;

namespace Tikai.IntegrationTests;

public class EventBusTests(PostgresFixture postgres)
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_notify_reaches_the_subscriber()
    {
        var topic = PostgresFixture.UniqueName("t");
        using var host = await StartAsync();
        var received = await SubscribeAsync(host, topic);

        var id = Guid.CreateVersion7();
        await PublishAsync(host, topic, id, commit: true);

        Assert.Equal(new BusEvent(topic, id), await received.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(_timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_publish_in_a_rolled_back_transaction_is_not_delivered()
    {
        var topic = PostgresFixture.UniqueName("t");
        using var host = await StartAsync();
        var received = await SubscribeAsync(host, topic);

        await PublishAsync(host, topic, Guid.CreateVersion7(), commit: false);
        var marker = Guid.CreateVersion7();
        await PublishAsync(host, topic, marker, commit: true);

        // Notifications arrive in commit order, so the first one must be the committed marker.
        var first = await received.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(_timeout, TestContext.Current.CancellationToken);
        Assert.Equal(marker, first.Id);
    }

    [Fact]
    public async Task After_a_lost_connection_subscribers_get_a_resync_and_later_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var topic = PostgresFixture.UniqueName("t");
        using var host = await StartAsync();
        var received = await SubscribeAsync(host, topic);

        // Kill the listener's connection from the server side, like a database restart would.
        await using (var db = postgres.CreateContext())
        {
            var killed = await db.Database.SqlQuery<bool>(
                $"""SELECT pg_terminate_backend(pid) AS "Value" FROM pg_stat_activity WHERE query = {$"LISTEN \"{topic}\""}""")
                .ToListAsync(ct);
            Assert.Equal([true], killed);
        }

        var resync = await received.ReadAsync(ct).AsTask().WaitAsync(_timeout, TestContext.Current.CancellationToken);
        Assert.True(resync.IsResync);
        Assert.Equal(topic, resync.Topic);

        await host.Services.GetRequiredService<PostgresEventBus>().Listening.Task.WaitAsync(_timeout, ct);
        var id = Guid.CreateVersion7();
        await PublishAsync(host, topic, id, commit: true);
        Assert.Equal(id, (await received.ReadAsync(ct).AsTask().WaitAsync(_timeout, TestContext.Current.CancellationToken)).Id);
    }

    [Fact]
    public async Task Invalid_topics_are_rejected_before_reaching_the_database()
    {
        using var host = await StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync("Bad-Topic", Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => host.Services.GetRequiredService<IEventBus>().Subscribe("x\"; --", (_, _) => Task.CompletedTask));
    }

    private async Task<IHost> StartAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTikaiData(postgres.ConnectionString);
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static async Task<ChannelReader<BusEvent>> SubscribeAsync(IHost host, string topic)
    {
        var channel = Channel.CreateUnbounded<BusEvent>();
        var bus = host.Services.GetRequiredService<PostgresEventBus>();
        bus.Subscribe(topic, (e, token) => channel.Writer.WriteAsync(e, token).AsTask());
        await bus.Listening.Task.WaitAsync(_timeout, TestContext.Current.CancellationToken);
        return channel.Reader;
    }

    private static async Task PublishAsync(IHost host, string topic, Guid id, bool commit)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TikaiDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(topic, id);
        if (commit)
        {
            await tx.CommitAsync();
        }
        else
        {
            await tx.RollbackAsync();
        }
    }
}
