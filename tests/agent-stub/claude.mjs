// Stand-in for Claude Code in Tikai's tests. Takes Claude Code's command line, reads the MCP config, calls the Tikai
// tools like Claude Code does (stateless Streamable HTTP, protocol 2026-07-28) and prints stream-json. What it does
// is chosen by a "stub:<scenario>" word in the ticket description:
//   ok (default)    get_ticket, report_progress, commit, submit_for_review, exit 0
//   no-submit       the same without submit_for_review
//   no-commit       submits before committing (refused), commits hello.txt, submits again
//   review          changes, deletes, renames and adds files (the test seeds the default branch, else it commits the
//                   files first), leaves notes.txt uncommitted, submits, submits again, asks a question (refused)
//   error           a result with is_error, exit 1
//   leak            prints the API key and the run token (the hub must redact them), then ok
//   big             a huge tool result and a line over 1 MB, then ok
//   hang            waits until stopped
//   ask             ask_human, gives up waiting after 2 s, exits 0 (the item stays open)
//   slow:<s>        waits <s> seconds after get_ticket, then ok
//   wait            commits before.txt, asks "Which word?" and waits (a dropped call is asked again, up to 10 times,
//                   1 s apart, like Claude Code's model does), writes the answer to answer.txt, commits, submits
//   wait-badresume  like wait, but a --resume of its session fails (error result, exit 1)
//   wait-nosession  like wait, but deletes its own session file before it asks
//   plan-wait[:<n>] proposes a two-step plan and waits (a revision is proposed again with the notes as a step), commits
//                   before.txt, asks "Which word?" with the options alpha and beta and waits, prints <n> lines 250 ms
//                   apart (stdout only, so a hub restart meanwhile loses nothing), then like wait; MCP calls after the
//                   lines are retried for a minute (the browser test restarts the hub while they print)
// Like Claude Code it keeps a session file in $CLAUDE_CONFIG_DIR/projects/-run-work/<session>.jsonl, continues it with
// --resume <session> (the answer comes in the -p message; a missing file is "No conversation found", exit 1), and
// takes over from a summary prompt in a fresh session. It reports what it sees from the inside (uid, branch, hook,
// what it can write, how it was started, the git log) as a "STUB_FACTS {json}" message.
import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

const args = process.argv.slice(2);
const arg = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const prompt = arg("-p") ?? "";
const resumeId = arg("--resume");
const isSummary = prompt.startsWith("You are continuing work on the Tikai ticket");
const mode = resumeId ? "resume" : isSummary ? "summary" : "start";
const sessionId = resumeId ?? randomUUID();
const sessionFile = path.join(process.env.CLAUDE_CONFIG_DIR ?? "/tmp", "projects", "-run-work", `${sessionId}.jsonl`);
const out = (event) => process.stdout.write(JSON.stringify({ ...event, session_id: sessionId }) + "\n");
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const say = (text) => out({ type: "assistant", message: { role: "assistant", content: [{ type: "text", text }] } });

const session = {
  read: () => fs.readFileSync(sessionFile, "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l)),
  add: (entry) => {
    fs.mkdirSync(path.dirname(sessionFile), { recursive: true });
    fs.appendFileSync(sessionFile, JSON.stringify(entry) + "\n");
  },
};

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
  if (!result) throw new Error("transport dropped mid-call; the response was lost");
  if (result.error) throw new Error(result.error.message);
  return result.result;
}

