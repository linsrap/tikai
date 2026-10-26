using Microsoft.EntityFrameworkCore;
using Tikai.Core.Events;

namespace Tikai.Data.Events;

/// <summary>Publishes with pg_notify on the context's connection, so the event fires when the caller's transaction commits.</summary>
internal sealed class PostgresEventPublisher(TikaiDbContext db) : IEventPublisher
{
    public async Task PublishAsync(string topic, Guid id, CancellationToken cancellationToken = default)
    {
        var channel = Topic.Validate(topic);
        var payload = id.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT pg_notify({channel}, {payload})", cancellationToken);
    }
}
