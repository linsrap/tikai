# Tikai agent base: what every agent container has (git, Node, a non-root user). Build:
#   docker build -t tikai/agent-base:local images/agent-base
# The orchestrator runs it read-only, cap-dropped, as uid 1000, with only the run's directory mounted at /run.
FROM node:24.22.1-trixie-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*
# The node image's user "node" (uid 1000) is the agent user; the run's work/ directory belongs to it.
USER node
