# Tekla MCP Server

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![CI](https://github.com/tempalg1n/tekla-mcp-server/actions/workflows/ci.yml/badge.svg)](https://github.com/tempalg1n/tekla-mcp-server/actions/workflows/ci.yml)

An [MCP](https://modelcontextprotocol.io/) server that connects AI assistants to **Tekla Structures** models via the [Tekla Open API](https://developer.tekla.com/doc/tekla-structures/2026/tekla-structures-64304).

Ask natural-language questions about steel structures — weights, counts, materials, profiles, selections — and let the assistant query the open model for you.

> **Status: early development.** The server builds and runs on Windows and has been exercised on live Tekla 2023 models. The tool set is growing; APIs and behavior may change between releases. See [Roadmap](#roadmap).

**Languages:** English (this file) · [Русский](README.ru.md)

---

## Why this project

Tekla Structures holds rich BIM data — parts, assemblies, weights, classes, user-defined attributes — but that data is locked behind a Windows-only .NET API. AI assistants need a structured, safe bridge to read (and selectively update) model data without manual export or custom macros.

This server provides that bridge through the **Model Context Protocol**: a standard way for Claude, Cursor, and other MCP clients to call typed tools against the open Tekla model.

**What you can do today:**

- Inspect connection status, model name, and object counts
- Filter and search parts by type, class, profile, material, and name
- Filter objects by UDA or arbitrary attribute value (including attribute discovery by value)
- Compute weights and counts with the same filters used across tools
- Group metrics by field (type, class, profile, material, name)
- Analyze material breakdowns (bill-of-materials style)
- Analyze unique connection types for a given profile (beam-end proximity heuristic)
- Read the current UI selection in Tekla
- Select objects in the Tekla UI by filter
- Read and write user-defined attributes (UDAs) with safe preview-by-default writes

---

## Architecture

The server is written in **C#** because Tekla Open API ships as .NET assemblies (`.NET Framework 4.8`, x64). All MCP tools depend on a single abstraction — `ITeklaModelService` — with two backends:

| Backend | Build | Purpose |
|---|---|---|
| `MockTeklaModelService` | `net8.0` | Synthetic steel frame (~42 objects) for development and testing without Tekla |
| `TeklaModelService` | `net48` (Windows) | Live data from the open Tekla model (verified on Tekla 2023) |

```
┌──────────────┐  stdio (JSON-RPC)   ┌───────────────────────────────────────┐
│  MCP client  │ ──────────────────► │            TeklaMcp.Server            │
│ (Claude,     │                     │  MCP tools (tekla_*)                  │
│  Cursor, …)  │ ◄────────────────── │            │                          │
└──────────────┘                     │            ▼  ITeklaModelService      │
                                     │   ┌─────────────────┐  ┌────────────┐ │
                                     │   │ Mock (net8.0)   │  │Tekla(net48)│ │
                                     │   │ synthetic data  │  │ Open API   │ │
                                     │   └─────────────────┘  └─────┬──────┘ │
                                     └──────────────────────────────┼────────┘
                                                                    ▼
                                                       Tekla Structures (open model)
```

Details: [docs/architecture.md](docs/architecture.md).

---

## Repository layout

```
tekla-mcp-server/
├── README.md              ← you are here
├── README.ru.md           ← Russian translation
├── LICENSE
├── CONTRIBUTING.md
├── AGENTS.md              ← conventions for AI-assisted development
├── docs/
│   ├── architecture.md
│   └── tekla-api-notes.md
├── TeklaMcp.sln
├── src/
│   ├── TeklaMcp.Core/      ← interface + DTOs (netstandard2.0)
│   ├── TeklaMcp.Mock/      ← mock backend (netstandard2.0)
│   ├── TeklaMcp.Scripting/ ← C# script escape hatch + API reference search (netstandard2.0)
│   ├── TeklaMcp.Tekla/     ← Tekla Open API backend (net48, Windows)
│   └── TeklaMcp.Server/    ← MCP host + tools (net8.0; +net48 on Windows)
└── tests/
    └── TeklaMcp.Tests/     ← unit tests (net8.0, mock-only)
```

---

## Tools

All tools use the `tekla_` prefix.

| Tool | Description |
|---|---|
| `tekla_get_connection_info` | Check whether a model is open; return name, path, and active backend. |
| `tekla_report_gap` | Report a missing capability / insufficient data; returns a ready-to-file issue draft. Use instead of scripting around gaps. |
| `tekla_get_model_summary` | Model-wide summary: object count, total weight, breakdowns by type/class/profile/material. On huge models use `includeWeights=false` / `maxObjects` to keep it fast. |
| `tekla_list_objects` | List objects with core properties (with limit). |
| `tekla_find_objects` | Search by filters: type, class, profile, material, name. |
| `tekla_get_object_by_guid` | Fetch a single object by GUID. |
| `tekla_get_properties` | Read any named properties (report props, UDAs, built-ins) for an object by GUID. |
| `tekla_get_selected_objects` | Return objects currently selected in the Tekla UI. |
| `tekla_find_attributes_by_value` | Find likely attribute names by known value (`BK1` -> matching fields). |
| `tekla_analyze_by_material` | Material breakdown (count + weight per steel grade). |
| `tekla_count_objects` | Count objects matching filters (fast: no per-object data is materialized; an unfiltered count is instant). |
| `tekla_sum_weight` | Sum weight for objects matching filters. |
| `tekla_group_weight_by` | Group count + weight by field (`type`, `class`, `profile`, `material`, `name`, `assembly`). |
| `tekla_list_distinct_values` | Distinct values for a field with count + weight. |
| `tekla_list_assemblies` | List assembly marks (ASSEMBLY_POS) with part count + total weight. |
| `tekla_count_assemblies` | Count distinct assembly marks (unique assembly types). |
| `tekla_get_assembly_parts` | List all parts sharing a given assembly mark. |
| `tekla_find_modeling_issues` | QA battery: missing material/profile/class, zero weight, not-numbered; grouped with sample GUIDs. |
| `tekla_export_objects` | Export filtered objects as a CSV/Markdown table (bill-of-materials). |
| `tekla_analyze_profile_connections` | Estimate unique connection/node types for members of a profile. |
| `tekla_select_objects` | Select matching objects in the Tekla UI; supports UDA/attribute filters and `guidIn`; returns `selectedCount` + preview. |
| `tekla_get_object_udas` | Read UDA fields for an object by GUID. |
| `tekla_set_object_udas` | Set UDAs on one object (`apply=false` by default). |
| `tekla_set_udas_by_filter` | Bulk UDA update by filter (`apply=false` by default). |

### Write tools — create / edit / delete

> ⚠️ **Mutating tools.** Every write tool runs in **preview mode by default** (`apply=false`): it returns a plan (counts + preview) and changes nothing. Pass `apply=true` to commit. Created/modified objects are tagged with the `MCP_ORIGIN` UDA so they can be found and reverted; Tekla's native **Ctrl+Z** also undoes committed changes. Coordinates are **global** model coordinates (mm).

| Tool | Description |
|---|---|
| `tekla_list_grids` | List grid lines (axis, label, coordinate). |
| `tekla_resolve_point` | Resolve a point from axis labels + elevation (e.g. `1` × `Д` × `6000`). |
| `tekla_create_beam` | Create a beam between two points. |
| `tekla_create_column` | Create a vertical column at (x,y) from bottomZ to topZ. |
| `tekla_create_plate` | Create a contour plate from 3+ points. |
| `tekla_modify_part` | Edit one part: profile/material/class/name and/or endpoints. |
| `tekla_swap_handles` | Swap start/end handles of matching parts. |
| `tekla_delete_objects` | Delete objects by filter or GUID list. |
| `tekla_create_beam_between_grids` | Create a beam between two grid intersections at an elevation. |
| `tekla_create_column_grid` | Create columns at every X×Y coordinate intersection. |
| `tekla_generate_frame` | Generate a full bayed frame (columns + per-story beams). |
| `tekla_straighten_columns` | Re-plumb crooked columns (align top over bottom). |
| `tekla_fix_column_handles` | Re-orient flipped columns (swap inverted handles). |

### C# scripting escape hatch

The Tekla Open API is far larger than this tool set. When no dedicated tool covers a need, an agent can run a short, **policy-checked C# script** against the live model — after verifying the API signatures offline:

| Tool | Description |
|---|---|
| `tekla_search_api` | Keyword search over the locally generated Tekla Open API reference (types + member signatures). |
| `tekla_get_api_doc` | Full reference page for one type: every constructor/property/method signature with summaries. |
| `tekla_run_csharp` | Run a C# script (top-level statements, Tekla namespaces pre-imported, `Print(...)` for output, last expression = JSON return value). |

Safety model:

- **Read-only by default, writes by consent.** Mutating members (`Insert`/`Modify`/`Delete`/`CommitChanges`/`SetUserProperty`/`Operation.*`) are rejected unless the call sets `allowMutations=true` — and the tool contract obliges the agent to show you the script and get your explicit go-ahead before setting it. Scripted changes should be tagged with the `MCP_ORIGIN` UDA where practical; committed changes are undoable with Tekla's **Ctrl+Z**.
- **No host access.** A syntax-level policy bans file system, network, processes, reflection, threads and `Console` (stdout belongs to the MCP protocol). `#r`/`#load` and `await` are rejected too.
- **Hard timeout** (default 60 s, max 600 s) on a dedicated thread.
- **Bounded output.** `Print` is capped at 500 lines; the return value is rendered to JSON defensively (depth/size caps, per-property try/catch).
- On the **mock backend** scripts are validated and compiled but never executed (compilation needs Tekla DLLs — point `TEKLA_MCP_SCRIPT_REF_DIR` at a folder with `Tekla.Structures*.dll`, e.g. extracted from the NuGet packages).

This is a pragmatic barrier for well-behaved agents, not a security sandbox — the script runs with the server's privileges. Prefer the dedicated write tools (preview-by-default) for changes; the scripted-write path is for cases they don't cover.

`tekla_search_api` reads the reference generated by [tools/TeklaApiDoc](tools/TeklaApiDoc/README.md) from `reference/tekla-api` (or `TEKLA_MCP_API_REF_DIR`). Generate it once per machine — with it, agents verify signatures instead of hallucinating them.

### Reporting gaps

The server ships MCP **instructions** giving connecting agents an escalation ladder: use the dedicated tools first; bridge one-off needs with the scripting escape hatch (never external ad-hoc automation, never fabricated data); and call `tekla_report_gap` for anything missing or recurring. `tekla_report_gap` returns a ready-to-file GitHub issue draft (and logs the request locally) for you to file — recurring scripts are exactly the signal that a first-class tool should be built. The server never creates issues itself (it holds no GitHub credentials).

### Shared filter parameters

Most query and analytics tools accept:

- `type` — exact object type (`Beam`, `ContourPlate`, `Bolt`, …)
- `class` — exact class
- `profile` — profile substring
- `material` — material substring
- `nameContains` — name substring
- `udaName` + `udaEquals` — exact UDA match
- `attributeName` + `attributeEquals` / `attributeContains` — exact/substring match for any known attribute
- `guidIn` — explicit GUID allow-list (for `tekla_select_objects`)
- `useSelection` — scope the tool to the **current Tekla UI selection** instead of the whole model (faster; enables "analyze what I selected" workflows)

### Example prompts

- “What is the total weight of columns (type `Beam`, name contains `Column`)?”
- “Show the top materials by weight in the current model.”
- “Group beams by profile and show weight per group.”
- “How many objects have class 20?”
- “Select all class-20 objects in Tekla.”
- “Find where value `BK1` is stored (`tekla_find_attributes_by_value`).”
- “Select columns where `RU_FN1_MRK = BK1`.”
- “How many unique connection types do beams with profile `20P` have?”
- “Read UDA `USER_FIELD_1` and `USER_PHASE` for this GUID.”
- “Preview setting `USER_FIELD_1=KMD; USER_PHASE=2` on all `I30K1` columns without applying.”
- “I’ve selected some parts — sum their weight and group by profile (`useSelection=true`).”
- “How many unique assembly marks are there, and which are the heaviest?”
- “List the parts of assembly `B1`.”
- “Run modeling-issue checks and show what’s missing material or not numbered.”
- “Read `VOLUME`, `AREA` and `PHASE` for this GUID (`tekla_get_properties`).”
- “Export all class-20 beams as a CSV bill-of-materials.”

### UDA write safety

`tekla_set_object_udas` and `tekla_set_udas_by_filter` default to **preview mode** (`apply=false`). Pass `apply=true` to commit changes. Bulk writes are capped by `limit` (default 200 objects).

---

## Requirements

**Production (live Tekla model):**

- Windows x64
- Tekla Structures installed, **running with a model open**
- [.NET SDK 8+](https://dotnet.microsoft.com/download)
- [.NET Framework 4.8 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net48)

**Development / testing (mock backend, no Tekla):**

- [.NET SDK 8+](https://dotnet.microsoft.com/download)

---

## Quick start

### Windows — live Tekla model

```powershell
# Open Tekla Structures and load a model, then:
dotnet build TeklaMcp.sln -c Release
dotnet run --project src/TeklaMcp.Server -f net48 -c Release
```

> **Works with any Tekla version (2021+).** The live build is **universal** — it does not bundle Tekla DLLs and loads them from your installed/running Tekla at runtime, so one build matches whatever version you run. No per-version download. If auto-detection fails, set the `TEKLA_BIN_DIR` environment variable to your Tekla `bin` folder. See [docs/tekla-api-notes.md](docs/tekla-api-notes.md#tekla-version-compatibility).
>
> **If Tekla is open but the server says "Not connected":** the error message now lists the client channel and the `Tekla.Structures.Model-*` named pipes Tekla actually publishes. The server auto-matches the published channel (some setups publish `…-Console:<version>` instead of the default `…-:<version>`) and ignores stale Tekla assemblies in the GAC. To force a specific channel, set the `TEKLA_MCP_CHANNEL` environment variable to the exact pipe name.

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

A Python smoke-test script is available at [scripts/mcp_smoke_test.py](scripts/mcp_smoke_test.py).

### Safe rebuild helper (Windows)

For local development, you can use [scripts/build-safe.ps1](scripts/build-safe.ps1).  
It stops running `TeklaMcp.Server` processes (to avoid locked `net48` DLLs), then runs `clean` + `build`.

```powershell
# default: kill running server processes, clean, build (Release)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-safe.ps1

# build server project only
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-safe.ps1 -ServerOnly

# faster loop: skip clean
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-safe.ps1 -SkipClean

# include Python MCP smoke test after build
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-safe.ps1 -RunSmokeTest
```

### Install from a GitHub Release (recommended for Windows)

1. Open **[Releases](https://github.com/tempalg1n/tekla-mcp-server/releases)** and download `TeklaMcp.Server-{version}-net48-win-x64.zip`.
2. Extract the zip to a folder (keep all `.dll` files next to the `.exe`).
3. Point your MCP client at `TeklaMcp.Server.exe` (see [MCP client configuration](#mcp-client-configuration)).

See [docs/releasing.md](docs/releasing.md) for how maintainers publish releases.

---

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

**Live Tekla** (Windows — prefer a built executable):

```json
{
  "mcpServers": {
    "tekla": {
      "command": "C:\\path\\to\\tekla-mcp-server\\src\\TeklaMcp.Server\\bin\\Release\\net48\\TeklaMcp.Server.exe"
    }
  }
}
```

---

## Roadmap

- [x] Core read tools: connection, summary, list, find, selection
- [x] Analytics: count, weight sum, grouping, material breakdown
- [x] UI selection via `tekla_select_objects`
- [x] UDA read/write with preview-by-default safety
- [ ] Broader object coverage: bolts, assemblies, rebar, geometry
- [ ] Automated tests for Core and Mock layers
- [ ] Compatibility matrix for Tekla 2023–2026

Contributions welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).

Release history: [CHANGELOG.md](CHANGELOG.md).

---

## Credits

This project was **designed and developed with [Claude Opus 4.8](https://www.anthropic.com/claude)** (Anthropic), using AI-assisted architecture, implementation, and documentation.

Tekla Structures is a product of [Trimble](https://www.tekla.com/). This project is not affiliated with or endorsed by Trimble.

---

## License

[MIT](LICENSE) — see the LICENSE file for details.
