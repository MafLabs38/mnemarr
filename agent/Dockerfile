# mnemarr-agent (ADR 0035): the optional companion that runs an agent CLI headless behind Mnemarr's chat panel.
# Not part of Mnemarr's image. Its only tool is Mnemarr's MCP server: no shell tool, no access to the machines.
#
#   docker build -t mnemarr-agent agent/
#
# Node 22 runs the TypeScript sources directly (type stripping): no build step, no runtime dependency.
# The base image is pinned by its digest (node 22.18, bookworm-slim); a change of it is a pull request like any other.
FROM node:22.18-bookworm-slim@sha256:752ea8a2f758c34002a0461bd9f1cee4f9a3c36d48494586f60ffce1fc708e0e

# The agent CLIs, installed from the lockfile in cli/ (the version and the integrity of the CLI and of everything it
# needs are fixed there; adapters for other CLIs add theirs). No install script of any package runs: the CLI is a bundle
# of JavaScript and needs none.
WORKDIR /opt/cli
COPY cli/package.json cli/package-lock.json ./
RUN npm ci --ignore-scripts --omit=dev \
 && npm cache clean --force
ENV PATH="/opt/cli/node_modules/.bin:${PATH}"

WORKDIR /app
COPY package.json ./
COPY src ./src

# Conversations, the CLI's own state (CLAUDE_CONFIG_DIR) and its empty working directory.
RUN mkdir -p /data && chown node:node /data
VOLUME /data
USER node

ENV MNEMARR_AGENT_DATA=/data \
    MNEMARR_AGENT_PORT=8489 \
    NODE_ENV=production
EXPOSE 8489

HEALTHCHECK --interval=30s --timeout=3s CMD node -e "fetch('http://127.0.0.1:8489/healthz').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"

CMD ["node", "src/main.ts"]
