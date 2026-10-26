# Self-hosting Tikai

Tikai runs with one `docker compose up` on Linux or on Windows with Docker Desktop (WSL2 backend, Linux containers).

## Start

```powershell
git clone https://github.com/linsrap/tikai.git
cd tikai
Copy-Item .env.example .env      # Linux/macOS: cp .env.example .env
# Edit .env: set DB_PASSWORD to something only you know.
docker compose up -d --build
```

Open <http://localhost:8080>. While no account exists, Tikai shows the setup page: the account you create there is the **admin** account. After that, sign-up is closed; everyone else needs the admin (more users come in a later version).

## Settings (`.env`)

| Variable | Default | Meaning |
| --- | --- | --- |
| `DB_PASSWORD` | `tikai` | Password of the Postgres user. Change it before the first start. |
| `TIKAI_BIND` | `127.0.0.1` | Address the web app listens on. `127.0.0.1` = this machine only. Use `0.0.0.0` to reach it from your network, and put a TLS reverse proxy in front: Tikai itself speaks plain HTTP. |
| `TIKAI_PORT` | `8080` | Port of the web app. |

## What runs

| Service | Purpose |
| --- | --- |
| `hub` | API, web app, background workers. Waits until the database is healthy, applies database migrations at startup. |
| `db` | Postgres 17. Not reachable from outside the compose network. |
| `docker-proxy` | Limited access to Docker, for starting agent containers in a later version. |

Data lives in three named volumes: `tikai_pg` (database), `tikai_data` (the keys that sign login cookies and encrypt stored credentials, and the repository mirrors) and `tikai_runs` (agent work directories, later). `docker compose down` keeps them; `docker compose down -v` **deletes everything**. Keep `tikai_data` and `tikai_pg` together: without the keys in `tikai_data`, stored repository credentials cannot be decrypted and have to be entered again.

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

## Health

- `http://localhost:8080/health`: the process runs.
- `http://localhost:8080/health/ready`: the process runs and the database is reachable. `docker compose ps` shows the hub as `healthy` when this succeeds.

## Restart and re-login checklist

Run this after an update or when something seems off. Each line says what you should see.

1. `docker compose ps`: `hub` and `db` are `healthy`, `docker-proxy` is `Up`.
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
docker compose up -d --build
```

Database migrations run automatically when the hub starts.
