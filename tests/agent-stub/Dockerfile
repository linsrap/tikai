# A stand-in for Claude Code in tests: same command line, MCP over HTTP, stream-json output, no API key needed.
#   docker build --build-arg BASE_IMAGE=tikai/agent-base:test -t tikai/agent-stub:test tests/agent-stub
ARG BASE_IMAGE=tikai/agent-base:local
FROM ${BASE_IMAGE}
USER root
COPY claude.mjs /opt/tikai-stub/claude.mjs
RUN printf '#!/bin/sh\nexec node /opt/tikai-stub/claude.mjs "$@"\n' > /usr/local/bin/claude && chmod 755 /usr/local/bin/claude
USER node
ENTRYPOINT ["claude"]
