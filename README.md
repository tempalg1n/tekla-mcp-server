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
└── src/
    ├── TeklaMcp.Core/     ← interface + DTOs (netstandard2.0)
    ├── TeklaMcp.Mock/     ← mock backend (netstandard2.0)
    ├── TeklaMcp.Tekla/    ← Tekla Open API backend (net48, Windows)
    └── TeklaMcp.Server/   ← MCP host + tools (net8.0; +net48 on Windows)
```

---

## Tools

All tools use the `tekla_` prefix.

| Tool | Description |
|---|---|
| `tekla_get_connection_info` | Check whether a model is open; return name, path, and active backend. |
| `tekla_get_model_summary` | Model-wide summary: object count, total weight, breakdowns by type/class/profile/material. |
| `tekla_list_objects` | List objects with core properties (with limit). |
| `tekla_find_objects` | Search by filters: type, class, profile, material, name. |
| `tekla_get_object_by_guid` | Fetch a single object by GUID. |
| `tekla_get_properties` | Read any named properties (report props, UDAs, built-ins) for an object by GUID. |
| `tekla_get_selected_objects` | Return objects currently selected in the Tekla UI. |
| `tekla_find_attributes_by_value` | Find likely attribute names by known value (`BK1` -> matching fields). |
| `tekla_analyze_by_material` | Material breakdown (count + weight per steel grade). |
| `tekla_count_objects` | Count objects matching filters. |
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
