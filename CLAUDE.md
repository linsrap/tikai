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
  Tikai.Api/                  ASP.NET Core host and composition root; feature folders Auth/, Projects/ (projects, repositories, git jobs, fetch scheduler), Llm/ (providers API), Security/ (key ring encryption, secret protector)
  Tikai.Core/                 domain: entities, state machines, interfaces; no packages, no references
  Tikai.Data/                 EF Core, migrations, job queue
  Tikai.Git/                  GitService (git CLI, isolated config, credentials via env only), ProcessRunner
  Tikai.Orchestrator/         run lifecycle, Docker
  Tikai.Mcp/                  MCP tools
  Tikai.Review/               diff model, analysis, ordering
  Tikai.Llm/                  IChatClientFactory (official Anthropic SDK via Microsoft.Extensions.AI), connection test
  Tikai.Adapters.ClaudeCode/  IAgentAdapter for Claude Code
tests/
  Tikai.ArchitectureTests/    enforces the project reference rules below
  Tikai.Core.Tests/           pure unit tests
  Tikai.IntegrationTests/     Testcontainers Postgres 17 (needs Docker running): job queue, worker, event bus, entity conventions, auth, projects API, key ring encryption, LLM providers API;
                              LLM client against an in-process fake of the Messages API (no key needed);
                              real git: GitService, token auth via in-process git http-backend, SSH via an sshd container (needs `ssh` on PATH for git)
web/                          React + TypeScript (Vite, TanStack Router + Query, openapi-fetch, ESLint, Prettier, Vitest)
  openapi.json                API description, written by `dotnet build` of Tikai.Api (committed; CI checks it is current)
  src/api/schema.d.ts         typed API client types from openapi.json (`npm run api:generate`; committed; CI checks it)
images/hub/Dockerfile         hub image: web build → API publish → aspnet runtime with git, serves the SPA from wwwroot
docs/                         contributing.md, self-hosting.md (start, settings, restart and re-login checklist)
compose.yml                   self-hosting: hub, db (Postgres 17), docker-proxy; volumes data, runs, pg
compose.dev.yml               Postgres 17 for local development
.github/workflows/ci.yml      CI on Linux: .NET build/test/format, web lint/format/test/build, hub image build, generated-file checks
```

Auth: ASP.NET Core Identity with a cookie (`tikai.auth`). The first sign-up becomes admin, then sign-up closes. Every unsafe `/api` request needs the antiforgery header `X-XSRF-TOKEN` (value from the `XSRF-TOKEN` cookie set by `GET /api/auth/antiforgery`; fetch it again after login and logout). New API endpoints go under the `/api` group so they get this check.

Git: every git call goes through `GitService` (never `Process.Start("git")` elsewhere): argument lists, `--git-dir`, no system/global config, no prompts, `GIT_ALLOW_PROTOCOL` from `Tikai:Git:AllowedProtocols`. Credentials only through env (`GIT_CONFIG_*` extraHeader, `GIT_SSH_COMMAND` with a temp key file), never in arguments, URLs or repo config. Mirrors live at `<data>/repos/<project-id>/<repo-id>.git` (paths from ids only); remote branches in `refs/remotes/origin/*`, the hub's own branches in `refs/heads/*`. Writes to mirrors go through the `git` job queue.

Secrets: `Tikai:EncryptionSecret` (`TIKAI_SECRET` in compose, 32+ characters) encrypts the Data Protection key ring; the hub refuses to start without it or with the wrong one (`Security/KeyRingStartup`). `appsettings.Development.json` has a development-only secret. Stored secrets (git credentials, LLM keys) go through `ISecretProtector` with their own purpose string, are write-only in the API, and never appear in logs (`ToString` of credential and connection records hides them). LLM calls go through `IChatClientFactory`, never `new AnthropicClient()` elsewhere (it would read `ANTHROPIC_*` env vars).

Project references: `Core` references nothing; `Data`, `Git`, `Llm`, `Review`, `Orchestrator`, `Mcp` and `Adapters.ClaudeCode` reference only `Core`; `Api` references everything. A new `src` project needs a rule in `tests/Tikai.ArchitectureTests/ProjectReferenceTests.cs`.

Commands (PowerShell or any shell, from the repo root):

```
dotnet build                             # warnings are errors
dotnet test                              # Microsoft.Testing.Platform (opted in via global.json); integration tests need Docker
dotnet tool restore                      # once: installs dotnet-ef from dotnet-tools.json
dotnet ef migrations add <Name> --project src/Tikai.Data --startup-project src/Tikai.Data --output-dir Migrations
dotnet format --verify-no-changes        # style check, also run in CI
cd web; npm ci; npm run lint; npm run format:check; npm test; npm run build
docker compose -f compose.dev.yml up -d  # Postgres on 127.0.0.1:5432 (settings in .env, see .env.example)
dotnet run --project src/Tikai.Api       # API on http://localhost:5080; applies migrations at startup; /health (live), /health/ready (DB)
npm run dev --prefix web                 # web on http://localhost:5173, proxies /api and /hubs to the API
npm run api:generate --prefix web        # after API changes (dotnet build updates web/openapi.json first)
docker compose up -d --build             # full stack on http://localhost:8080 (see docs/self-hosting.md)
```
