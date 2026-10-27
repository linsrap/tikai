// Stand-in for Claude Code in Tikai's tests. Takes Claude Code's command line, reads the MCP config, calls the Tikai
// tools like Claude Code does (stateless Streamable HTTP, protocol 2026-07-28) and prints stream-json. What it does
// is chosen by a "stub:<scenario>" word in the ticket description:
//   ok (default)  get_ticket, report_progress, commit, submit_for_review, exit 0
//   no-submit     the same without submit_for_review
//   error         a result with is_error, exit 1
//   leak          prints the API key and the run token (the hub must redact them), then ok
//   big           a huge tool result and a line over 1 MB, then ok
//   hang          waits until stopped
//   ask           ask_human, gives up waiting after 2 s, exits 0 (the item stays open)
//   slow:<s>      waits <s> seconds after get_ticket, then ok
// It also reports what it sees from the inside (uid, branch, hook, what it can write) as a "STUB_FACTS {json}" message.
import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import fs from "node:fs";

const args = process.argv.slice(2);
const arg = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const sessionId = randomUUID();
const out = (event) => process.stdout.write(JSON.stringify({ ...event, session_id: sessionId }) + "\n");
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const config = JSON.parse(fs.readFileSync(arg("--mcp-config"), "utf8"));
const server = config.mcpServers.tikai;
const meta = {
  "io.modelcontextprotocol/protocolVersion": "2026-07-28",
  "io.modelcontextprotocol/clientInfo": { name: "tikai-agent-stub", version: "1" },
  "io.modelcontextprotocol/clientCapabilities": {},
};
let nextId = 1;

async function mcp(method, params, name, timeoutMs = 30000) {
  const headers = {
    ...server.headers,
    "Content-Type": "application/json",
    Accept: "application/json, text/event-stream",
    "MCP-Protocol-Version": "2026-07-28",
    "Mcp-Method": method,
  };
  if (name) headers["Mcp-Name"] = name;
  const response = await fetch(server.url, {
    method: "POST",
    headers,
    body: JSON.stringify({ jsonrpc: "2.0", id: nextId++, method, params: { ...params, _meta: { ...meta, ...(params._meta ?? {}) } } }),
    signal: AbortSignal.timeout(timeoutMs),
  });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  const text = await response.text();
  // An SSE stream (progress notifications, then the result) or plain JSON.
  const messages = text.includes("data:")
    ? text.split("\n").filter((l) => l.startsWith("data:")).map((l) => JSON.parse(l.slice(5)))
    : [JSON.parse(text)];
  const result = messages.find((m) => m.id !== undefined);
  if (result.error) throw new Error(result.error.message);
  return result.result;
}

let toolNumber = 0;
async function tool(name, input, timeoutMs) {
  const id = `toolu_stub_${++toolNumber}`;
  out({ type: "assistant", message: { role: "assistant", content: [{ type: "tool_use", id, name: `mcp__tikai__${name}`, input }] }, parent_tool_use_id: null });
  const result = await mcp("tools/call", { name, arguments: input, _meta: { progressToken: id } }, name, timeoutMs);
  const text = result.content.map((c) => c.text).join("");
  out({ type: "user", message: { role: "user", content: [{ type: "tool_result", tool_use_id: id, content: text, is_error: !!result.isError }] } });
  if (result.isError) throw new Error(`${name}: ${text}`);
  return text;
}

