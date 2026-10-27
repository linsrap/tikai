# Tikai hub: API + MCP + orchestrator, serving the web app. Build from the repo root:
#   docker build -f images/hub/Dockerfile -t tikai/hub .

# 1. Web app
FROM node:24-trixie-slim AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# 2. API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Tikai.slnx .editorconfig ./
COPY src/ src/
# The OpenAPI document is generated in development and checked in CI; not needed here.
RUN dotnet publish src/Tikai.Api/Tikai.Api.csproj -c Release -o /app -p:OpenApiGenerateDocuments=false

# 3. Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# openssh-client: SSH remotes (the git package only recommends it). curl: the health check.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git openssh-client curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /web/dist ./wwwroot
# Named volumes copy this ownership on first use, so the non-root app user can write to it.
RUN mkdir -p /data && chown "$APP_UID" /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Tikai__DataPath=/data
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=30s --retries=3 \
  CMD curl -fsS http://localhost:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "Tikai.Api.dll"]
