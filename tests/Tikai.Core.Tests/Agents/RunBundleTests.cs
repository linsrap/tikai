using System.Formats.Tar;
using Tikai.Orchestrator;

namespace Tikai.Core.Tests.Agents;

public sealed class RunBundleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tikai-bundle-tests", Guid.NewGuid().ToString("N"));

    public RunBundleTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void The_bundle_script_takes_its_values_as_arguments_without_network_shapes()
    {
        var spec = RunBundle.Container("img", "tikai/HUB-1-abcdef12", "0123abcd", "--tikai-m");

        Assert.Equal(["/bin/sh", "-euc"], spec.Entrypoint.Take(2));
        Assert.DoesNotContain("tikai/HUB-1", spec.Entrypoint[2], StringComparison.Ordinal);
        Assert.DoesNotContain("\r", spec.Entrypoint[2], StringComparison.Ordinal);
        Assert.Equal(["tikai-bundle", "tikai/HUB-1-abcdef12", "0123abcd", "--tikai-m"], spec.Command);
        Assert.Equal("", RunBundle.Container("img", "b", null, "m").Command[2]);
        Assert.Equal("/run/home/.tikai-review/review.bundle", RunBundle.BundlePath);
    }

    [Fact]
    public void The_report_reads_commits_uncommitted_files_and_the_bundle_by_the_marker_only()
    {
        var report = RunBundle.Parse(["m commits 3", "m status", " M src/a.cs", "?? notes.txt", "", "m end", "m bundle"], "m");

        Assert.Equal(new BundleReport(true, 3, report.Uncommitted, true), report);
        Assert.Equal([" M src/a.cs", "?? notes.txt"], report.Uncommitted);
    }

    [Fact]
    public void A_commit_message_cannot_fake_the_report()
    {
        // The agent does not know the marker: lines without it say nothing.
        var report = RunBundle.Parse(["commits 9", "bundle", "x commits 5", "m commits 0", "m status", "m end"], "m");

        Assert.Equal((0, false), (report.Commits, report.Bundled));
    }

    [Fact]
    public void A_missing_branch_and_a_long_status_are_reported()
    {
        Assert.False(RunBundle.Parse(["m nobranch"], "m").BranchExists);

        var lines = new List<string> { "m commits 1", "m status" };
        lines.AddRange(Enumerable.Range(0, 300).Select(i => $"?? {new string('f', 400)}{i}"));
        lines.Add("m end");
        var report = RunBundle.Parse(lines, "m");
        Assert.Equal(RunBundle.MaxUncommitted, report.Uncommitted.Count);
        Assert.All(report.Uncommitted, l => Assert.EndsWith(" more characters]", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_bundle_comes_out_of_a_tar_with_just_that_file()
    {
        var output = Path.Combine(_directory, "out.bundle");
        await using var tar = await TarAsync(new PaxTarEntry(TarEntryType.RegularFile, RunBundle.BundleName) { DataStream = new MemoryStream([1, 2, 3]) });

        await RunBundle.ExtractAsync(tar, output, 100, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal([output], Directory.GetFiles(_directory)); // the copy of the tar is gone
    }

    public static TheoryData<string> Refused() => ["symlink", "directory", "two", "large", "name"];

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Anything_but_one_regular_bundle_file_within_the_limit_is_refused(string kind)
    {
        TarEntry[] entries = kind switch
        {
            "symlink" => [new PaxTarEntry(TarEntryType.SymbolicLink, RunBundle.BundleName) { LinkName = "/etc/passwd" }],
            "directory" => [new PaxTarEntry(TarEntryType.Directory, RunBundle.BundleName)],
            "two" => [Regular(RunBundle.BundleName, 3), Regular("other", 3)],
            "large" => [Regular(RunBundle.BundleName, 200)],
            _ => [Regular("passwd", 3)],
        };
        await using var tar = await TarAsync(entries);

        await Assert.ThrowsAsync<WorkCollectException>(() => RunBundle.ExtractAsync(tar, Path.Combine(_directory, "out.bundle"), 100, TestContext.Current.CancellationToken));
    }

    private static PaxTarEntry Regular(string name, int size) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(new byte[size]) };

    private static async Task<MemoryStream> TarAsync(params TarEntry[] entries)
    {
        var stream = new MemoryStream();
        await using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                await writer.WriteEntryAsync(entry);
            }
        }
        stream.Position = 0;
        return stream;
    }
}