function bash(command) {
  const id = `toolu_stub_${++toolNumber}`;
  out({ type: "assistant", message: { role: "assistant", content: [{ type: "tool_use", id, name: "Bash", input: { command } }] } });
  let output;
  let isError = false;
  try {
    output = execFileSync("/bin/sh", ["-c", command], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
  } catch (e) {
    output = `${e.stdout ?? ""}${e.stderr ?? ""}`;
    isError = true;
  }
  out({ type: "user", message: { role: "user", content: [{ type: "tool_result", tool_use_id: id, content: output, is_error: isError }] } });
  return output.trim();
}

const can = (fn) => {
  try {
    fn();
    return true;
  } catch {
    return false;
  }
};

function facts() {
  const sh = (c) => {
    try {
      return execFileSync("/bin/sh", ["-c", c], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
    } catch {
      return null;
    }
  };
  return {
    uid: process.getuid(),
    gid: process.getgid(),
    cwd: process.cwd(),
    argv: args.map((a, i) => (args[i - 1] === "-p" ? "<prompt>" : a)),
    prompt_start: (arg("-p") ?? "").slice(0, 120),
    env_names: Object.keys(process.env).sort(),
    home: process.env.HOME,
    claude_config_dir: process.env.CLAUDE_CONFIG_DIR,
    branch: sh("git branch --show-current"),
    remotes: sh("git remote"),
    branches: sh("git branch --list --all --format='%(refname)'"),
    author: sh("git config user.name") + " <" + sh("git config user.email") + ">",
    hook: fs.existsSync(".git/hooks/commit-msg"),
    excluded: sh("cat .git/info/exclude"),
    ticket_md: sh("head -n 1 .tikai/ticket.md"),
    git_status: sh("git status --porcelain"),
    mcp_header: (server.headers?.Authorization ?? "").startsWith("Bearer tikai_run_"),
    mcp_timeout: server.timeout,
    can_write_ticket: can(() => fs.appendFileSync(".tikai/ticket.md", "x")),
    can_delete_ticket: can(() => fs.rmSync(".tikai/ticket.md")),
    can_write_mcp_config: can(() => fs.appendFileSync("/run/mcp.json", "x")),
    can_create_in_run_root: can(() => fs.writeFileSync("/run/stub-was-here", "x")),
    can_write_root_fs: can(() => fs.writeFileSync("/etc/stub-was-here", "x")),
    can_write_tmp: can(() => fs.writeFileSync("/tmp/stub-was-here", "x")),
    can_write_home: can(() => fs.writeFileSync(`${process.env.HOME}/stub-was-here`, "x")),
    run_root: sh("ls -lan /run"),
    sees_data: fs.existsSync("/data") || fs.existsSync("/runs"),
  };
}

async function main() {
  // Like Claude Code: connect first, report the MCP status in the init event, carry on even when it failed.
  let connected = false;
  try {
    await mcp("tools/list", {}, undefined, 10000);
    connected = true;
  } catch {
    connected = false;
  }
  out({
    type: "system",
    subtype: "init",
    cwd: process.cwd(),
    model: arg("--model"),
    tools: ["Bash", "Read", "Write", "mcp__tikai__get_ticket"],
    mcp_servers: [{ name: "tikai", status: connected ? "connected" : "failed" }],
    permissionMode: arg("--permission-mode"),
    apiKeySource: "ANTHROPIC_API_KEY",
    claude_code_version: "stub",
  });
  if (!connected) {
    out({ type: "assistant", message: { role: "assistant", content: [{ type: "text", text: "The Tikai tools are missing; working without them." }] } });
    await sleep(30000);
    out({ type: "result", subtype: "success", is_error: false, num_turns: 1, result: "done without tools", total_cost_usd: 0 });
    return 0;
  }

  const ticket = JSON.parse(await tool("get_ticket", {}));
  const scenario = /stub:([a-z-]+(?::\d+)?)/.exec(ticket.description)?.[1] ?? "ok";
  out({ type: "assistant", message: { role: "assistant", content: [{ type: "thinking", thinking: `Scenario ${scenario}.` }, { type: "text", text: `Working on ${ticket.key}.` }] } });

  if (scenario === "hang") {
    await sleep(3600_000);
  }
  if (scenario === "error") {
    out({ type: "result", subtype: "error_during_execution", is_error: true, num_turns: 2, result: "it broke" });
    return 1;
  }
  if (scenario === "ask") {
    try {
      await tool("ask_human", { question: "Which colour?" }, 2000);
    } catch {
      out({ type: "assistant", message: { role: "assistant", content: [{ type: "text", text: "No answer; giving up." }] } });
    }
    out({ type: "result", subtype: "success", is_error: false, num_turns: 3, result: "gave up waiting", total_cost_usd: 0.001 });
    return 0;
  }
  if (scenario.startsWith("slow:")) {
    await sleep(Number(scenario.split(":")[1]) * 1000);
  }
  if (scenario === "leak") {
    const token = server.headers.Authorization.slice("Bearer ".length);
    out({ type: "user", message: { role: "user", content: [{ type: "tool_result", tool_use_id: "toolu_leak", content: `key=${process.env.ANTHROPIC_API_KEY} token=${token}` }] } });
    process.stderr.write(`stderr key=${process.env.ANTHROPIC_API_KEY}\n`);
  }
  if (scenario === "big") {
    out({ type: "user", message: { role: "user", content: [{ type: "tool_result", tool_use_id: "toolu_big", content: "b".repeat(100_000) }] } });
    process.stdout.write("x".repeat(1_200_000) + "\n");
  }

  await tool("report_progress", { step: "Writing hello.txt", note: "stub" });
  const stubFacts = facts();
  bash("printf 'hello\\n' > hello.txt && git add hello.txt && git commit --quiet -m 'Add hello.txt'");
  stubFacts.last_commit = bash("git log -1 --format='%an <%ae>%n%B'");
  stubFacts.files_in_commit = bash("git show --name-only --format= HEAD");
  out({ type: "assistant", message: { role: "assistant", content: [{ type: "text", text: "STUB_FACTS " + JSON.stringify(stubFacts) }] } });

  if (scenario !== "no-submit") {
    await tool("submit_for_review", {
      summary: "Added hello.txt.",
      criteria_mapping: ticket.criteria.map((c) => ({ criterion_id: c.id, status: "covered", note: "stub" })),
      open_concerns: [],
    });
  }
  out({
    type: "result",
    subtype: "success",
    is_error: false,
    num_turns: 6,
    duration_ms: 1234,
    result: "Done.",
    total_cost_usd: 0.0123,
    usage: { input_tokens: 100, output_tokens: 20, cache_creation_input_tokens: 5, cache_read_input_tokens: 7 },
    permission_denials: [],
  });
  return 0;
}

main().then(
  (code) => process.exit(code),
  (error) => {
    process.stderr.write(`stub failed: ${error.stack ?? error}\n`);
    process.exit(2);
  },
);
