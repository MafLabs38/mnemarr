# Connect Claude to Mnemarr

This guide takes a computer where you use [Claude Code](https://claude.com/claude-code) (in a terminal or in VS Code) and makes it use your Mnemarr. It assumes a Mnemarr is already running and that you can sign in to it. If not, start with [deploy/](../../deploy/README.md).

At the end, on that computer:

1. **Your conversations reach Mnemarr** (the collector), so that the memory has something to remember.
2. **Claude can search and read the memory** during a session, and write to it if your Mnemarr allows it (the MCP server).
3. **A new chat knows where the last one stopped**, without you asking (the hook).

Each part works alone, and you can stop after any of them. They are three steps because they are three different programs and three different keys; none of them is difficult once you know which is which.

## The three keys

| Part | What it is | Whose key | Who makes it | Where it lives |
|---|---|---|---|---|
| Collector | A small program that sends this computer's conversations | The **ingest key** of the machine: it can write conversations for this machine and nothing else, and it reads nothing | Mnemarr, when the machine joins | `key-<name>` in the folder of the collector |
| MCP | Claude Code talks to `/mcp` | An **MCP key** of a service account | You, in Mnemarr | Claude Code's own configuration |
| Hook | Claude Code runs the collector at the start of a session, and it asks Mnemarr what to tell the assistant | A **read key** of a service account | You, in Mnemarr | a file named `read-key` in the folder of the collector |

Make a **separate key for the MCP and for the hook, and one set per computer**: if a computer is lost or retired, or one file leaks, you revoke those keys and nothing else stops working.

## Before you start

- **An address** for your Mnemarr that this computer can reach, written `https://mnemarr.homelab.example` in this guide. Use HTTPS: the keys travel on it. (On a home network without TLS, see [Try it on a home network without TLS](../network.md#try-it-on-a-home-network-without-tls).)
- **A proxy with a single sign-on** in front of Mnemarr must let `/mcp` and `/api/v1/ingest/*` through without it: they carry a key, Mnemarr checks it itself, and a program cannot sign in with a browser. See [What to publish](../network.md#what-to-publish-and-what-not-to). The hook of step 3 makes one more call, `GET /api/v1/context`; whether it is published like the other two is an open question (Q-42), set out in [the paragraph on the hook](../network.md#what-to-publish-and-what-not-to). If the hook cannot reach Mnemarr, it prints nothing and the session starts without it.
- **Claude Code**, installed on this computer. The extension for VS Code uses the same configuration as the command `claude`.
- **Whether Claude may write.** Out of the box the MCP tools only read. The operator of your Mnemarr turns on the tools that write notes with `MNEMARR__MCP__WRITETOOLS=true` ([configuration](../configuration.md#mcp-mcp)). If you cannot tell, do the steps and see which tools Claude is offered.

## Step 1. Join this computer to Mnemarr

In Mnemarr, open **Machines** and add this computer.

- **Windows.** Download the installer and open it (Windows warns once that the publisher is unknown: *More info*, then *Run anyway*). A page opens in your browser to give the address of your Mnemarr; check that the code on it matches the one on the computer, name the machine and approve it. Nothing to copy by hand.
- **Linux and macOS.** The page shows one line with a one-time code, valid 15 minutes. Paste it in a terminal, as your usual user (no administrator rights):

  ```bash
  curl -fsSL 'https://mnemarr.homelab.example/api/v1/ingest/install.sh?code=ABCD-EFGH-JKLM&machine=my-laptop' | sh
  ```

The machine shows as *waiting* on the page, then *online* after its first pass. Details, commands and removal: [the collector](../collector.md#join-a-machine-built-adr-0027).

The program is now installed and runs in the background. Its folder holds your keys and `destinations.json`:

| System | Folder of the collector |
|---|---|
| Linux, macOS | `~/.config/mnemarr-collector/` (under `$XDG_CONFIG_HOME` instead, if you set it: then use that folder wherever this guide says `~/.config`) |
| Windows | `%AppData%\mnemarr-collector\` |

The program itself is where the way you joined put it, and the commands below call it `mnemarr-collector`. If your terminal does not know that name, give its full path instead:

| How you joined | Is `mnemarr-collector` a command? | Full path of the program |
|---|---|---|
| Windows installer | yes, in a new terminal | `%LocalAppData%\MnemarrCollector\` (the installer's folder) |
| Windows line (PowerShell) | no | `%LocalAppData%\mnemarr-collector\mnemarr-collector.exe` |
| Linux, macOS line | no | `~/.local/share/mnemarr-collector/mnemarr-collector` (or under `$XDG_DATA_HOME`) |

## Step 2. Let Claude use the memory (MCP)

1. In Mnemarr, open **Accounts** and **Create an account**: a name such as `claude-my-laptop`, the kind **Service**, and the role **Reader** (to read and search) or **Assistant** (to read, search and, when the operator allows it, write).
2. Open the keys of that account and **Create a key**. Copy it when it is shown: **it is shown once** and cannot be read again. If you lose it, make another and revoke the old one.
3. On the computer, in **a terminal of your own, not in a Claude Code chat** (see [Keep keys out of the chat](#keep-keys-out-of-the-chat)), add Mnemarr to Claude Code:

   ```bash
   # Linux, macOS: paste the key when asked; nothing is shown
   read -rs MNEMARR_KEY
   claude mcp add --scope user --transport http mnemarr https://mnemarr.homelab.example/mcp --header "X-Api-Key: $MNEMARR_KEY"
   unset MNEMARR_KEY
   ```

   The key is on the command line of `claude mcp add` for a moment, where another user of the same computer could see it with `ps`. It does not matter on a computer that only you use.

   ```powershell
   # Windows PowerShell
   $secret = Read-Host "Key" -AsSecureString
   $key = [System.Net.NetworkCredential]::new('', $secret).Password
   claude mcp add --scope user --transport http mnemarr https://mnemarr.homelab.example/mcp --header "X-Api-Key: $key"
   Remove-Variable key, secret
   ```

   - The address ends with **`/mcp`**.
   - **`--scope user`** makes it available in every project. Without it, the default scope is `local` and Mnemarr is only there for the folder you ran the command in.
   - Never use `--scope project` with a key: it writes the key to a `.mcp.json` file that is meant to be committed.
   - The command works the same in PowerShell, cmd and a Unix shell.

4. Check it:

   ```bash
   claude mcp list
   ```

   `mnemarr` should say **Connected**. In a Claude Code session, `/mcp` shows the same and the tools it offers. Then ask Claude something your memory knows, for example *Search Mnemarr: what did we decide about backups?*

What Claude is told when it connects: start from `list_sources`, search the **Memory** source first (consolidated notes, short, each linked to the conversations it came from), and go down to the **Conversations** source only to check a detail. The tools are `list_sources`, `search`, `list_notes`, `read_note` and `list_tags`; with the writing tools on, also `create_note`, `update_note`, `append_to_note`, `rename_note` and `delete_note`; and `request_collector_enrolment`, which asks for a new machine to join (a person approves it on the Machines page). What a key may do is what the role of its account allows: [service accounts and keys](../configuration.md#service-accounts-and-keys).

## Step 3. A new chat resumes where the last one stopped (the hook)

Without this step Claude knows the memory when it looks. With it, every new chat **starts** with the **thread** of the project (where the last sessions stood, the last in detail and the earlier ones condensed) and the end of the latest conversation of that project. Say *let's resume* and it can answer without searching. The design is in [ADR 0038](../adr/0038-a-new-session-resumes-the-thread-of-the-last-one.md).

1. Make a key as in step 2: a service account with the role **Assistant** (**Reader** gives the thread but not the end of the latest conversation, which needs the right to read conversations). Make it a **separate key** from the one of the MCP: with the writing tools on, an **Assistant** key can also write notes, and `read-key` is one more file on the computer that holds it, so you want to be able to revoke it alone.
2. In a terminal of your own, put the key, alone, in a file named `read-key` in the folder of the collector. The file is created readable by you only, and the key never appears on a command line:

   ```bash
   # Linux, macOS: paste the key when asked; nothing is shown
   (umask 077; read -rs KEY; printf '%s' "$KEY" > ~/.config/mnemarr-collector/read-key)
   chmod 600 ~/.config/mnemarr-collector/read-key   # in case the file already existed
   ```

   ```powershell
   # Windows PowerShell
   $secret = Read-Host "Key" -AsSecureString
   Set-Content -Path "$env:APPDATA\mnemarr-collector\read-key" -Value ([System.Net.NetworkCredential]::new('', $secret).Password) -NoNewline
   Remove-Variable secret
   ```

   In Windows PowerShell (the old one, version 5.1) do not write the file with `>`: it saves it in UTF-16 and the key is not read.

   If this computer sends to **several** Mnemarr, `mnemarr-collector destinations` lists them by name; name the file `read-key.<name>` for each.

3. Add the hook to Claude Code:

   ```bash
   mnemarr-collector context-hook
   ```

   It adds a `SessionStart` hook to `settings.json` in Claude Code's folder (`~/.claude/`, or `CLAUDE_CONFIG_DIR` if you set it), keeps a copy of the file as it was (`settings.json.mnemarr-backup`) and touches nothing else. Run it again whenever you like: it changes nothing if all is in place. `mnemarr-collector context-hook --remove` takes it away.

4. Check it, from a folder of a project you already worked on (the project is the **last folder** of the path):

   ```bash
   cd ~/code/my-project
   mnemarr-collector context
   ```

   It prints what a new session of that project would be told. Open a new chat in that folder and say *let's resume*.

**What to expect.**
- The end of the latest conversation is there at once. The **thread** is written by Mnemarr's consolidation a few minutes after a conversation goes quiet, so a project with no thread yet is normal on the first day.
- The hook never gets in your way: no key, no answer within 5 seconds, or nothing known about the project, and the session starts as it would have.
- A new chat is never told its own beginning: the hook passes it the session that is starting and Mnemarr leaves it out.
- What the hook gives is **data from memory, not instructions**: Claude is told so, and it is written by a model from past conversations, so it can be wrong. You can correct the thread: it is a note in the memory vault (`<project>/_thread.md`).

## The same on another computer

On each computer: step 1 (join), then a new account and key for steps 2 and 3. That is all; nothing is shared between computers except the Mnemarr itself, which is the point: what you did on one is known on the others once its conversations have been sent.

## Keep keys out of the chat

Everything you type or paste in a Claude Code chat, and every command it runs, is saved in the conversation file. The collector sends that file to Mnemarr, where it is stored, searched and consolidated like any other conversation. Mnemarr masks many kinds of secret on the way in, but a key that was typed in a chat is a key that has been copied into the memory, and the memory is meant to be read by assistants.

So: **create keys in the interface, and enter them yourself in a terminal that is not a Claude Code chat** (the terminal of VS Code is fine; the chat panel is not). Do not ask Claude to run a command that contains a key, and do not paste a key into a message. If a key did end up in a chat, revoke it on the Accounts page and make another.

## Let Claude do it with you

Claude can do the parts that hold no secret. You can paste this to Claude Code on the computer you want to connect:

> Connect this computer to my Mnemarr at `https://mnemarr.homelab.example`. Read the guide `docs/user/connect-claude.md` in the Mnemarr repository (https://github.com/MafLabs38/mnemarr) and go through the three steps with me. You may run the commands that hold no secret (installing the collector with the one-time code I give you, `claude mcp list`, `mnemarr-collector context-hook`, the checks). For anything that needs a key, give me the command and wait: I create the key in Mnemarr and enter it myself in a terminal of my own, and I will tell you when it is done. Do not ask me for a key, do not run a command that contains one, and never put a key in a file that is committed.

The one-time code of step 1 is safe to give: it works once, for 15 minutes, for one machine name. Approving a machine and creating a key are done in Mnemarr's interface, signed in as you, and no assistant does them for you.

## When it does not work

| What you see | What to check |
|---|---|
| `claude mcp list` says the server failed | The address ends with `/mcp` and is reachable from this computer; the header is `X-Api-Key: <key>`; the key was not revoked or expired (Accounts, the keys of the account). Behind a proxy with a single sign-on, `/mcp` must go through it. |
| Claude does not have the Mnemarr tools in a project | The server was added with the default `local` scope, which is the folder you ran the command in: run `claude mcp remove mnemarr` there and add it again with `--scope user`. |
| Claude can search but cannot write | The tools that write are off by default (`MNEMARR__MCP__WRITETOOLS`), and the account needs the role **Assistant**. |
| A new chat is told nothing | Run `MNEMARR_DEBUG=1 mnemarr-collector context` (PowerShell: `$env:MNEMARR_DEBUG=1; mnemarr-collector context`): it says why it prints nothing. Usual causes: no `read-key` file or a key that was revoked; the folder is not the one the conversations were collected under; the project has no conversation in Mnemarr yet (check the Machines page); the host Mnemarr is reached on does not let `/api/v1/context` through. |
| Only the end of the last conversation, no thread | The consolidation has not run for this project yet, or is off. Its state is on the Sources page of Mnemarr, and the thread appears a few minutes after a conversation goes quiet. |
| The thread is there but not the end of the last conversation | The key's role is **Reader**. Make a key for an account with the role **Assistant**. |
| The hook stopped after the collector was updated | Run `mnemarr-collector context-hook` again: it points the hook at the program that is installed now. |
| Two computers, and one sees nothing from the other | The conversations of the other have not arrived: look at its row on the Machines page (*online*, the count of conversations and what its last pass found). |

## Security

- A key is a password. The MCP key is stored in clear text in Claude Code's own configuration (`~/.claude.json`) and the read key in `read-key`: keep both readable by your user only, and keep neither in a repository. [Keep them out of the chat](#keep-keys-out-of-the-chat) too.
- A key can do what the role of its account allows, no more. When you create it you can make it expire. Limiting it to some sources is done through the API ([service accounts and keys](../configuration.md#service-accounts-and-keys)).
- Memory can hold sensitive text. The thread and the end of the last conversation go into the context of the model that Claude uses, like anything you paste into a chat; Mnemarr masks the secrets it recognises, and nothing leaves your Mnemarr except to the assistant you connected.
- To retire a computer: revoke the machine on the Machines page and the keys of its accounts on the Accounts page.
