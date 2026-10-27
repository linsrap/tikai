using Tikai.Core.Git;

namespace Tikai.Git;

/// <summary>
/// A git bundle (v2 or v3) is a text header (signature, capabilities, prerequisites, refs) ended by an empty line,
/// followed by a pack. This reads the pack, for <c>git index-pack</c>.
/// </summary>
internal static class BundlePack
{
    private const int MaxHeaderBytes = 16 * 1024 * 1024;

    public static async Task<byte[]> ReadAsync(string bundleFile, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(bundleFile, cancellationToken);
        if (!bytes.AsSpan().StartsWith("# v2 git bundle\n"u8) && !bytes.AsSpan().StartsWith("# v3 git bundle\n"u8))
        {
            throw new GitException("The file is not a git bundle (v2 or v3).");
        }
        var end = bytes.AsSpan(0, Math.Min(bytes.Length, MaxHeaderBytes)).IndexOf("\n\n"u8);
        if (end < 0 || !bytes.AsSpan(end + 2).StartsWith("PACK"u8))
        {
            throw new GitException("The git bundle has no pack after its header.");
        }
        return bytes[(end + 2)..];
    }
}