let toolNumber = 0;
async function tool(name, input, timeoutMs) {
  const id = `toolu_stub_${++toolNumber}`;
  out({ type: "assistant", message: { role: "assistant", content: [{ type: "tool_use", id, name: `mcp__tikai__${name}`, input }] }, parent_tool_use_id: null });
  let result;
  try {
    result = await mcp("tools/call", { name, arguments: input, _meta: { progressToken: id } }, name, timeoutMs);
  } catch (e) {
    out({ type: "user", message: { role: "user", content: [{ type: "tool_result", tool_use_id: id, content: `MCP server "tikai": ${e.message}`, is_error: true }] } });
    throw e;
  }
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

const sh = (c) => {
  try {
    return execFileSync("/bin/sh", ["-c", c], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
  } catch {
    return null;
  }
};

function facts() {
  return {
    uid: process.getuid(),
    gid: process.getgid(),
    cwd: process.cwd(),
    argv: args.map((a, i) => (args[i - 1] === "-p" ? "<prompt>" : a)),
    prompt_start: prompt.slice(0, 120),
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

function init(connected) {
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
}

const result = (fields) =>
  out({ type: "result", subtype: "success", is_error: false, num_turns: 6, duration_ms: 1234, permission_denials: [], ...fields });

async function submit(ticket) {
  await tool("submit_for_review", {
    summary: "Added hello.txt.",
    criteria_mapping: ticket.criteria.map((c) => ({ criterion_id: c.id, status: "covered", note: "stub" })),
    open_concerns: [],
  });
}

/** Asks, and asks again when the call drops (Claude Code's model does that, spike Q3a), until it gives up (Q3b). */
async function askUntilAnswered(question, extra = {}) {
  for (let attempt = 1; ; attempt++) {
    try {
      return { answer: JSON.parse(await tool("ask_human", { question, ...extra }, 3600_000)).answer, asked: attempt };
    } catch (e) {
      if (attempt >= 10) return { answer: null, asked: attempt };
      await sleep(1000);
    }
  }
}

/**
 * review: every kind of change on top of the files the test put on the default branch (change.txt, gone.txt,
 * old-name.txt), one file left uncommitted; then a second submit (already in review) and a question (refused).
 */
async function reviewChanges(ticket) {
  // The browser test cannot seed the default branch: then the files come in a commit of the run itself.
  if (!fs.existsSync("change.txt")) {
    bash(
      "printf 'first line\\n' > change.txt && printf 'bye\\n' > gone.txt && printf 'a file that moves\\n' > old-name.txt" +
        " && git add -A && git commit --quiet -m 'Seed files'",
    );
  }
  bash(
    [
      "printf 'one more line\\n' >> change.txt",
      "git rm --quiet gone.txt",
      "git mv old-name.txt new-name.txt",
      "mkdir -p src && printf 'export const answer = 42;\\n' > src/added.ts",
      "printf '\\211PNG\\r\\n\\032\\n\\000\\000\\001' > image.png",
      "printf '{\"lockfileVersion\": 3}\\n' > package-lock.json",
      "head -c 2000000 /dev/zero | tr '\\000' 'a' | fold -w 99 > big.txt",
      "git add -A && git commit --quiet -m 'Review changes'",
      "printf 'work in progress\\n' > notes.txt",
    ].join(" && "),
  );
  const submitted = await tool("submit_for_review", {
    summary: "Changed, removed, renamed and added files.",
    criteria_mapping: ticket.criteria.map((c, i) => ({ criterion_id: c.id, status: i === 0 ? "covered" : "partial", note: `note ${i + 1}` })),
    open_concerns: ["big.txt is large"],
  });
  const again = await tool("submit_for_review", { summary: "Again." });
  let asked = "accepted";
  try {
    await tool("ask_human", { question: "Anything else?" }, 10000);
  } catch (e) {
    asked = e.message;
  }
  say("STUB_REVIEW " + JSON.stringify({ submitted, again, asked }));
  result({ result: "Done.", total_cost_usd: 0.003 });
  return 0;
}

/** Calls a tool again while the hub is unreachable (restarting), for up to a minute. */
async function patiently(call) {
  for (let attempt = 1; ; attempt++) {
    try {
      return await call();
    } catch (e) {
      if (attempt >= 60) throw e;
      await sleep(1000);
    }
  }
}

/** plan-wait: a plan to approve (or revise), then the question, then a stream of lines. */
async function planAndWait(ticket, lines) {
  let steps = ["Write before.txt", "Ask which word to use and write it to answer.txt"];
  for (;;) {
    const decision = JSON.parse(
      await tool("propose_plan", { steps, risks: ["None: this is the stub agent"], files_expected: ["before.txt", "answer.txt"] }, 3600_000),
    );
    if (decision.decision === "approve") break;
    steps = [...steps, `Also: ${decision.notes}`];
  }
  bash("printf 'before\\n' > before.txt && git add before.txt && git commit --quiet -m 'Add before.txt'");
  const before = sh("git rev-parse HEAD");
  session.add({ step: "asking", question: "Which word?" });
  const { answer, asked } = await askUntilAnswered("Which word?", { context: "It goes into `answer.txt`.", options: ["alpha", "beta"] });
  if (answer === null) {
    say("No answer; giving up.");
    result({ num_turns: 3, result: "gave up waiting", total_cost_usd: 0.001 });
    return 0;
  }
  for (let i = 1; i <= lines; i++) {
    say(`Line ${i} of ${lines}`);
    await sleep(250);
  }
  await patiently(() => tool("report_progress", { step: "Writing answer.txt", note: answer }));
  return finishWait(ticket, answer, { asked, before_sha: before }, true);
}

/** The wait scenarios after the question: write the answer, commit, report, submit. */
async function finishWait(ticket, answer, extra, retry = false) {
  bash(`printf '%s\\n' '${answer.replaceAll("'", "")}' > answer.txt && git add answer.txt && git commit --quiet -m 'Add answer.txt'`);
  const stubFacts = {
    ...facts(),
    mode,
    session_id: sessionId,
    answer,
    git_log: sh("git log --format='%H %s' main..HEAD"),
    ...extra,
  };
  say("STUB_FACTS " + JSON.stringify(stubFacts));
  await (retry ? patiently(() => submit(ticket)) : submit(ticket));
  result({ result: "Done.", total_cost_usd: 0.002, usage: { input_tokens: 10, output_tokens: 2 } });
  return 0;
}

async function connect() {
  try {
    await mcp("tools/list", {}, undefined, 10000);
    return true;
  } catch {
    return false;
  }
}

async function resume() {
  // Like Claude Code 2.1.310: an unknown session ends the process before the init event, with an error result.
  if (!fs.existsSync(sessionFile)) {
    const error = `No conversation found with session ID: ${sessionId}`;
    process.stderr.write(error + "\n");
    out({ type: "result", subtype: "error_during_execution", is_error: true, num_turns: 0, total_cost_usd: 0, errors: [error] });
    return 1;
  }
  const entries = session.read();
  const scenario = entries.find((e) => e.scenario)?.scenario;
  init(await connect());
  if (scenario === "wait-badresume") {
    out({ type: "result", subtype: "error_during_execution", is_error: true, num_turns: 1, result: "could not continue the session" });
    return 1;
  }
  const answer = /The answer is: (.*)/.exec(prompt)?.[1] ?? null;
  say(`Resumed with the answer ${answer}.`);
  session.add({ step: "resumed", answer });
  const ticket = JSON.parse(await tool("get_ticket", {}));
  return finishWait(ticket, answer, {
    asked: 0,
    before_sha: entries.find((e) => e.before_sha)?.before_sha,
    earlier_steps: entries.map((e) => e.step).filter(Boolean),
  });
}

async function fromSummary() {
  session.add({ step: "summary" });
  init(await connect());
  const answer = /^ {2}Answer: (.*)$/m.exec(prompt)?.[1] ?? null;
  const ticket = JSON.parse(await tool("get_ticket", {}));
  say(`Taking over from a summary; the answer was ${answer}.`);
  return finishWait(ticket, answer, {
    asked: 0,
    before_sha: sh("git log --format=%H -1 -- before.txt"),
    summary_git_log: /git log of the run branch, newest first:\n([\s\S]*?)\n\n/.exec(prompt)?.[1] ?? null,
    summary_has_question: prompt.includes("You asked: Which word?"),
  });
}

async function main() {
  if (mode === "resume") return resume();
  if (mode === "summary") return fromSummary();

  // Like Claude Code: connect first, report the MCP status in the init event, carry on even when it failed.
  const connected = await connect();
  init(connected);
  if (!connected) {
    say("The Tikai tools are missing; working without them.");
    await sleep(30000);
    result({ num_turns: 1, result: "done without tools", total_cost_usd: 0 });
    return 0;
  }

  const ticket = JSON.parse(await tool("get_ticket", {}));
  const scenario = /stub:([a-z-]+(?::\d+)?)/.exec(ticket.description)?.[1] ?? "ok";
  session.add({ scenario });
  out({ type: "assistant", message: { role: "assistant", content: [{ type: "thinking", thinking: `Scenario ${scenario}.` }, { type: "text", text: `Working on ${ticket.key}.` }] } });

  if (scenario.startsWith("plan-wait")) {
    return planAndWait(ticket, Number(scenario.split(":")[1] ?? 0));
  }
  if (scenario.startsWith("wait")) {
    bash("printf 'before\\n' > before.txt && git add before.txt && git commit --quiet -m 'Add before.txt'");
    const before = sh("git rev-parse HEAD");
    say(`STUB_COMMIT ${before}`);
    session.add({ step: "committed", before_sha: before });
    if (scenario === "wait-nosession") fs.rmSync(sessionFile);
    else session.add({ step: "asking", question: "Which word?" });
    const { answer, asked } = await askUntilAnswered("Which word?");
    if (answer === null) {
      say("No answer; giving up.");
      result({ num_turns: 3, result: "gave up waiting", total_cost_usd: 0.001 });
      return 0;
    }
    return finishWait(ticket, answer, { asked, before_sha: before });
  }
  if (scenario === "review") {
    return reviewChanges(ticket);
  }
  if (scenario === "no-commit") {
    let first = "accepted";
    try {
      await submit(ticket);
    } catch (e) {
      first = e.message;
    }
    bash("printf 'hello\\n' > hello.txt && git add hello.txt && git commit --quiet -m 'Add hello.txt'");
    await submit(ticket);
    say("STUB_REVIEW " + JSON.stringify({ first }));
    result({ result: "Done.", total_cost_usd: 0.002 });
    return 0;
  }
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
      say("No answer; giving up.");
    }
    result({ num_turns: 3, result: "gave up waiting", total_cost_usd: 0.001 });
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
  say("STUB_FACTS " + JSON.stringify(stubFacts));

  if (scenario !== "no-submit") {
    await submit(ticket);
  }
  result({
    result: "Done.",
    total_cost_usd: 0.0123,
    usage: { input_tokens: 100, output_tokens: 20, cache_creation_input_tokens: 5, cache_read_input_tokens: 7 },
  });
  return 0;
}

// process.exit() drops output still being written when stdout is asynchronous (after the big scenario's 1.2 MB
// line the result line got lost once in CI), so exit only after both streams have flushed.
const exitAfterFlush = (code) => process.stdout.write("", () => process.stderr.write("", () => process.exit(code)));

main().then(exitAfterFlush, (error) => {
  process.stderr.write(`stub failed: ${error.stack ?? error}\n`);
  exitAfterFlush(2);
});
