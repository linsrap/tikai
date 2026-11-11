using Tikai.Core.Common;

namespace Tikai.Core.Projects;

/// <summary>One product: holds repositories and (later) tickets. Slug and key are fixed after creation.</summary>
public class Project : Entity
{
    public required string Name { get; set; }

    /// <summary>URL name, see <see cref="ProjectSlug"/>.</summary>
    public required string Slug { get; set; }

    /// <summary>Ticket key prefix (HUB in HUB-42), see <see cref="ProjectKey"/>.</summary>
    public required string Key { get; set; }

    /// <summary>JSON of the <see cref="ProjectSettings"/>; "{}" means all defaults.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Archived: hidden, its history kept, and nothing can be started in it until it is restored.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>Someone asked to delete the project with everything in it; it goes once nothing of it runs any more.</summary>
    public DateTimeOffset? DeletingAt { get; set; }
}
