# Self-hosting Tikai

Tikai runs with one `docker compose up` on Linux or on Windows with Docker Desktop (WSL2 backend, Linux containers).

## Start

```powershell
git clone https://github.com/linsrap/tikai.git
cd tikai
Copy-Item .env.example .env      # Linux/macOS: cp .env.example .env
# Edit .env: set TIKAI_SECRET (see below) and DB_PASSWORD to something only you know.
docker compose --profile agents build   # the agent images (Claude Code); once, and after updates
docker compose up -d --build
```

Open <http://localhost:8080>. While no account exists, Tikai shows the setup page: the account you create there is the **admin** account. After that, sign-up is closed; everyone else needs the admin (more users come in a later version).

## Settings (`.env`)

| Variable | Default | Meaning |
| --- | --- | --- |
| `TIKAI_SECRET` | none, **required** | Encrypts the hub's key ring, which protects login cookies, stored git credentials and LLM keys. Random, at least 32 characters (see below). |
| `DB_PASSWORD` | `tikai` | Password of the Postgres user. Change it before the first start. |
| `TIKAI_BIND` | `127.0.0.1` | Address the web app listens on. `127.0.0.1` = this machine only. Use `0.0.0.0` to reach it from your network, and put a TLS reverse proxy in front: Tikai itself speaks plain HTTP. |
| `TIKAI_PORT` | `8080` | Port of the web app. |

### `TIKAI_SECRET`

Make one random value and put it in `.env`:

```powershell
# PowerShell (Windows PowerShell 5.1 and PowerShell 7)
$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
# Linux/macOS
openssl rand -base64 32
```

Keep a copy outside the server (a password manager). The keys in the `tikai_data` volume are encrypted with it, so a copy of the volume is useless without the secret, and so is the volume to you if the secret is lost.

- **Missing or shorter than 32 characters:** `docker compose up` refuses to start (missing), or the hub stops at startup with a message that says so (short).
- **Wrong secret** (for example a typo after moving to a new server): the hub stops at startup with "Data Protection key … cannot be decrypted". Nothing is changed; put the right secret back and start again.
- **Secret lost:** put a new secret in `.env`, stop the hub (`docker compose stop hub`), delete the old keys (`docker compose run --rm --no-deps --entrypoint sh hub -c "rm -rf /data/keys"`), then `docker compose up -d`. Everyone logs in again, and the stored repository credentials and LLM keys have to be entered again. Projects, repositories and tickets are kept.
- **Changing the secret** is not supported yet (it works like a lost secret).

**Upgrading from a version without `TIKAI_SECRET`:** add it to `.env` and start as usual. At the first start the hub encrypts the existing keys with it (the log says "Encrypted … Data Protection key(s)"); sessions and stored credentials keep working.

## What runs

| Service | Purpose |
| --- | --- |
| `hub` | API, web app, background workers. Waits until the database is healthy, applies database migrations at startup. |
| `db` | Postgres 17. Not reachable from outside the compose network. |
| `docker-proxy` | Limited access to Docker: the hub starts agent containers through it. |
| agent containers | One per agent run, started by the hub (not by compose): `tikai-agent-<run-id>`, plus short-lived `tikai-prep-…` and `tikai-seed-…` containers while a run is prepared. |

