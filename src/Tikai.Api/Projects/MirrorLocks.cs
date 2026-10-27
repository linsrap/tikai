using Tikai.Api.Hosting;

namespace Tikai.Api.Projects;

/// <summary>
/// One writer per mirror at a time, keyed by repository id: the git job queue (clone, fetch, delete) and a submission
/// taking an agent's work into the mirror both hold the repository's lock while they write.
/// </summary>
internal sealed class MirrorLocks : KeyedLocks;
