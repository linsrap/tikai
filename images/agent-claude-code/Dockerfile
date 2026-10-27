# Claude Code for Tikai agent runs. Build after agent-base:
#   docker build -t tikai/agent-claude-code:local images/agent-claude-code
# The version is pinned; changing it is a commit. Each run records the version it used (its system event).
ARG BASE_IMAGE=tikai/agent-base:local
FROM ${BASE_IMAGE}
ARG CLAUDE_CODE_VERSION=2.1.310
USER root
RUN npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION} && npm cache clean --force
USER node
LABEL tikai.claude-code-version=${CLAUDE_CODE_VERSION}
# No self-updates (the image is read-only anyway), no telemetry or error reports, nothing but the API and the MCP server.
ENV DISABLE_AUTOUPDATER=1 \
    DISABLE_TELEMETRY=1 \
    DISABLE_ERROR_REPORTING=1 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1
ENTRYPOINT ["claude"]
