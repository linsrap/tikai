namespace Tikai.Mcp;

/// <summary>Settings of the MCP server (configuration section Tikai:Mcp).</summary>
public sealed class McpOptions
{
    public const string Section = "Tikai:Mcp";

    /// <summary>
    /// How often a waiting blocking call sends a progress notification and re-reads its item. The notification
    /// keeps Claude Code's idle and per-request timers from ending the call (spike 01, Q2).
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
}
