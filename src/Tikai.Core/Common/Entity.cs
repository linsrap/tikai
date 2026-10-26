namespace Tikai.Core.Common;

/// <summary>
/// Base for persisted entities. Id, CreatedAt and UpdatedAt are filled in on save
/// (version 7 GUIDs, time from TimeProvider); optimistic concurrency uses Postgres xmin.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
