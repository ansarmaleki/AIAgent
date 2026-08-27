# ALAgent

A minimal command-line AI agent written in C# (.NET 10), with **zero NuGet dependencies**.
It talks to any OpenAI-compatible `/chat/completions` endpoint, gives the model real tools
(files, shell, web search), and gates every risky action behind a human yes/no prompt.

## Features

- **Interactive REPL** and one-shot mode
- **Tool use**: `web_search`, `write_file`, `read_file`, `edit_file`, `list_files`,
  `delete_file`, `run_command`
- **Human-in-the-loop**: every write/edit/delete/run needs your approval (`y/n`) before
  it happens; denials are never retried in the same turn
- **Robust loop**: retries transient HTTP errors (429/5xx), caps tool steps per turn
  (default 15), truncates oversized tool output, and turns tool errors into text the
  model can recover from
- **Tested**: offline tool selftests plus an eval suite (mock tools + LLM-as-judge)

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An API key for any OpenAI-compatible provider

## Configuration

Environment variables, or a `.env` file next to the project (real environment variables
always win; `.env.{AGENT_ENV}` — default `.env.development` — overrides `.env`):

| Variable         | Required | Default                   | Purpose                                      |
|------------------|----------|---------------------------|----------------------------------------------|
| `AGENT_API_KEY`  | yes      | —                         | Bearer token for the API                     |
| `AGENT_BASE_URL` | no       | `https://agentrouter.org/v1` | Base URL of the OpenAI-compatible endpoint |
| `AGENT_MODEL`    | no       | `glm-5.3`                 | Model name sent to the API                   |
| `AGENT_UA`       | no       | `agent-cli/1.0.0`         | User-Agent header (some gateways need one)   |
| `AGENT_ENV`      | no       | `development`             | Which `.env.{name}` override file to load    |
| `AGENT_TRACE`    | no       | —                         | Directory to dump raw request bodies         |

`.env` format (plain `KEY=value`, no quotes, no colons):

```
AGENT_API_KEY=sk-...
AGENT_BASE_URL=https://api.deepseek.com
AGENT_MODEL=deepseek-chat
```

## Usage

```bash
dotnet run                            # interactive REPL (default)
dotnet run -- -p "question"           # one-shot question
dotnet run -- selftest                # offline tool tests, no API key needed
dotnet run -- eval [--out F] [--only NAME] [--debug]
                                      # agent eval suite (mock tools + LLM judge)
dotnet run -- script FILE [--out F]   # scripted input -> transcript file
```

Example session:

```
agent ready — glm-5.3 via https://agentrouter.org/v1
risky tools need your approval (y/n). type /exit to quit.

you › create notes.txt with the text "buy milk"
agent › I'd like to write notes.txt. Allow? (y/n) › y
agent › Done — notes.txt now contains "buy milk".
```

## Tools

| Tool          | Risky | Description                                                        |
|---------------|-------|--------------------------------------------------------------------|
| `web_search`  | no    | DuckDuckGo HTML search, titled results with snippets               |
| `read_file`   | no    | Read a UTF-8 text file (directory listing errors are reported)     |
| `list_files`  | no    | List a directory with sizes                                        |
| `write_file`  | yes   | Create or overwrite a file                                         |
| `edit_file`   | yes   | Replace an exact unique `old_string` with `new_string`             |
| `delete_file` | yes   | Delete a file (never a directory)                                  |
| `run_command` | yes   | Run a shell command (`cmd.exe` / `bash`), 30s default, 60s max     |

Tool output is truncated at 4000 chars so a runaway command can't blow up the context.

## Project layout

| File          | Contents                                                        |
|---------------|-----------------------------------------------------------------|
| `Program.cs`  | Entry point, command dispatch, REPL / one-shot / scripted modes |
| `Config.cs`   | `AgentConfig` + minimal dotenv loader                           |
| `Agent.cs`    | `ApiClient` (OpenAI-compatible client), `AgentSession` (tool-use loop), `HumanGate` |
| `Tools.cs`    | `Toolbox`: the real tool implementations + API tool specs        |
| `Eval.cs`     | `MockToolbox` + `EvalSuite` (behavioral tests, LLM-as-judge)    |
| `SelfTest.cs` | Offline tool tests (`dotnet run -- selftest`)                   |

## Safety notes

- The agent runs on your real machine: approved `run_command` executes real shell
  commands, approved file tools touch real files. Read the `y/n` prompt before
  approving.
- A turn stops after 15 tool steps even if the model wants to continue.
- Don't commit your `.env` — add it to `.gitignore` if it contains real keys.
