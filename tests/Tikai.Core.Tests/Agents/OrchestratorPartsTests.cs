using System.Formats.Tar;
using System.Text;
using Tikai.Core.Agents;
using Tikai.Orchestrator;

namespace Tikai.Core.Tests.Agents;

public class OrchestratorPartsTests
{
    [Fact]
    public void Lines_are_split_across_chunks_and_carriage_returns_dropped()
    {
        var splitter = new LineSplitter();

        var first = splitter.Add("one\r\ntw"u8);
        var second = splitter.Add("o\nthree"u8);
        var rest = splitter.Flush();

        Assert.Equal(["one"], first.Select(l => l.Text));
        Assert.Equal(["two"], second.Select(l => l.Text));
        Assert.Equal("three", rest!.Text);
        Assert.Null(splitter.Flush());
    }

    [Fact]
    public void A_line_over_the_limit_is_skipped_and_counted()
    {
        var splitter = new LineSplitter();
        var chunk = new byte[600 * 1024];
        Array.Fill(chunk, (byte)'x');

        Assert.Empty(splitter.Add(chunk));
        Assert.Empty(splitter.Add(chunk));
        var lines = splitter.Add("tail\nnext\n"u8);

        Assert.Equal(2, lines.Count);
        Assert.Null(lines[0].Text);
        Assert.Equal(2 * chunk.Length + 4, lines[0].SkippedBytes);
        Assert.Equal("next", lines[1].Text);
    }

    [Fact]
    public void Utf8_split_inside_a_character_comes_out_whole()
    {
        var splitter = new LineSplitter();
        var bytes = Encoding.UTF8.GetBytes("grüß\n");

        Assert.Empty(splitter.Add(bytes.AsSpan(0, 3)));
        Assert.Equal("grüß", Assert.Single(splitter.Add(bytes.AsSpan(3))).Text);
    }

    [Fact]
    public async Task The_run_archive_sets_owner_and_mode_from_the_entries()
    {
        var bundle = Path.GetTempFileName();
        await File.WriteAllTextAsync(bundle, "bundle bytes", TestContext.Current.CancellationToken);
        try
        {
            var runId = Guid.NewGuid().ToString("D");
            await using var tar = await RunArchive.BuildAsync(runId,
                RunPrep.Skeleton("HUB-1", bundle, [RunFile.ReadOnly("mcp.json", "{}"u8.ToArray())]), agentUid: 1000, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

            var entries = new Dictionary<string, TarEntry>();
            await using (var reader = new TarReader(tar, leaveOpen: true))
            {
                while (await reader.GetNextEntryAsync(copyData: true, TestContext.Current.CancellationToken) is { } entry)
                {
                    entries[entry.Name] = entry;
                }
            }

            Assert.Equal(TarEntryType.Directory, entries[runId + "/"].EntryType);
            Assert.Equal(0, entries[runId + "/"].Uid);
            Assert.Equal(1000, entries[runId + "/work"].Uid);
            Assert.Equal(1000, entries[runId + "/home"].Uid);
            Assert.Equal(RunFile.Modes.Private, entries[runId + "/home"].Mode);
            Assert.Equal(0, entries[runId + "/mcp.json"].Uid);
            Assert.Equal(RunFile.Modes.ReadOnly, entries[runId + "/mcp.json"].Mode);
            Assert.Equal(RunFile.Modes.Executable, entries[runId + "/prep/commit-msg"].Mode);
            using var bundleData = new StreamReader(entries[runId + "/prep/source.bundle"].DataStream!);
            Assert.Equal("bundle bytes", await bundleData.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(bundle);
        }
    }

    [Fact]
    public void The_ticket_files_are_root_owned_and_read_only()
    {
        var files = RunPrep.TicketFiles("# HUB-1").Select(f => f.Entry).ToList();

        Assert.All(files, f => Assert.False(f.AgentOwned));
        Assert.Equal(RunFile.Modes.Executable, files.Single(f => f.Path == ".tikai").Mode); // r-x: no new files, no deleting
        Assert.Equal(RunFile.Modes.ReadOnly, files.Single(f => f.Path == ".tikai/ticket.md").Mode);
    }

    [Fact]
    public void The_hook_adds_the_ticket_trailer_and_the_prep_script_takes_its_values_as_arguments()
    {
        Assert.Contains("--trailer \"Ticket: HUB-42\"", RunPrep.CommitMsgHook("HUB-42"), StringComparison.Ordinal);
        Assert.DoesNotContain("\r", RunPrep.CommitMsgHook("HUB-42"), StringComparison.Ordinal);

        var spec = RunPrep.Container("img", "main", "tikai/HUB-42-1a2b3c4d");

        Assert.Equal(["/bin/sh", "-euc"], spec.Entrypoint.Take(2));
        Assert.DoesNotContain("tikai/HUB-42", spec.Entrypoint[2], StringComparison.Ordinal);
        Assert.Equal(["tikai-prepare", "main", "tikai/HUB-42-1a2b3c4d", RunPrep.AuthorName, RunPrep.AuthorEmail], spec.Command);
        Assert.Empty(spec.Environment);
    }
}
