# Architecture

A short map for contributors. Details live in the code and in `CLAUDE.md`.

## Parts

| Part | What it does |
| --- | --- |
| `hub` | ASP.NET Core: REST API, web app, MCP endpoint (`/mcp`), SignalR (`/hubs/live`), background jobs (queue in Postgres). The only part that runs git on the mirrors. |
| `db` | Postgres 17: everything except git objects and keys. The source of truth for runs, answers and reviews. |
| `docker-proxy` | Lets the hub start containers without giving it the Docker socket. |
| agent containers | One per run (Claude Code), started by the hub. They see only their own run directory and talk to the hub only through MCP with a run token. |
| preview containers | A review round's app and its services, started from the repository's `tikai-preview.yml` in stock images (no image builds), on their own network. The hub serves each on its own host name. |

Volumes: `data` (key ring, repository mirrors; hub only), `pg`, `runs` (one directory per run; never mounted in the hub), `previews` (one directory per running preview; never mounted in the hub).

## The loop

1. A person writes a ticket (optionally refined by an LLM) and starts a run.
2. The hub clones the default branch into the run directory and starts the agent.
3. The agent works on its branch and asks through MCP (`ask_human`, `propose_plan`). A blocking call waits; if the container ends meanwhile, the run is resumed once all answers are in.
4. `submit_for_review`: the hub imports the branch as a git bundle into the mirror. Then the review: diff, analysis, comments. Request changes resumes the agent for a new round.
5. Approve: the hub merges (or pushes the branch) and pushes to the remote. The run directory is removed.

Before approving, a person can start a round as a preview: it is cloned like a run, set up and started in sandboxed containers, and opened at `<id>.preview.<host>`, never on the hub's own origin, so the app never gets the hub's cookies.

## Decisions

- **A. A run clone, not a worktree.** A worktree's `.git` points back into the mirror, so mounting it alone does not work, and mounting the mirror would give the agent every branch. Each run gets its own clone; work comes back as a git bundle that the hub checks before importing.
- **B. `data` never reaches an agent.** It holds the key ring and all mirrors. Agents mount only their own subdirectory of `runs` (Docker volume subpath mount); the hub writes into a run directory only through the Docker archive API and never follows its symlinks.
- **C. Blocking MCP calls may stay open for hours.** The call stays open with progress notifications every 30 s and a long client timeout. Answers live in the database, not in the open call: if the call or container is gone, the agent is resumed with `--resume <session>` in a new container, or, if that fails, in a new session with a summary.
- **F. The key ring is encrypted with `TIKAI_SECRET`.** Data Protection keys (cookies, stored credentials, LLM keys) are encrypted with AES-GCM using a key derived from the secret. A copy of the `data` volume alone is useless.
