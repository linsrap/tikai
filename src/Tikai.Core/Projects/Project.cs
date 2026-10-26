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
}
