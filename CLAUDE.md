# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What Tikai is

Tikai (ticket + AI) is an open-source, non-profit app that combines a **ticket system** (in the spirit of YouTrack) with **git**, so AI agents can develop quickly while humans stay firmly in control.

Core ideas:
- Tickets are where work starts: an agent picks up a ticket, works on a branch, and the result is linked back to the ticket.
- **Multi-agent support**: several agents can work in parallel, and an agent can **ask a human** a question from inside the ticket instead of guessing.
- **Strong human code review** is a core feature, not an afterthought. AI-written changes are always reviewed and approved by a human before they merge.

## MVP scope

- **Claude Code is the only agent** in the MVP. Tikai orchestrates Claude Code and does not build its own agent.
- Users **bring their own LLM API key**. Tikai never ships or proxies a shared key.
- Self-hosted: the whole stack starts with a single `docker compose up`.

## Tech stack

- **Backend:** .NET (C#)
- **Database:** PostgreSQL
- **Frontend:** React + TypeScript
- **Deployment:** Docker Compose, Linux containers

## Platform requirements

- The main developer works on **Windows**. Everything (builds, scripts, tooling, docker compose) must work on Windows with Docker Desktop running Linux containers.
- Prefer cross-platform tooling: `dotnet` CLI, npm/pnpm scripts, or PowerShell-compatible commands. Avoid bash-only scripts unless there is a PowerShell equivalent.
- Mind line endings (`.gitattributes`) and path separators.

## Licensing and contributions

- License: **AGPL-3.0** (see `LICENSE`).
- Contributions use the **DCO** (Developer Certificate of Origin): commits are signed off with `git commit -s`.
- **No per-file license headers.** The root `LICENSE` file covers the whole repo.

## Working conventions for Claude Code

- Keep changes small and reviewable. One ticket or concern per branch/PR.
- Never commit secrets or API keys. Use environment variables / `.env` files, which are git-ignored, and provide `.env.example`.
- Ask when a requirement is unclear instead of guessing. This mirrors the "agents ask humans" principle of the product.
- When adding a dependency, note why in the PR description.

## Planning docs (`aidocs/`)

- `aidocs/` holds the local, git-ignored working docs for building Tikai. **Read `aidocs/design-doc.md` before starting any feature work.** It is the source of truth for architecture, data model, MCP tools and the MVP phases.
- Tikai is built in small steps, not all at once. Work on one item from "MVP phases" at a time, with a plan in `aidocs/plans/` (see `aidocs/README.md`).
- Don't implement features from later phases unless asked.

## Repository layout

```
Tikai.slnx                    .NET solution (global.json pins SDK 10.0.402; Directory.*.props hold shared settings and package versions)
src/
  Tikai.Api/                  ASP.NET Core host and composition root; feature folders Auth/, Projects/ (projects, repositories, git jobs, fetch scheduler), Tickets/ (tickets, acceptance criteria, refine spec), Llm/ (providers API), Security/ (key ring encryption, secret protector), Runs/ (runs, run tokens, `RunToken` auth scheme, `IRunTools`, `RunService.StartAsync` (assign and retry), run endpoints incl. the board list, `RunStore`, `RunResumes`), Attention/ (attention items, waiter, inbox API: answer, dismiss, retry), Live/ (SignalR hub `/hubs/live`, `LiveUpdates` from the event bus, Origin check), Mcp/ (`/mcp` endpoint)
  Tikai.Core/                 domain: entities, state machines (`RunStateMachine`, `RunExit`), interfaces (`IAgentAdapter`); no packages, no references
  Tikai.Data/                 EF Core, migrations, job queue, ticket numbers (counter on projects, UPDATE … RETURNING), run event numbers (counter on agent_runs, `RunEventLog`)
  Tikai.Git/                  GitService (git CLI, isolated config, credentials via env only), ProcessRunner
  Tikai.Orchestrator/         run lifecycle on Docker.DotNet.Enhanced: start and resume job (`RunStarter`, queue `runs`, `RunInspect` helper), `RunMonitor` (logs → run_events, exit → state), `RunReconciler` (startup and every minute: re-attach, block timeout, abandoned runs, orphan containers; decisions in the pure `RunReconciliation.Plan`), `RunControl` (cancel), `IRunStore` (implemented in Api)
  Tikai.Mcp/                  MCP tools on the official C# SDK (`TikaiTools`), `RunScope` from the token's claims, `IRunTools` (implemented in Api)
  Tikai.Review/               diff model, analysis, ordering
  Tikai.Llm/                  IChatClientFactory (official Anthropic SDK via Microsoft.Extensions.AI), connection test, Refine/ (refine spec), Prompts/ (versioned prompt files, embedded)
  Tikai.Adapters.ClaudeCode/  IAgentAdapter for Claude Code: command line (start and `--resume`), mcp.json, stream-json parser, Prompts/ (versioned start, resume and summary prompts, embedded)
tests/
  Tikai.ArchitectureTests/    enforces the project reference rules below
  Tikai.Core.Tests/           pure unit tests (also the pure parts of the adapter and the orchestrator: parsing, command line, tar, prep script)
  Tikai.IntegrationTests/     Testcontainers Postgres 17 (needs Docker running): job queue, worker, event bus, entity conventions, auth, projects API, tickets API, key ring encryption, LLM providers API;
                              LLM client and refine spec against an in-process fake of the Messages API (no key needed);
                              MCP tools, run tokens and blocking calls through the SDK's own `McpClient`, attention API;
                              real git: GitService, token auth via in-process git http-backend, SSH via an sshd container (needs `ssh` on PATH for git);
                              agent runs with real containers: builds images/agent-base and tests/agent-stub as :test, a hub on a real port (Kestrel); pause, resume, fallback and reconciliation (the reconciler runs only where `Test:Reconciler` is on);
                              inbox API (order, excerpt, dismiss, retry), live hub through the SignalR client (cookie only, Origin, groups, resync)
  agent-stub/                 stand-in for Claude Code in tests (same command line, MCP over HTTP, stream-json, a session file for `--resume`, the summary prompt; scenario from the ticket description, e.g. `stub:plan-wait:40`)
web/                          React + TypeScript (Vite, TanStack Router + Query + Virtual, openapi-fetch, @microsoft/signalr, react-markdown + remark-gfm, ESLint, Prettier, Vitest)
  src/live/                   one hub connection (`live`), group watches re-sent after reconnects, query invalidation per message, `LogBuffer` + `useRunLog` (live log from the last seq), `RunLog` (virtualized)
  src/inbox/                  inbox query and actions, `AttentionCard`, `AnswerForms` (also on the ticket page)
  e2e/                        Playwright: `loop.spec.ts`, `stack.mjs` (compose project tikai-e2e on :18092, images :e2e, `e2e.env`), `compose.e2e.yml`
  openapi.json                API description, written by `dotnet build` of Tikai.Api (committed; CI checks it is current)
  src/api/schema.d.ts         typed API client types from openapi.json (`npm run api:generate`; committed; CI checks it)
images/hub/Dockerfile         hub image: web build → API publish → aspnet runtime with git, serves the SPA from wwwroot
images/agent-base/            agent base image (node:24 trixie slim, git, uid 1000)
images/agent-claude-code/     Claude Code on agent-base, version pinned by ARG CLAUDE_CODE_VERSION
docs/                         contributing.md, self-hosting.md (start, settings, restart and re-login checklist)
compose.yml                   self-hosting: hub, db (Postgres 17), docker-proxy, agent images (profile `agents`, build only); networks default, agents; volumes data, pg (the runs volume is made by Docker with the first run)
compose.dev.yml               Postgres 17 for local development
.github/workflows/ci.yml      CI on Linux: .NET build/test/format, web lint/format/test/build, hub image build, generated-file checks, Playwright (npm run e2e)
```

Auth: ASP.NET Core Identity with a cookie (`tikai.auth`). The first sign-up becomes admin, then sign-up closes. Every unsafe `/api` request needs the antiforgery header `X-XSRF-TOKEN` (value from the `XSRF-TOKEN` cookie set by `GET /api/auth/antiforgery`; fetch it again after login and logout). New API endpoints go under the `/api` group so they get this check.

Git: every git call goes through `GitService` (never `Process.Start("git")` elsewhere): argument lists, `--git-dir`, no system/global config, no prompts, `GIT_ALLOW_PROTOCOL` from `Tikai:Git:AllowedProtocols`. Credentials only through env (`GIT_CONFIG_*` extraHeader, `GIT_SSH_COMMAND` with a temp key file), never in arguments, URLs or repo config. Mirrors live at `<data>/repos/<project-id>/<repo-id>.git` (paths from ids only); remote branches in `refs/remotes/origin/*`, the hub's own branches in `refs/heads/*`. Writes to mirrors go through the `git` job queue.

Secrets: `Tikai:EncryptionSecret` (`TIKAI_SECRET` in compose, 32+ characters) encrypts the Data Protection key ring; the hub refuses to start without it or with the wrong one (`Security/KeyRingStartup`). `appsettings.Development.json` has a development-only secret. Stored secrets (git credentials, LLM keys) go through `ISecretProtector` with their own purpose string, are write-only in the API, and never appear in logs (`ToString` of credential and connection records hides them). LLM calls go through `IChatClientFactory`, never `new AnthropicClient()` elsewhere (it would read `ANTHROPIC_*` env vars).

Tickets: numbers come from `TicketNumbers.NextAsync` inside the insert transaction (never `MAX()+1`) and are never reused. Ticket markdown is rendered only through `web/src/tickets/Markdown.tsx` (raw HTML dropped, unsafe URLs blanked; no `dangerouslySetInnerHTML`). Criteria keep their ids across edits; review coverage will reference them.

MCP: `/mcp` is stateless Streamable HTTP (protocol 2026-07-28) and accepts only a run token (`Authorization: Bearer tikai_run_…`, scheme `RunToken`; the login cookie is ignored there, and the token does not open `/api`). Tokens come from `RunService` (stored as SHA-256 only; invalid once the run has ended; `RenewTokenAsync` replaces one). Tools take no run, ticket or project ids: everything comes from `RunScope`. Tool argument names are snake_case (the C# parameter names); errors the agent can fix are thrown as `McpException`. Blocking tools (`ask_human`, `propose_plan`) create or merge an attention item (same run, latest blocking item, same request hash → reused, stored answer returned at once) and wait through `IAttentionWaiter`, which registers before it reads the row; a progress notification and a re-read every `Tikai:Mcp:HeartbeatInterval` (30 s). The database is the source of truth for answers; nothing in memory must survive a restart.

Runs: `POST /api/projects/{slug}/tickets/{n}/runs` creates a queued run (one active run per ticket and per repository, enforced by partial unique indexes) and a `runs` job. `RunStarter` bundles the default branch in the mirror (the only git the hub runs), writes the run directory through the Docker archive API into a never-started `seed` container (tar headers set owner and mode: run root, `mcp.json` and `.tikai/ticket.md` root-owned and read-only; `work/`, `home/`, `claude/` owned by uid 1000), clones in a `prep` container (agent image, agent uid, no network), renews the token, and starts the agent. The hub never mounts the runs volume and never runs git or follows symlinks in a run directory; the subpath mount is exactly the run's GUID. The sandbox (user, read-only root, cap-drop, limits, network, `json-file` log driver) is set only in `DockerContainerRuntime.CreateAsync`; adapters give image, command and environment. State changes are compare-and-set along `RunStateMachine`; a container's end goes through `RunExit.Map` (exit code 0 alone means nothing; the `system/init` MCP status is checked as soon as it arrives). Agent output is redacted (`SecretRedactor`) and trimmed (`RunEventPayload`) before it becomes `run_events`, numbered from `agent_runs.last_event_seq`; after a hub restart the monitor re-reads the log and skips `log_lines_read` lines. Docker log timestamps are not used (Docker splits long lines into 16 KB chunks, each with its own timestamp).

Pause and resume: a run is `blocked` while one of its blocking items is open (`AttentionService.RaiseAsync` blocks it in the transaction that locks the run row) and runs again only when the agent has every answer (`delivered_at`: set by the tool that returns it, or by the resume start), not when it is stored. A container end while blocked keeps the run blocked without a container (`EndAsync` with `Block` on a blocked run); an answered but undelivered item maps to `Block` too. A resume is requested (`RunResumes.RequestIfReadyAsync`, run row locked, at most one live `runs` job per run) by the answer, by a container end and by the reconciler: blocked, no container, nothing open, no cancel. Every end is container-scoped (`RunEnd.ContainerId` must be the run's `container_id`). The resume renews the token, rewrites only the adapter's root-owned files (`mcp.json`) through a seed container, reads session file, `git log` and `git status` in an `inspect` container (session line first, sections split by a random marker), then starts `--resume <session_id>` with `run-resume.v1` or a fresh session with `run-summary.v1`; a resumed container that ends with an agent error is retried once as a summary (`RetryWithSummary`). The block timeout counts from the oldest open item's `created_at` by the database clock (`now()`), checked by the reconciler; `ToolCallTimeout` must be at least `BlockTimeout` + 15 min. Ending a run dismisses its open blocking items (answering one is 409). Row locks: `SELECT 1 … FOR UPDATE`, not `FromSql("SELECT * …")` (the entities map `xmin`).

Live updates: SignalR hub `/hubs/live`, login cookie only (a run token gets 401), requests with a foreign `Origin` get 403, no hub method changes data (actions stay REST with antiforgery). Messages carry ids and keys only (`RunChanged`, `AttentionChanged` to groups `all` and `project:<id>`; `RunEvents` to `run:<id>`; `Resync` after the event bus reconnected); the browser re-reads through the REST API. Groups are per connection: the client re-sends its watches after a reconnect and re-reads what it shows. The live log asks `GET /api/runs/{id}/events?afterSeq=<last>` and drops seqs it has; seqs become visible in order (the counter row lock is held until commit), so there are no gaps. New views that change live: add their query keys to `staleKeys`/`liveKeys` in `web/src/live/live.ts`.

Run cost (`RunCost.After`): `cost_usd` is the agent's session total (Claude Code's `total_cost_usd` covers a resumed session's earlier containers, checked with a real run) plus `cost_usd_before_session` (earlier sessions, set when a summary attempt starts); `cost_tokens` adds each container's tokens, summed from per-message `usage` (the result's `usage` covers only the last process).

Inbox: open items, blocking (an agent waits) first, then newest. Questions and plans are answered (`/answer`), failures and reviews dismissed (`/dismiss`), failures retried (`/retry`: a new run with the same repository and provider); starting a run dismisses the ticket's open failure items. Blocking items are never dismissed by people (cancel the run).

Prompts: one versioned markdown file per prompt in `src/Tikai.Llm/Prompts/` (`refine-spec.v1.md`) or, for agents, in the adapter (`src/Tikai.Adapters.ClaudeCode/Prompts/run-start.v1.md`); a changed prompt is a new file. Structured output uses an explicit JSON schema with a `required` list: the Anthropic SDK silently drops a schema without one (no `output_config` is sent), and schemas inferred from classes with settable properties have none.

Project references: `Core` references nothing; `Data`, `Git`, `Llm`, `Review`, `Orchestrator`, `Mcp` and `Adapters.ClaudeCode` reference only `Core`; `Api` references everything. A new `src` project needs a rule in `tests/Tikai.ArchitectureTests/ProjectReferenceTests.cs`.

Commands (PowerShell or any shell, from the repo root):

```
dotnet build                             # warnings are errors
dotnet test                              # Microsoft.Testing.Platform (opted in via global.json); integration tests need Docker (agent tests reach the test hub at host.docker.internal; inside a container set TIKAI_TEST_AGENT_NETWORK and TIKAI_TEST_HUB_HOST, and on Docker Desktop TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal)
dotnet tool restore                      # once: installs dotnet-ef from dotnet-tools.json
dotnet ef migrations add <Name> --project src/Tikai.Data --startup-project src/Tikai.Data --output-dir Migrations
dotnet format --verify-no-changes        # style check, also run in CI
cd web; npm ci; npm run lint; npm run format:check; npm test; npm run build
docker compose -f compose.dev.yml up -d  # Postgres on 127.0.0.1:5432 (settings in .env, see .env.example)
dotnet run --project src/Tikai.Api       # API on http://localhost:5080; applies migrations at startup; /health (live), /health/ready (DB)
npm run dev --prefix web                 # web on http://localhost:5173, proxies /api and /hubs to the API
npm run e2e --prefix web                 # Playwright against its own compose stack (tikai-e2e, :18092, :e2e images; removed afterwards); once: npx playwright install chromium
npm run api:generate --prefix web        # after API changes (dotnet build updates web/openapi.json first)
docker compose --profile agents build    # agent images tikai/agent-base:local, tikai/agent-claude-code:local
docker compose up -d --build             # full stack on http://localhost:8080 (see docs/self-hosting.md)
```
