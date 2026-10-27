using System.Formats.Tar;
using Tikai.Core.Agents;

namespace Tikai.Orchestrator;

/// <summary>
/// Builds the tar the hub uploads into a run directory. Docker keeps the owner and mode from the tar headers, so the
/// hub sets them without being root: root-owned entries the agent can only read, agent-owned ones it may change.
/// </summary>
internal static class RunArchive
{
    /// <summary>Writes the entries to a temp file (a bundle can be big) and returns it opened for reading; deleted on dispose.</summary>
    /// <param name="prefix">Prepended to every path, e.g. the run id for an upload into the volume root.</param>
    /// <param name="files">Files with content from memory or (for <see cref="ArchiveEntry.SourceFile"/>) from disk.</param>
    public static async Task<FileStream> BuildAsync(
        string? prefix, IEnumerable<ArchiveEntry> files, int agentUid, DateTimeOffset modified, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tikai-run-{Guid.NewGuid():N}.tar");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        try
        {
            await using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var file in files)
                {
                    var name = prefix is null ? file.Entry.Path : $"{prefix}/{file.Entry.Path}";
                    var entry = new PaxTarEntry(file.Entry.Content is null && file.SourceFile is null ? TarEntryType.Directory : TarEntryType.RegularFile, name)
                    {
                        Uid = file.Entry.AgentOwned ? agentUid : 0,
                        Gid = file.Entry.AgentOwned ? agentUid : 0,
                        Mode = file.Entry.Mode,
                        ModificationTime = modified,
                    };
                    await using var content = file.SourceFile is not null
                        ? (Stream)File.OpenRead(file.SourceFile)
                        : file.Entry.Content is not null ? new MemoryStream(file.Entry.Content) : null;
                    if (content is not null)
                    {
                        entry.DataStream = content;
                    }
                    await writer.WriteEntryAsync(entry, cancellationToken);
                }
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}

/// <param name="SourceFile">Read from this hub file instead of <see cref="RunFile.Content"/>.</param>
internal sealed record ArchiveEntry(RunFile Entry, string? SourceFile = null)
{
    public static implicit operator ArchiveEntry(RunFile entry) => new(entry);
}
