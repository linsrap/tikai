using System.Formats.Tar;
using System.Globalization;
using Tikai.Core.Agents;
using Tikai.Core.Runs;

namespace Tikai.Orchestrator;

/// <summary>
/// When the agent submits, a helper container (agent image, agent uid, no network) bundles the run branch, because
/// the hub never runs git in a run clone. It also counts the commits since the run's start and lists what is left
/// uncommitted. Its output is agent-controlled text, split by a random marker as in <see cref="RunInspect"/>.
/// </summary>
internal static class RunBundle
{
    public const string BundleDirectory = RunLayout.Home + "/.tikai-review";
    public const string BundleName = "review.bundle";
    public const string BundlePath = BundleDirectory + "/" + BundleName;

    public const int MaxUncommitted = 200;
    public const int MaxUncommittedLength = 300;

    // $1 branch, $2 base commit (may be empty or unknown to the clone), $3 marker. Arguments never go into the script text.
    private const string Script = """
        cd /run/work
        if ! git rev-parse --verify --quiet "refs/heads/$1^{commit}" >/dev/null; then echo "$3 nobranch"; exit 0; fi
        base=""
        if [ -n "$2" ] && git cat-file -e "$2^{commit}" 2>/dev/null; then base="$2"; fi
        if [ -n "$base" ]; then count=$(git rev-list --count "$base..refs/heads/$1"); else count=$(git rev-list --count "refs/heads/$1"); fi
        echo "$3 commits $count"
        echo "$3 status"
        git -c core.fsmonitor= status --porcelain --untracked-files=all 2>&1 | head -n 201 || true
        echo "$3 end"
        if [ "$count" -eq 0 ]; then exit 0; fi
        rm -rf /run/home/.tikai-review
        mkdir /run/home/.tikai-review
        if [ -n "$base" ]; then
          git bundle create --quiet /run/home/.tikai-review/review.bundle "refs/heads/$1" "^$base"
        else
          git bundle create --quiet /run/home/.tikai-review/review.bundle "refs/heads/$1"
        fi
        echo "$3 bundle"
        """;

    public static ContainerSpec Container(string image, string branch, string? baseSha, string marker) => new(
        image,
        Entrypoint: ["/bin/sh", "-euc", Script.Replace("\r\n", "\n", StringComparison.Ordinal)],
        Command: ["tikai-bundle", branch, baseSha ?? "", marker],
        Environment: [],
        WorkingDirectory: RunLayout.Work);

    public static BundleReport Parse(IReadOnlyList<string> lines, string marker)
    {
        if (lines.Contains(marker + " nobranch"))
        {
            return new BundleReport(BranchExists: false, 0, [], Bundled: false);
        }
        var commits = lines.Where(l => l.StartsWith(marker + " commits ", StringComparison.Ordinal))
            .Select(l => int.TryParse(l[(marker.Length + 9)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .FirstOrDefault();
        var start = lines.ToList().IndexOf(marker + " status");
        var end = lines.ToList().IndexOf(marker + " end");
        var uncommitted = start >= 0 && end > start
            ? lines.Skip(start + 1).Take(end - start - 1).Where(l => l.Length > 0).Take(MaxUncommitted)
                .Select(l => RunEventPayload.Cut(l, MaxUncommittedLength)).ToList()
            : [];
        return new BundleReport(BranchExists: true, commits, uncommitted, Bundled: lines.Contains(marker + " bundle"));
    }

    /// <summary>
    /// Copies the bundle out of the tar Docker returned: exactly one regular file, at most <paramref name="maxBytes"/>.
    /// Anything else (a symlink the agent planted, a directory, a second entry) is refused.
    /// </summary>
    /// <exception cref="WorkCollectException">The tar is not just the bundle file.</exception>
    public static async Task ExtractAsync(Stream tar, string outputFile, long maxBytes, CancellationToken cancellationToken)
    {
        // TarReader reading Docker.DotNet's chunked response directly fails with EndOfStreamException in the entry data
        // (checked with Docker.DotNet.Enhanced 4.3.3); the same bytes from a file read fine. So the tar goes to a file
        // first, with room for its headers on top of the bundle limit.
        var tarFile = outputFile + ".tar";
        try
        {
            await using (var copy = new FileStream(tarFile, FileMode.CreateNew, FileAccess.Write))
            {
                await CopyAtMostAsync(tar, copy, maxBytes + 1024 * 1024, cancellationToken);
            }
            await using var file = new FileStream(tarFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await ExtractFileAsync(file, outputFile, maxBytes, cancellationToken);
        }
        finally
        {
            File.Delete(tarFile);
        }
    }

    private static async Task ExtractFileAsync(Stream tar, string outputFile, long maxBytes, CancellationToken cancellationToken)
    {
        await using var reader = new TarReader(tar);
        var entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken);
        if (entry is not { EntryType: TarEntryType.RegularFile or TarEntryType.V7RegularFile, Name: BundleName })
        {
            throw new WorkCollectException($"The bundle in the run directory is not a regular file ({entry?.EntryType.ToString() ?? "missing"}).");
        }
        if (entry.Length > maxBytes)
        {
            throw new WorkCollectException($"Your work is {entry.Length / (1024 * 1024)} MB as a git bundle; the hub takes at most {maxBytes / (1024 * 1024)} MB. Remove large files from your commits.");
        }
        await using (var output = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write))
        {
            if (entry.DataStream is { } data)
            {
                await CopyAtMostAsync(data, output, maxBytes, cancellationToken);
            }
        }
        if (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is not null)
        {
            throw new WorkCollectException("The bundle in the run directory is not a single file.");
        }
    }

    private static async Task CopyAtMostAsync(Stream source, Stream target, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new WorkCollectException($"Your work is more than {maxBytes / (1024 * 1024)} MB as a git bundle. Remove large files from your commits.");
            }
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}

/// <param name="Commits">Commits on the run branch since the run's start (the whole branch for runs without a recorded start).</param>
/// <param name="Uncommitted">git status --porcelain lines, cut.</param>
internal sealed record BundleReport(bool BranchExists, int Commits, IReadOnlyList<string> Uncommitted, bool Bundled);
