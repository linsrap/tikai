# Tikai agent base: what every agent container has (git, Node, Python, a non-root user). Build:
#   docker build -t tikai/agent-base:local images/agent-base
# The orchestrator runs it read-only, cap-dropped, as uid 1000, with only the run's directory mounted at /run.
FROM node:24.22.1-trixie-slim
# python3 is Debian's package: the standard library for the agent's small scripts, without pip and venv.
# python-is-python3 adds the `python` command (a symlink). procps gives ps, pgrep and pkill: without them agents stopped
# their dev servers with loops over /proc/*/cmdline, which often matched and killed their own shell.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates curl python3 python-is-python3 procps \
 && rm -rf /var/lib/apt/lists/*
# No __pycache__ directories next to the modules a script imports: they would land in the run's clone.
ENV PYTHONDONTWRITEBYTECODE=1
# The node image's user "node" (uid 1000) is the agent user; the run's work/ directory belongs to it.
USER node
