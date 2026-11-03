# Security Policy

Mnemarr stores sensitive material: full conversations with AI assistants and notes about your work. We take security reports seriously.

## Reporting a vulnerability

**Do not open a public issue.** Use GitHub's [private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability) on this repository ("Security" tab, then "Report a vulnerability").

Please include the affected version, reproduction steps and the impact you observed. We aim to acknowledge reports within 7 days.

## Supported versions

Until 1.0, only the latest release receives security fixes.

## Security model (summary)

- Nothing leaves the host unless the operator configures it; any external model or embeddings API is opt-in.
- Secrets are never stored in memories nor returned by the API: they are masked before they leave the machine that holds the conversation, and checked again on the server.
- Every API and MCP call is authenticated by Mnemarr itself. A collector agent holds an ingest-only credential, one per machine, revocable: a compromised agent can add conversations, it cannot read any memory.
- The image runs as a non-root user, with no shell.

The threat model is in the [specification](docs/SPEC.md), §6.
