using System.Security.Cryptography;
using Tikai.Core.Agents;
using Tikai.Core.Runs;

namespace Tikai.Orchestrator;

/// <summary>
/// Before a resume, a helper container (agent image, agent uid, no network) reads what the hub must not read itself:
/// whether the agent's session file still exists, and the run clone's git log and status. Its output is agent-controlled
/// text: the session line comes first, before any git runs, and the sections are split by a random marker the agent
/// cannot know, so a commit message cannot fake them. The text only goes into a hub note and this agent's own prompt.
/// </summary>
internal static class RunInspect
{
    public const int MaxSectionLength = 4_000;

    // Arguments come as positional parameters, never inside the script text.
    private const string Script = """
        if [ -n "$1" ] && [ -f "/run/$1" ]; then echo "session: yes"; else echo "session: no"; fi
        cd /run/work
        echo "$3 log"
        git -c core.fsmonitor= --no-pager log --oneline --no-decorate -n 50 "$2..HEAD" 2>&1 || true
        echo "$3 status"
        git -c core.fsmonitor= --no-pager status --short 2>&1 || true
        """;

    /// <param name="sessionFile">Relative to the run directory; null when there is no session to look for.</param>
    public static ContainerSpec Container(string image, string? sessionFile, string defaultBranch, string marker) => new(
        image,
        Entrypoint: ["/bin/sh", "-euc", Script.Replace("\r\n", "\n", StringComparison.Ordinal)],
        Command: ["tikai-inspect", sessionFile ?? "", defaultBranch, marker],
        Environment: [],
        WorkingDirectory: RunLayout.Work);

    public static string NewMarker() => "--tikai-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static RunCloneState Parse(IReadOnlyList<string> lines, string marker)
    {
        var sessionExists = lines.Count > 0 && lines[0] == "session: yes";
        var log = Section(lines, marker + " log", marker + " status");
        var status = Section(lines, marker + " status", null);
        return new RunCloneState(sessionExists, RunEventPayload.Cut(log, MaxSectionLength), RunEventPayload.Cut(status, MaxSectionLength));
    }

    private static string Section(IReadOnlyList<string> lines, string start, string? end)
    {
        var from = IndexOf(lines, start);
        if (from < 0)
        {
            return "";
        }
        var to = end is null ? -1 : IndexOf(lines, end);
        var count = (to > from ? to : lines.Count) - from - 1;
        return string.Join("\n", lines.Skip(from + 1).Take(count)).Trim();
    }

    private static int IndexOf(IReadOnlyList<string> lines, string value)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i] == value)
            {
                return i;
            }
        }
        return -1;
    }
}

/// <param name="SessionExists">The agent's session file is still in the run directory.</param>
/// <param name="GitLog">The run branch's commits since the default branch, newest first (oneline, cut).</param>
public sealed record RunCloneState(bool SessionExists, string GitLog, string GitStatus);
