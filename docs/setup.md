# Setup

How to install Tekla MCP Server, connect an MCP client and configure it. Overview: [README](../README.md);
every tool: [tools.md](tools.md).

## Requirements

**Production (live Tekla model, release zip):**

- Windows x64
- Tekla Structures 2021–2026 installed, **running with a model open** (2024–2026 untested, see [Known limitations](../README.md#known-limitations))
- .NET Framework 4.8 runtime (already present wherever Tekla runs)

**Building the live backend from source** additionally needs:

- [.NET SDK 8+](https://dotnet.microsoft.com/download)
- [.NET Framework 4.8 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net48)

**Development / testing (mock backend, no Tekla):**

- [.NET SDK 8+](https://dotnet.microsoft.com/download)

## Install from a GitHub Release (Windows, recommended)

Releases ship **one zip per Tekla version** — pick the one matching *your* Tekla:

1. Open **[Releases](https://github.com/tempalg1n/tekla-mcp-server/releases)** and download the zip for your Tekla version: `TeklaMcp.Server-vX.Y.Z-tekla2021.zip` … `-tekla2026.zip` (e.g. running Tekla Structures 2023 → `…-tekla2023.zip`).
2. Extract the zip to a folder (keep all `.dll` files next to the `.exe`).
3. Open Tekla Structures with a model, and point your MCP client at `TeklaMcp.Server.exe` (see [MCP client configuration](#mcp-client-configuration)). Codex users: the zip's `codex\Install-CodexPlugin.ps1` builds a plugin card instead — see [Codex (plugin card)](#codex-plugin-card).
4. Ask the assistant to call `tekla_get_connection_info`: it shows the open model, the Tekla version the zip was built for, and where the Tekla assemblies were loaded from.

No further setup is needed — the offline API reference used by the scripting tools is generated from your Tekla on first use. To try the server without Tekla, the `TeklaMcp.Server-X.Y.Z-net8.0-mock.zip` runs the mock backend (needs the .NET 8 runtime; `dotnet TeklaMcp.Server.dll`).

> **Why per-version zips?** The Tekla Open API protocol is version-locked, so the server must be compiled for the Tekla it talks to. The zip does **not** bundle Tekla DLLs — it loads them from your installed Tekla at runtime, verifying the version matches. A mismatched zip fails with a clear message naming the right one. If auto-detection of the Tekla Open API folder fails, set `TEKLA_BIN_DIR` (see [Configuration](#configuration)). See [tekla-api-notes.md](tekla-api-notes.md#tekla-version-compatibility).
>
> **If Tekla is open but the server says "Not connected"** (Tekla 2021–2023): the error message lists the client channel and the `Tekla.Structures.Model-*` named pipes Tekla actually publishes. The server auto-matches the published channel (some setups publish `…-Console:<version>` instead of the default `…-:<version>`). To force a specific channel, set the `TEKLA_MCP_CHANNEL` environment variable to the exact pipe name.
>
> **Starting the MCP client before Tekla is fine** (Tekla 2021–2023): until Tekla publishes its channel, tools answer "not reachable … nothing was sent to Tekla" and work as soon as the model is open — no server restart. **Restarting Tekla is fine too** (2021–2023; accepted live on Tekla 2023 with three restarts, 2021 still to be run): the next tool call notices the dead connection and reconnects on the same channel (`tekla_get_connection_info` shows `lastReconnect`). Nothing is retried, so a write is never repeated. The call that reconnects while Tekla is still opening the model waits for it (up to ~70 s measured), so a client may time out on that one call — the next one works. Only a Tekla that comes back under another Windows session or instance name (e.g. a second instance) needs a server restart, and so does Tekla 2024+ for now.
>
> **The server exits with its MCP client** (Windows) — when the process that launched it is gone, or when a shutdown hangs for 10 s (a Tekla call cannot be cancelled). If your launcher exits while keeping the server's stdio open, set `TEKLA_MCP_EXIT_WITH_PARENT=0`.
>
> **Tekla 2024–2026 users: testing and issues are very welcome.** The 2024+ zips are built and compiled against the official Open API packages, but the maintainers have no 2024+ install to run them on. Those versions talk to the server over a different transport (Trimble.Remoting instead of named pipes), so the connection guard and the automatic reconnect are not active there yet, and everything connection-related is untested. If you work with 2024, 2025 or 2026, please [open an issue](https://github.com/tempalg1n/tekla-mcp-server/issues) with what worked and what did not — the `tekla_get_connection_info` output and the server's stderr log help most. Agents can draft the report for you with `tekla_report_gap`.

See [releasing.md](releasing.md) for how maintainers publish releases.

## MCP client configuration

Example for Claude Desktop, Claude Code, or Cursor (`mcpServers`):

**Mock backend** (development):

```json
{
  "mcpServers": {
    "tekla": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/tekla-mcp-server/src/TeklaMcp.Server"]
    }
  }
}
```

**Live Tekla** (Windows — the extracted release zip, or `src\TeklaMcp.Server\bin\Release\net48\` of a source build):

```json
{
  "mcpServers": {
    "tekla": {
      "command": "C:\\Tools\\TeklaMcp-tekla2023\\TeklaMcp.Server.exe",
      "env": {
        "TEKLA_MCP_FILE_ROOT": "D:\\tekla-exchange"
      }
    }
  }
}
```

The `env` block is optional; the variables are listed below. The server speaks MCP over stdio: the
client starts it, and it exits with the client.

## Codex (plugin card)

Codex starts the MCP servers listed in its `config.toml`, but only plugins get a card in its
Plugins directory. Every Windows release zip ships a `codex` folder whose script builds a
**Tekla MCP** card for your computer: one server `tekla<year>` per installed Tekla version, each
with its own switch ([#18](https://github.com/tempalg1n/tekla-mcp-server/issues/18)).

1. Unpack the zips for your Tekla versions next to each other, e.g.
   `C:\MCP\TeklaMcp.Server-v0.9.0-tekla2021` and `C:\MCP\TeklaMcp.Server-v0.9.0-tekla2026`.
2. Run the script from one of them:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File C:\MCP\TeklaMcp.Server-v0.9.0-tekla2026\codex\Install-CodexPlugin.ps1
   ```

3. Quit the Codex app completely (also from the system tray), start it again and open a new thread.

The script finds the installed Teklas in the registry and the unpacked builds (it reads which Tekla
each one was built for), writes a local marketplace to `%LOCALAPPDATA%\TeklaMcp\codex-marketplace`,
and installs it with `codex plugin marketplace add` and `codex plugin add` (the CLI on `PATH`, or
the Codex app's own). Its `.mcp.json` holds this computer's paths, the `TEKLA_BIN_DIR` of each
Tekla and an explicit `TEKLA_MCP_USE_MOCK`; nothing machine-specific ships in the zip. By default
the card gets every Tekla version that has both an install and a build. `-Years 2021,2026` picks
the versions, `-Server` / `-ServerRoot` point at builds elsewhere, `-FileRoot` sets
`TEKLA_MCP_FILE_ROOT`, `-UseMock` starts the mock backend, `-NoInstall` only prints the commands.
Run it again after unpacking a new release: the card is regenerated and reinstalled under a new
version suffix, because Codex caches plugins by version.

**Switches and precedence** (checked with the Codex CLI 0.153.4 in an isolated `CODEX_HOME`):

- A server's switch is stored in `~/.codex/config.toml` as
  `[plugins."tekla-mcp@tekla-mcp-local".mcp_servers.tekla2026]` `enabled = false`.
- A direct `[mcp_servers.tekla2026]` table with the same name **replaces** the card's server, even
  with `enabled = false` (that simply switches the server off), and of two plugins declaring one
  name only one is started. So do not keep old direct entries "disabled" next to the card: the
  script lists such definitions after installing, and `-RemoveConflicts` removes them with
  `codex mcp remove` / `codex plugin remove`.
- `codex mcp list --json` shows what Codex will actually start; the script prints it for its servers.

**Troubleshooting.** No card: the marketplace was not added, or the app was not restarted. A card
without tools: the server is switched off or replaced by a same-name definition (above). Tools that
answer "not connected": that is the Tekla side, see `tekla_get_connection_info`. Checked by the
maintainers with the CLI 0.153.4 of the Codex app 26.924: generation, installation, the resolved
servers, and a card server started from its `.mcp.json` against a live Tekla 2021. The issue
reporter used the same layout with the Codex app 26.928 (CLI 0.159.2). How the switches look inside
the Codex app has not been verified by the maintainers.

## Configuration

All settings are optional environment variables, set in the client's `env` block or the system
environment.

| Variable | Default | Meaning |
|---|---|---|
| `TEKLA_BIN_DIR` | auto: the running Tekla process, then the registry | Folder holding the Tekla Open API (`Tekla.Structures.Model.dll` — `nt\bin\plugins` on 2021, `bin` on 2023+); its parent or the install root work too. A folder without the Model DLL is ignored with a warning. Its `Net48Runtime` subfolder, where Tekla 2026 keeps `Tekla.Structures.dll`, is searched automatically — do not point the variable there. |
| `TEKLA_MCP_CHANNEL` | auto-matched to the pipes Tekla publishes (2021–2023); Tekla's own names on 2024+ | Exact Model remoting channel (pipe) name to force when auto-matching picks the wrong one; the base and Drawing channels follow. |
| `TEKLA_MCP_FILE_ROOT` | `%LOCALAPPDATA%\TeklaMcp\exchange` + the open model's folder | `;`-separated roots the file-exchange tools may read and write. Setting it replaces the defaults. |
| `TEKLA_MCP_EXIT_WITH_PARENT` | on (Windows) | `0` stops the server from exiting when the process that launched it exits. |
| `TEKLA_MCP_API_REF_DIR` | generated on first use, cached in `%LOCALAPPDATA%\TeklaMcp\api-reference` | Folder with a pre-generated Open API reference (`*.md`); wins over the cache. |
| `TEKLA_MCP_SCRIPT_REF_DIR` | unset | Mock backend only: folder with `Tekla.Structures*.dll` so `tekla_check_csharp` can compile and the API reference can be generated without Tekla. |
| `TEKLA_MCP_USE_MOCK` | unset | `1` forces the mock backend in the Windows (`net48`) build. |
| `TEKLA_MCP_ISSUES_URL` | this repository's new-issue page | Where `tekla_report_gap` drafts point, e.g. an internal tracker. |
| `TEKLA_MCP_READ_ONLY` | off | `1` (or `true`/`yes`/`on`) serves only read tools and makes every write path refuse — see below. Any value other than `0`/`false`/`no`/`off` also turns it on. |
| `TEKLA_MCP_TOOLSETS` | all | Comma-separated toolsets to serve, e.g. `model,analytics` — see below. An unknown name stops the server. |

### Read-only mode and toolsets

**`TEKLA_MCP_READ_ONLY=1`** is for an agent that may look but not touch. The server then lists
only the 50 tools annotated `readOnlyHint=true` — no model or drawing writes, no
`tekla_run_csharp` (whether a script mutates is decided by a syntax check, which read-only mode
does not rely on), no selection or model-view capture, no file exports, no `tekla_report_gap`. It is
also enforced at runtime, not only by hiding tools: every write wrapper, the script tool and the
tools that write outside the wrappers refuse with a message naming the switch, so a tool annotated
read-only by mistake still cannot write. The server instructions tell the agent that it runs
read-only, and `tekla_get_connection_info` reports `serverReadOnly`.

**`TEKLA_MCP_TOOLSETS`** trims `tools/list` for clients that load every tool description into the
context (the full list is about 195k characters; read-only mode alone cuts it to about 61k).
`core` is always served. It hides tools; it is not a safety feature — combine it with read-only
mode for that.

| Toolset | Tools | What |
|---|---|---|
| `core` | 4 | `tekla_get_connection_info`, `tekla_get_model_summary`, `tekla_get_numbering_status`, `tekla_report_gap` — always on |
| `model` | 42 | queries, properties, geometry, grids, reference models, assemblies, environment, UDAs, part/connection/component writes, generators, model save/open |
| `analytics` | 10 | count / sum / group / distinct values, material breakdown, attribute search and UDA discovery, QA checks, selection, table export |
| `files` | 3 | `tekla_export_parts_file`, `tekla_set_udas_from_file`, `tekla_export_reference_objects_file` |
| `drawings` | 54 | every drawing tool, `tekla_capture_drawing` included (about 82k characters on its own) |
| `scripting` | 5 | `tekla_run_csharp`, `tekla_check_csharp`, API reference search and docs |
| `visual` | 2 | `tekla_capture_view`, `tekla_render_schematic` |

Both switches are read once at startup; restart the server (the MCP client) after changing them.

## Build from source

Developer notes, checks to run before a pull request and the repository layout are in
[CONTRIBUTING.md](../CONTRIBUTING.md).

```powershell
# Pass the TeklaVersion matching your installed Tekla (NuGet package version, year first —
# see docs/releasing.md for the exact per-year strings):
dotnet build TeklaMcp.sln -c Release -p:TeklaVersion=2023.0.1

# Open Tekla Structures and load a model, then:
dotnet run --no-build --project src/TeklaMcp.Server -f net48 -c Release
```

`--no-build` keeps the build you just made: a plain `dotnet run` rebuilds first without your
`-p:TeklaVersion`, i.e. against the default Tekla 2021 API, and the server then refuses a newer
Tekla as the wrong build.

Force the mock backend even on Windows (e.g. when Tekla is not open):

```powershell
$env:TEKLA_MCP_USE_MOCK = "1"
dotnet run --project src/TeklaMcp.Server -f net48
```

### Mock backend (no Tekla)

```bash
dotnet run --project src/TeklaMcp.Server
```

The server speaks MCP over **stdio** and waits for a client — that is expected. To exercise tools interactively:

```bash
npx @modelcontextprotocol/inspector dotnet run --project src/TeklaMcp.Server
```

A Python smoke-test script is available at [scripts/mcp_smoke_test.py](../scripts/mcp_smoke_test.py).

### Safe rebuild helper (Windows)

For local development, you can use [scripts/build-safe.ps1](../scripts/build-safe.ps1).  
It stops running `TeklaMcp.Server` processes (to avoid locked `net48` DLLs), then runs `clean` + `build`.
Pass `-TeklaVersion` with the NuGet version of your Tekla (table in
[releasing.md](releasing.md)); without it the script builds the default `2021.0.0`, which
only talks to Tekla 2021.

```powershell
# default: kill running server processes, clean, build (Release) for Tekla 2023
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-safe.ps1 -TeklaVersion 2023.0.1

# build server project only
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-safe.ps1 -ServerOnly

# faster loop: skip clean
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-safe.ps1 -SkipClean

# include Python MCP smoke test after build
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-safe.ps1 -RunSmokeTest
```
