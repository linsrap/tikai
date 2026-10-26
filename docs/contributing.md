# Contributing to Tikai

Thanks for helping. This guide gets you from a fresh clone to a green build, on Windows or Linux.

## Prerequisites

| Tool | Version | Notes |
| --- | --- | --- |
| .NET SDK | 10.0.402 or a later 10.0 feature band | Pinned in `global.json`. |
| Node.js | 24 LTS or later | `web/.nvmrc` says 24. npm comes with Node. |
| Docker | Docker Desktop with the WSL2 backend (Windows) or Docker Engine (Linux) | Linux containers only. |
| git | any recent version | |

On Windows, use PowerShell or Windows Terminal. All commands below work there unchanged. No bash is needed.

## Get the code

```powershell
git clone https://github.com/linsrap/tikai.git
cd tikai
```

Line endings are normalized by `.gitattributes`: files are LF in the repo and in your working tree, except `*.ps1`, `*.cmd` and `*.bat`. Shell scripts that run inside Linux containers break with CRLF, so do not override this with `core.autocrlf`.

## Build and test

```powershell
# Backend
dotnet build
dotnet test
dotnet format --verify-no-changes

# Frontend
cd web
npm ci
npm run lint
npm run format:check
npm test
npm run build
```

`dotnet test` includes integration tests that start a Postgres container with Testcontainers, so Docker must be running.

Warnings are errors in the .NET build. `dotnet format` fixes most style issues for you (run it without `--verify-no-changes`), and `npm run format` does the same for the web app.

## Run locally

```powershell
Copy-Item .env.example .env                    # once; adjust if port 5432 is taken
docker compose -f compose.dev.yml up -d        # Postgres 17
dotnet run --project src/Tikai.Api             # API on http://localhost:5080
npm run dev --prefix web                       # web app on http://localhost:5173, proxies /api and /hubs
```

To run the whole stack as users will, see [self-hosting.md](self-hosting.md) (`docker compose up -d --build`, then <http://localhost:8080>).

When you change the API, `dotnet build` rewrites `web/openapi.json`; then run `npm run api:generate --prefix web` to update the typed client in `web/src/api/schema.d.ts`, and commit both. CI fails if either is stale.

The API applies EF Core migrations at startup. To add one after changing the model:

```powershell
dotnet tool restore    # once
dotnet ef migrations add <Name> --project src/Tikai.Data --startup-project src/Tikai.Data --output-dir Migrations
```

Review the generated SQL (`dotnet ef migrations script …`) before committing a migration.

## Project layout

The backend is split into projects with fixed dependency rules (`Core` references nothing; `Data`, `Git`, `Llm`, `Review`, `Orchestrator`, `Mcp` and the adapters reference only `Core`; `Api` is the composition root). `tests/Tikai.ArchitectureTests` enforces these rules. If you add a project, add its rule there.

## Commits and pull requests

- **Sign off every commit** (Developer Certificate of Origin): `git commit -s`. This adds a `Signed-off-by:` line with your name and email and certifies that you wrote the change or have the right to submit it under the project's license ([developercertificate.org](https://developercertificate.org/)).
- Keep pull requests small: one ticket or concern each.
- When you add a dependency, say why in the pull request description.
- Never commit secrets. `.env` is git-ignored; put example values in `.env.example`.

## License

Tikai is licensed under AGPL-3.0 (see `LICENSE`). By contributing, you agree that your contributions are licensed under the same terms. Files carry no per-file license headers.
