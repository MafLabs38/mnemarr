# mnemarr-agent (ADR 0035): the optional companion that runs an agent CLI headless behind Mnemarr's chat panel.
# Not part of Mnemarr's image. Its only tool is Mnemarr's MCP server: no shell tool, no access to the machines.
#
#   docker build -t mnemarr-agent agent/
#
# Node 22 runs the TypeScript sources directly (type stripping): no build step, no runtime dependency.
# The base image is pinned by tag and digest: the digest makes the build reproducible, the tag is what Dependabot follows (with the digest
# alone it moved to `node:latest`, the full image). An exception to SonarQube's docker:S8431 stated in ci.yml and ADR 0013: do not "fix" it.
# tests/e2e/stack/docker-compose.yml runs the fake agent and the fake transcriber on the same image (Dependabot moves both).
FROM node:22-bookworm-slim@sha256:be979e5523c76fa4d5ca4ebd3099078f220aa47ec39c48ad5b5df9314debd6eb

# The agent CLIs, installed from the lockfile in cli/ (the version and the integrity of the CLI, of its native binary and of
# everything else are fixed there; adapters for other CLIs add theirs). No install script of any package runs. The one
# step the CLI needs from its own installer (copying the native binary of this platform over the placeholder of
# bin/claude.exe) is run by hand: it only picks and copies a file, it downloads nothing.
WORKDIR /opt/cli
COPY cli/package.json cli/package-lock.json ./
RUN npm ci --ignore-scripts \
 && node node_modules/@anthropic-ai/claude-code/install.cjs \
 && rm -rf node_modules/@anthropic-ai/claude-code-*-musl \
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
