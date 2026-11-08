# The chat

A panel in the interface where you talk to a model that uses Mnemarr the way you do: it searches your notes and answers from them, adds a line to a note, declares a source. It is **optional**: it needs a small second container, the **assistant runner** ([ADR 0035](adr/0035-the-chat-is-a-panel-a-proxy-and-a-runner-with-adapters-for-any-model.md)). Without it Mnemarr works as before and shows no chat.

```text
browser ── Mnemarr (/api/v1/chat, the proxy) ── shared secret ──▶ mnemarr-agent (the runner, not published)
                                                                    │ the model: Claude Code, or an OpenAI-compatible API
                                                                    ▼
                                         Mnemarr /mcp  (a token of the person, 30 minutes, MCP only)
```

## What it can do

- **Ask.** *What did we decide about backups?* The model searches by words and by meaning, reads the notes, and answers with where it found it.
- **Write, with your approval.** *Add this to my courses.* It reads the note, proposes the change as a card (**Apply this change?**, with the tool and its arguments); nothing changes until you press **Approve**. A source that is a Git checkout open for writing commits and pushes it, so you can pull it elsewhere.
- **Look after Mnemarr.** Declare, change or forget a source, read it again, bring a Git source up to date, ask for a machine to join. Same cards.
- It does **not** change Mnemarr's settings (they are the operator's environment and configuration as code), and it has no shell, no file and no network access.
- The writing tools are off unless the operator turned them on (`MNEMARR__MCP__WRITETOOLS=true` for notes, `MNEMARR__MCP__SOURCETOOLS=true` as well for the tools that look after sources): the chat then only reads, and says so.
- It acts **as you**: what it may do is what your role allows, and the audit log says `you (via chat:<id>)`. The roles Admin, Operator and Reader may use it (`chat.use`); an assistant's key may not.

## Run the runner

```yaml
services:
  mnemarr:
    environment:
      MNEMARR__AGENT__URL: http://mnemarr-agent:8489
      MNEMARR__AGENT__SECRET: ${AGENT_SECRET}            # 32 characters or more: openssl rand -hex 32
      MNEMARR__MCP__WRITETOOLS: 'true'                   # so that it can change notes, with your approval
      MNEMARR__MCP__SOURCETOOLS: 'true'                  # and declare, change and forget sources
  mnemarr-agent:
    image: ghcr.io/<owner>/mnemarr-agent:<tag>
    restart: unless-stopped
    environment:
      MNEMARR_AGENT_SECRET: ${AGENT_SECRET}
      MNEMARR_MCP_URL: http://mnemarr:8383/mcp
      CLAUDE_CODE_OAUTH_TOKEN: ${CLAUDE_CODE_OAUTH_TOKEN}  # `claude setup-token`: your Claude subscription, not an API key
    volumes:
      - agent-data:/data
volumes:
  agent-data:
```

No port of the runner is published: only Mnemarr talks to it. A ready file is in [`deploy/companions/mnemarr-agent`](../deploy/companions/mnemarr-agent/docker-compose.yml).

### Another model: any OpenAI-compatible API

OpenAI, Azure OpenAI, Ollama, vLLM, a gateway: anything that answers `/chat/completions` with tool calls. Give the address, a key if it needs one, and the model names it accepts:

| Variable (runner) | Meaning |
|---|---|
| `MNEMARR_AGENT_OPENAI_URL` | The base address, for example `https://api.openai.com/v1` or `http://ollama:11434/v1`. Setting it adds a second agent beside Claude Code. |
| `MNEMARR_AGENT_OPENAI_MODELS` | The model names, `id` or `id:Label`, comma separated (required). |
| `MNEMARR_AGENT_OPENAI_KEY` | The key, when the API wants one (a local server does not). Never sent anywhere but that address. |
| `MNEMARR_AGENT_OPENAI_NAME` | What the agent is called in the panel (default *OpenAI-compatible*). |
| `MNEMARR_AGENT_OPENAI_DEFAULT_MODEL` | The first model by default. |

> This adapter was tried against stand-ins that speak the API, not against a real provider: try it with your own and tell what breaks.

### Other settings of the runner

| Variable | Default | Meaning |
|---|---|---|
| `MNEMARR_AGENT_MODELS` / `_DEFAULT_MODEL` | `sonnet,opus,haiku` | The Claude models offered, `id` or `id:Label`. |
| `MNEMARR_AGENT_MAX_SESSIONS` | `3` | Models running at once (each costs one process, about 100 to 200 MB). |
| `MNEMARR_AGENT_MAX_CONVERSATIONS` | `200` | Conversations kept on disk; the oldest idle one makes room. |
| `MNEMARR_AGENT_IDLE_MINUTES` | `15` | A model that is not used stops after this; the conversation resumes at the next message. |

## What to know

- **Long notes are read in windows.** A conversation can hold hundreds of thousands of characters. `read_note` gives at most 20 000 characters at a time and says the total length and where the next window starts; the model can ask for the end of the note (`tail`, the latest of a conversation), another window (`offset`) or the passage around some words (`around`). It is told to do so for the conversations, to learn where a project stands.

- **What you say goes to the model's provider** (Anthropic, OpenAI, …), the conversation's notes included. Choose it knowing that; a local model keeps everything on your host.
- **A subscription has limits** shared with your own use of Claude; a chat turn costs what a Claude Code turn costs.
- **The runner and Mnemarr move together**: Mnemarr passes the runner's answers through to the panel, so update both images at once. Each person keeps 50 conversations at most (`MNEMARR__AGENT__MAXCONVERSATIONSPERPERSON`): delete one to start another. The runner deletes nothing to make room: at its own cap (`MNEMARR_AGENT_MAX_CONVERSATIONS`, 200) it refuses new conversations, so keep the number of people times the cap per person under it.
- **No API key by default.** `CLAUDE_CODE_OAUTH_TOKEN` is the login of a Claude subscription. If you set `ANTHROPIC_API_KEY` on the runner instead, the CLI uses it and the usage is billed by the API; it is there for those who want it, and is not needed.
- The conversations (what was said, the tool calls) are in the runner's volume; Mnemarr keeps whose they are. They are yours: another person does not see them.
- **No chat in the interface?** The runner is off or not configured (no `MNEMARR__AGENT__URL`), or your role has no `chat.use`. **"The runner does not answer"**: check its address and that its secret equals Mnemarr's. **"has no credentials"**: the runner has no `CLAUDE_CODE_OAUTH_TOKEN` (or no OpenAI address).
