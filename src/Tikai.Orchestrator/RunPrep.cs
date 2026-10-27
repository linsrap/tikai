using System.Text;
using Tikai.Core.Agents;

namespace Tikai.Orchestrator;

/// <summary>
/// The run clone is made inside a prep container (agent image, agent uid, no network) from a bundle of the default
/// branch the hub wrote into the run directory. The hub itself never runs git in a run directory.
/// </summary>
internal static class RunPrep
{
    public const string BundleFile = "prep/source.bundle";
    public const string HookFile = "prep/commit-msg";
    public const string TicketDirectory = ".tikai";
    public const string TicketFile = ".tikai/ticket.md";

    public const string AuthorName = "Claude Code (Tikai)";
    public const string AuthorEmail = "claude-code@agents.tikai.invalid";

    // Arguments come as positional parameters, never inside the script text.
    private const string Script = """
        cd /run/work
        git clone --quiet --single-branch --no-tags --branch "$1" /run/prep/source.bundle .
        git remote remove origin
        git switch --quiet --create "$2"
        git config user.name "$3"
        git config user.email "$4"
        git config commit.gpgsign false
        cp /run/prep/commit-msg .git/hooks/commit-msg
        chmod 755 .git/hooks/commit-msg
        printf '/.tikai/\n' >> .git/info/exclude
        git log --oneline -1
        """;

    /// <summary>The directories and hub files of a new run directory (before the clone).</summary>
    public static IEnumerable<ArchiveEntry> Skeleton(string ticketKey, string bundleFile, IEnumerable<RunFile> adapterFiles)
    {
        yield return RunFile.Directory("", agentOwned: false, RunFile.Modes.Directory);
        yield return RunFile.Directory("work", agentOwned: true, RunFile.Modes.Directory);
        yield return RunFile.Directory("home", agentOwned: true, RunFile.Modes.Private);
        yield return RunFile.Directory("prep", agentOwned: false, RunFile.Modes.Directory);
        yield return new ArchiveEntry(new RunFile(BundleFile, null, AgentOwned: false, RunFile.Modes.ReadOnly), bundleFile);
        yield return new RunFile(HookFile, Encoding.UTF8.GetBytes(CommitMsgHook(ticketKey)), AgentOwned: false, RunFile.Modes.Executable);
        foreach (var file in adapterFiles)
        {
            yield return file;
        }
    }

    /// <summary>.tikai/ticket.md, owned by root in a root-owned directory: the agent can read it, not change or delete it.</summary>
    public static IEnumerable<ArchiveEntry> TicketFiles(string ticketDocument) =>
    [
        RunFile.Directory(TicketDirectory, agentOwned: false, RunFile.Modes.Executable),
        RunFile.ReadOnly(TicketFile, Encoding.UTF8.GetBytes(ticketDocument)),
    ];

    public static ContainerSpec Container(string image, string defaultBranch, string branch) => new(
        image,
        Entrypoint: ["/bin/sh", "-euc", Script.Replace("\r\n", "\n", StringComparison.Ordinal)],
        Command: ["tikai-prepare", defaultBranch, branch, AuthorName, AuthorEmail],
        Environment: [],
        WorkingDirectory: RunLayout.Work);

    /// <summary>Adds the Ticket trailer to every commit (once). The agent could remove the hook; the merge adds it anyway.</summary>
    public static string CommitMsgHook(string ticketKey) =>
        "#!/bin/sh\n" +
        "# Added by Tikai: every commit names its ticket.\n" +
        $"exec git interpret-trailers --in-place --if-exists addIfDifferent --trailer \"Ticket: {ticketKey}\" \"$1\"\n";
}
