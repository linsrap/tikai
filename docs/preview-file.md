# tikai-preview.yml

**Preview** on the review screen starts a round in containers, using `tikai-preview.yml` at the repository root. Agents can write and maintain the file. The project's Tickets page uses the same file to preview the default branch as it is now (for example once every ticket is done); that preview stops when the branch moves. The hub checks the file strictly: when something is wrong, the review screen lists the errors instead of starting a preview. Tikai's own [`tikai-preview.yml`](../tikai-preview.yml) is a working example.

A preview is for apps served over HTTP: a person opens it in the browser. A project with nothing to serve (a desktop app such as WPF, a mobile app, a command line tool, a library) gets no `tikai-preview.yml`; the reviewer uses **Try locally** on the review screen and builds the round on their own machine.

## How a preview starts

1. The round is checked out to `/preview/src`. This is the working directory of every setup step, app and seed step.
2. The `setup` steps run in order, each in its own container, with internet for package registries.
3. The `services` start (stock images such as a database). The hub waits for their health checks.
4. All apps start, and the hub waits until every app answers on its port.
5. The `seed` steps run in order, each in its own container, and fill the preview with data (see [Seed data](#seed-data)).
6. The preview is running. When any app stops, the preview fails.

Setup steps, apps and seed steps run as uid 1000 with a read-only root filesystem, so `apt-get install` or `npm i -g` fail. They can write to `/preview` (setup output stays there for the apps, for example in `/preview/out`), to `HOME` (`/preview/home`) and to `/tmp`. **Apps and seed steps have no internet**: install everything in `setup`. Each container gets 4 GB of memory, 2 CPUs and 1024 processes.

Time limits:
- 10 minutes to pull each image.
- 20 minutes for each setup step.
- 3 minutes for the services to be healthy and the apps to answer. Database migrations at startup count toward this.
- 5 minutes for each seed step.

## The file

The file is one YAML document of at most 64 KB. Anchors and aliases are not allowed. Unknown keys are errors: there is no `build`, `volumes`, `ports`, `networks`, `depends_on` or `privileged`, because the hub decides those.

| Key | Required | What it holds |
|---|---|---|
| `setup` | no | A list of at most 10 steps. Each step has `image` and `run` (a shell command). |
| `app` | one of `app`, `apps` | A single process, always on `/`. |
| `apps` | one of `app`, `apps` | A map from a name to an app, at most 5 (see [Several processes](#several-processes)). |
| `services` | no | A map from a name to a service, at most 5. |
| `seed` | no | A list of at most 10 seed steps (see [Seed data](#seed-data)). |
| `seed_hint` | no | One line of at most 200 characters shown next to **Open**, for example the seeded login. |
| `idle_minutes` | no | 5 to 240, default 30. Previews also stop after 4 hours. |

An app:

| Field | Required | Rules |
|---|---|---|
| `image` | yes | A stock image. Use the image of the setup step that built the app's files, so native modules match. |
| `command` | yes | Run with `/bin/sh -c`. It must listen on `0.0.0.0`, not `localhost`. Start the server with `exec`. |
| `port` | yes | 1 to 65535. |
| `environment` | no | A map from names to values. Quote numbers and booleans (`"8080"`, `"false"`). |
| `generate` | no | Variable names that get a random value per preview, for secrets the app needs. Not also in `environment`. |
| `healthcheck` | no | `{ path: /health }`. The hub waits for a 2xx or 3xx answer there. Without it, any HTTP answer on `/` counts. |
| `path` | only in `apps` | The path prefix the app serves (see below). |

A service:

| Field | Required | Rules |
|---|---|---|
| `image` | yes | A stock image. It runs as the image's own user, without the source. |
| `environment` | no | A map from names to values. |
| `command` | no | A list of arguments for the image's entrypoint, for example `["redis-server", "--save", ""]`. |
| `healthcheck` | no | `{ command: pg_isready -U app }`. Apps start once it succeeds. |

Apps and services reach a service by its name (`Host=db`, `redis://cache:6379`). A name is lowercase letters, digits and `-`, starts with a letter, and is not `app` or `hub`. Every preview starts with empty data; `seed` fills it. Use throwaway passwords for services (`POSTGRES_PASSWORD: preview`) and never put real credentials in the file.

## Several processes

`apps` instead of `app` runs several processes behind the preview's one address:

- **Path:** each app has a `path` prefix, default `/`. Exactly one app is on `/`, and prefixes are unique (`/api`, `/v1/admin`; no trailing slash).
- **Routing:** the longest matching prefix gets the request, with the path unchanged. `/api/users` reaches the `/api` app as `/api/users`, so that app serves its routes under `/api`. The browser code calls relative URLs (`/api/...`), not `http://localhost:8080`.
- **Names:** apps reach each other and the services by name (`http://api:8080`). An app's name follows the service rules and is not a service's name.
- **Shared values:** a variable listed in `generate` by several apps has the same value in all of them.
- **Host header:** each app gets requests with its own container name as the host (`tikai-preview-app-<id>-<name>`). Dev servers that check the host have to allow it; for Vite, set `server.allowedHosts: true` in the config the preview uses.

## Seed data

Seed steps put demo data into the preview, so the reviewer sees the change with data instead of empty pages. They run after every app answers, so the app's migrations have created the tables. A step has `image` and `run` like a setup step, and `environment` and `generate` like an app: a name in `generate` gets the same value as in the apps that list it. Steps reach services and apps by name and can read the round's files and the setup output, but have no internet. A step that fails or takes longer than 5 minutes fails the preview, and its output is in the preview log (`seed 1`, `seed 2`, …).

Seed data comes from the repository only: files next to the code (`seed/demo.sql`, a small script), kept current with the schema. Never put real or personal data there. Any of these works:

- **SQL into the database**, with the database's own image: `psql "postgresql://app:preview@db/app" -f seed/demo.sql`. Fast; passwords must be stored already hashed, and the SQL must follow schema changes.
- **Through the app's API**, like a user: a script that signs up and creates data at `http://app:8080` (`http://<name>:<port>` with `apps`). The app checks the data and hashes passwords itself.
- **The app's own seeder**, if it has one (`npm run seed`, `dotnet Api.dll --seed-demo`), with the app's image and the same `environment` and `generate` as the app.

When the data includes a login, say it in `seed_hint` (throwaway values only).

## Examples

Adapt the commands to the project.

A production build served by a small server (the fastest and most reliable start):

```yaml
setup:
  - image: node:24-trixie-slim
    run: npm ci --no-audit --no-fund && npm run build
app:
  image: node:24-trixie-slim
  command: exec npx --no-install vite preview --host 0.0.0.0 --port 8080
  port: 8080
```

An API with a database:

```yaml
setup:
  - image: mcr.microsoft.com/dotnet/sdk:10.0
    run: dotnet publish src/Api -c Release -o /preview/out
app:
  image: mcr.microsoft.com/dotnet/aspnet:10.0
  command: cd /preview/out && exec dotnet Api.dll
  port: 8080
  environment:
    ASPNETCORE_HTTP_PORTS: "8080"
    ConnectionStrings__Db: Host=db;Database=app;Username=app;Password=preview
  generate: [Jwt__Secret]
  healthcheck: { path: /health }
services:
  db:
    image: postgres:17
    environment: { POSTGRES_DB: app, POSTGRES_USER: app, POSTGRES_PASSWORD: preview }
    healthcheck: { command: pg_isready -U app -d app }
```

The same API with demo data: SQL from the repository, then a user created through the API:

```yaml
setup:
  - image: mcr.microsoft.com/dotnet/sdk:10.0
    run: dotnet publish src/Api -c Release -o /preview/out
app:
  image: mcr.microsoft.com/dotnet/aspnet:10.0
  command: cd /preview/out && exec dotnet Api.dll
  port: 8080
  environment:
    ASPNETCORE_HTTP_PORTS: "8080"
    ConnectionStrings__Db: Host=db;Database=app;Username=app;Password=preview
  healthcheck: { path: /health }
services:
  db:
    image: postgres:17
    environment: { POSTGRES_DB: app, POSTGRES_USER: app, POSTGRES_PASSWORD: preview }
    healthcheck: { command: pg_isready -U app -d app }
seed:
  - image: postgres:17
    run: psql "postgresql://app:preview@db/app" -v ON_ERROR_STOP=1 -f seed/demo.sql
  - image: node:24-trixie-slim
    run: node seed/demo-user.mjs http://app:8080
seed_hint: Log in as demo@example.com / preview
```

A frontend dev server and an API, both on one address:

```yaml
setup:
  - image: node:24-trixie-slim
    run: npm ci --prefix web --no-audit --no-fund
  - image: mcr.microsoft.com/dotnet/sdk:10.0
    run: dotnet publish src/Api -c Release -o /preview/api
apps:
  web:
    image: node:24-trixie-slim
    command: cd web && exec npx --no-install vite --host 0.0.0.0 --port 5173
    port: 5173
  api:
    path: /api
    image: mcr.microsoft.com/dotnet/aspnet:10.0
    command: cd /preview/api && exec dotnet Api.dll
    port: 8080
    environment: { ASPNETCORE_HTTP_PORTS: "8080" }
    healthcheck: { path: /api/health }
```

## Common mistakes

- An app downloads packages when it starts. It has no internet; move the install into `setup`.
- The server listens on `localhost`. The hub cannot reach it there; use `0.0.0.0`.
- Files are written next to the image's own files (for example `/app`). Only `/preview`, `HOME` and `/tmp` are writable.
- The frontend calls the API at an absolute `localhost` URL instead of a relative `/api/...`.
- The API under `/api` expects the prefix to be stripped. Tikai keeps the path as it is.
- A seed step downloads packages (`npx some-tool`, `pip install`). It has no internet; install in `setup` and run from `/preview`.
- Seed SQL that does not stop on the first error (`psql` without `-v ON_ERROR_STOP=1`): the step succeeds with half the data.

**For agents:** this page is what `get_preview_spec` returns. You cannot start a preview yourself, so call `check_preview_definition` with the file's text before you submit, and compare what it cannot see (commands, ports, what is installed where) with this page. Do not write the file for a project that serves nothing over HTTP: the check would pass, but the preview fails because no app answers. When your change needs data to be seen (a list, a chart, a page behind a login), add or extend the seed so the reviewer sees it with data. When a preview needs something the file cannot express (a private image, internet at runtime, more than 5 apps or services), ask with `ask_human` instead of working around it.