Data lives in three named volumes: `tikai_pg` (database), `tikai_data` (the keys that sign login cookies and encrypt stored credentials, and the repository mirrors) and `tikai_runs` (one directory per agent run: its clone and the agent's session). `docker compose down` keeps them; `docker compose down -v` **deletes** `tikai_pg` and `tikai_data`. `tikai_runs` is created by Docker with the first agent run, so `down -v` leaves it: remove it with `docker volume rm tikai_runs`. Keep `tikai_data` and `tikai_pg` together: without the keys in `tikai_data`, stored repository credentials cannot be decrypted and have to be entered again.

## LLM providers

Tikai never ships or shares an LLM key: you bring your own. Under **Settings → Your LLM providers** (or on a project page, for a provider the whole project uses) add a provider with a name, a model (default `claude-opus-5-5`) and an Anthropic API key (`sk-ant-api03-…`) from <https://platform.claude.com>. API usage is billed there, separately from a Claude Pro/Max subscription; a subscription token from `claude setup-token` (`sk-ant-oat01-…`) is not an API key and is refused. The key is stored encrypted and never shown again; Tikai shows only its last four characters.

**Test connection** sends one short message ("Reply with OK") with that key and model, which costs a fraction of a cent, and shows the answer and the time, or what went wrong (key rejected, model not found, rate limited, provider not reachable).

The optional **base URL** is for a gateway or proxy in front of the Anthropic API. Tikai sends the key as `Authorization: Bearer …` (Anthropic API version 2026-10-21); a gateway that only reads `X-Api-Key` will reject it.

## Projects and repositories

A project holds repositories (and, later, tickets). Its **slug** (URL name) and **ticket key** (`HUB` in `HUB-42`) cannot be changed after creation.

A repository is either:

- **Local**: a new, empty repository that lives only in Tikai (one empty "Initial commit" on the default branch, `main` unless you choose another). There is no way to clone it from outside; Tikai is not a git host.
- **Mirror of a remote**: Tikai clones the remote (GitHub, GitLab, Gitea/Forgejo, any git server) and fetches from it every 5 minutes, or on "Fetch now". The default branch is taken from the remote unless you enter one.

Credentials for a remote are stored encrypted and are never shown again after saving; you can replace them at any time.

| Remote URL | Credential |
| --- | --- |
| `https://…` (public) | None. |
| `https://…` (private) | An **access token** (GitHub fine-grained or classic token, GitLab/Gitea access token). Read access is enough for now; pushing merges comes later. The user name is optional (`x-access-token` by default); some servers (e.g. Bitbucket) need your real user name. Do not put the token in the URL; Tikai rejects that. |
| `ssh://…` or `git@host:path` | An **SSH private key** in OpenSSH format, without a passphrase (best: a deploy key made only for Tikai). The server's host key is trusted on first use and stored with the mirror; if it changes later, fetching fails with "Host key verification failed". To accept a new host key, delete the repository in Tikai and add it again. |

Only `https` and `ssh` remotes are allowed. Local paths, `file://`, plain `http://` and git's `transport::` helpers are refused.

If a clone fails (wrong token, repository not found, no network), the repository shows the git error. Fix the credential and press **Retry clone**.

Advanced settings (environment variables of the `hub` service):

| Variable | Default | Meaning |
| --- | --- | --- |
| `Tikai__Git__FetchInterval` | `00:05:00` | Time between fetches of one repository. |
| `Tikai__Git__RemoteTimeout` | `00:30:00` | Limit for one clone or fetch. |

## Agent runs

On a ticket page, **Assign to agent** starts Claude Code on the ticket. The agent works in its own container on its own branch `tikai/<KEY-N>-<8 characters>`, reads the ticket over Tikai's MCP endpoint, proposes a plan and waits until it is approved, then implements, commits, and submits its work for review. Until the inbox exists, plans and questions are answered through the API (`POST /api/attention-items/{id}/answer`).

- **Images:** `docker compose --profile agents build` builds `tikai/agent-base:local` and `tikai/agent-claude-code:local` (Claude Code at the version pinned in `images/agent-claude-code/Dockerfile`). Without them a run fails with "The agent image … is not on this Docker host".
- **Which key:** the agent uses the API key of the project's LLM provider, or the assigning person's own provider if the project has none (a picker shows when there is a choice), with that provider's model. Its API usage is billed to that key. The key reaches only that run's container, as an environment variable; Tikai removes it (and the run's MCP token) from everything it stores of the agent's output. Anyone who can run `docker inspect` on the host can see it.
- **One run at a time** per ticket and per repository. A project with several repositories asks which one the agent works on.
- **The sandbox:** non-root (uid 1000), read-only root file system with a writable `/tmp`, no Linux capabilities, 4 GiB memory, 2 CPUs. The container mounts only its own directory of `tikai_runs`; it never sees `tikai_data`, the database, git credentials or the Docker socket. The hub writes that directory through Docker (as root-owned, read-only files: the MCP config and the ticket), and never runs git in it.
- **Network (not restricted yet):** agents join the `tikai_agents` network. They reach the hub's MCP endpoint, but not the database or the docker proxy. They **can reach the internet and your local network** (and on Docker Desktop the host through `host.docker.internal`): an egress proxy with an allowlist comes in a later version. Only assign tickets on repositories you would let an agent with internet access work on.
- **What you see:** the ticket page lists the runs with state, branch, last progress step, failure reason, cost, and the transcript (`GET /api/runs/{id}/events`). A failed run also shows up in the attention items. The full Claude Code transcript stays in the run's directory.
- **Hub restarts** do not stop agents: after the restart the hub follows them again. Run directories are kept after a run ends (cleanup comes with merging).

## Health

- `http://localhost:8080/health`: the process runs.
- `http://localhost:8080/health/ready`: the process runs and the database is reachable. `docker compose ps` shows the hub as `healthy` when this succeeds.

## Restart and re-login checklist

Run this after an update or when something seems off. Each line says what you should see.

1. `docker compose ps`: `hub` and `db` are `healthy`, `docker-proxy` is `Up`. `docker images tikai/agent-claude-code` lists the agent image.
2. Open <http://localhost:8080> and log in. You see your name with "(admin)".
3. `docker compose restart hub`, wait until it is `healthy` again, reload the page: you are **still logged in** (the cookie keys are kept in the `tikai_data` volume).
4. `docker compose down` then `docker compose up -d`, reload: **still logged in**, your account still exists.
5. Open the page in a private window: you see the **login** page, not the setup page (sign-up stays closed).
6. Log out, log in again: works. Five wrong passwords in a row lock the account for 5 minutes.
7. Open **Projects** and a project: its repositories are **Ready**, and mirrors show a "fetched" time no older than about 6 minutes. "Branches" lists the branches.

If step 3 or 4 logs you out, the `tikai_data` volume was not kept or not writable; check `docker compose logs hub` for Data Protection warnings. If step 5 shows the setup page, the database volume was lost. If step 7 shows "The stored credential cannot be decrypted", the keys in `tikai_data` were lost: enter the credentials again.

## Update

```powershell
git pull
docker compose --profile agents build
docker compose up -d --build
```

A run that is working during the update keeps running and is picked up again by the new hub.

Database migrations run automatically when the hub starts.
