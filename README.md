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
- Search and QA the drawing list without opening drawings
- Create, update, issue, print, and manage assembly/single-part/cast-unit/GA drawings
- Inspect and edit drawing views, annotations, graphics, dimensions, marks, and symbols

---

## Architecture

The server is written in **C#** because Tekla Open API ships as .NET assemblies (`.NET Framework 4.8`, x64). All MCP tools depend on a single abstraction — `ITeklaModelService` — with two backends:

| Backend | Build | Purpose |
|---|---|---|
| `MockTeklaModelService` | `net8.0` | Synthetic steel frame (~42 objects) for development and testing without Tekla |
| `TeklaModelService` | `net48` (Windows) | Live data from the open Tekla model (verified on Tekla 2023) |

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

All tools use the `tekla_` prefix. v0.7.0 exposes **100 tools** in total, including **51
drawing-specific tools**.

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
| `tekla_get_reference_geometry` | Inspect selected/ID-addressed IFC objects: external GUID/entity, dimensions, world AABB and capped face polygons. |
| `tekla_get_solid_bbox` | Read explicit native-part solid bounding boxes (or current selection). |
| `tekla_list_control_lines` | List ControlLine start/end coordinates. |
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
| `tekla_list_connections` | List actual Connection/component objects attached to a part. |
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
| `tekla_create_beam` | Create a beam between two points, with optional Plane/Rotation/Depth or Position copied from an exemplar. |
| `tekla_create_beams` | Create up to 200 beams in one structured batch. |
| `tekla_create_column` | Create a vertical column at (x,y) from bottomZ to topZ. |
| `tekla_create_plate` | Create a contour plate from 3+ points. |
| `tekla_modify_part` | Edit properties/endpoints/Position, or copy Position from an exemplar. |
| `tekla_create_connection` | Create a system/custom Connection (primary, secondaries, UpVector, attributes file). |
| `tekla_copy_connection` | Copy a Connection's exact name/number/orientation to new parts. |
| `tekla_swap_handles` | Swap start/end handles of matching parts. |
| `tekla_delete_objects` | Delete objects by filter or GUID list. |
| `tekla_create_beam_between_grids` | Create a beam between two grid intersections at an elevation. |
| `tekla_create_column_grid` | Create columns at every X×Y coordinate intersection. |
| `tekla_generate_frame` | Generate a full bayed frame (columns + per-story beams). |
| `tekla_straighten_columns` | Re-plumb crooked columns (align top over bottom). |
| `tekla_fix_column_handles` | Re-orient flipped columns (swap inverted handles). |

### Drawing tools

Drawing tools use Tekla's separate Drawing API and its editor state. Drawing-list queries work
with the editor open or closed. View/object inspection and content editing require an **active
drawing**; creating drawings, updating them from the model, deleting them, and PDF export have
stricter closed-editor/closed-drawing preconditions noted below.

Drawing, view, and object addresses should be obtained from the corresponding list tool.
`DrawingInternal` ID/ID2 values are exposed when Tekla provides them; they are a best-effort,
version-sensitive API. Drawing keys fall back to a composite of public properties when IDs are
unavailable. View/object indices are explicitly ephemeral — re-list after insert/delete or other
structural edits, and prefer non-zero `ID:ID2` pairs.

Persistent drawing changes are preview-by-default. The backend attempts to stamp
`MCP_ORIGIN`, but drawing-side UDA support varies by object and environment; do not rely on the
tag as the only rollback mechanism. Pay particular attention to
`tekla_close_drawing(save=false)`, which discards unsaved editor changes when applied.

#### Drawing discovery and editor selection

| Tool | Description |
|---|---|
| `tekla_get_drawing_status` | Check Drawing API connectivity and active-editor state. |
| `tekla_get_active_drawing` | Return the currently open drawing, or `null` when the editor is closed. |
| `tekla_list_drawings` | Search drawing-list rows by key/type/mark/name/title/model GUID/status/flags/selection. |
| `tekla_get_selected_drawings` | Return rows selected in Tekla's Drawing List dialog. |
| `tekla_get_drawing_summary` | Aggregate counts by type/status and issued/locked/ready/stale state, with explicit scan truncation. |
| `tekla_find_drawing_issues` | QA heuristics for stale, issued-but-modified, ready-but-stale, and missing mark/name rows. |
| `tekla_get_drawing_model_objects` | Return a bounded/paginated page of full model identifiers represented by a drawing. |
| `tekla_get_drawing_sheet` | Read active-sheet width, height, origin and configured layout size/mode in paper millimetres. |
| `tekla_list_drawing_views` | List active-drawing views with best-effort ID/ID2, paper frame, scale, restriction box, and coordinate systems. |
| `tekla_list_drawing_objects` | Filter active-drawing objects by ID/index/type/view/model GUID/text/selection, with optional geometry and UDAs. |
| `tekla_get_selected_drawing_objects` | Return the current drawing-editor selection. |
| `tekla_select_drawing_objects` | Select/highlight matching active-drawing objects (an immediate UI side effect, not a model mutation). |

#### Drawing lifecycle and output

All state-changing tools in this table are **preview-by-default** (`apply=false`).

| Tool | Description |
|---|---|
| `tekla_open_drawing` | Open one drawing by opaque key or an unambiguous exact mark; refuses to replace an active drawing. |
| `tekla_save_drawing` | Save the active drawing. |
| `tekla_close_drawing` | Close the active drawing; `save=false` explicitly discards unsaved editor changes. |
| `tekla_create_drawing` | Create one assembly, single-part, cast-unit, or GA drawing; editor must be closed. |
| `tekla_create_drawings` | Batch-create up to 50 drawings; editor must be closed. |
| `tekla_create_drawings_from_rule` | Run a saved Tekla AutoDrawing rule for up to 50 model GUIDs; editor must be closed. |
| `tekla_modify_drawings` | Batch-edit name, titles, frozen/locked/master/ready flags. |
| `tekla_delete_drawings` | Delete matched drawings; an active drawing cannot be deleted. |
| `tekla_issue_drawings` | Issue matched drawings. |
| `tekla_unissue_drawings` | Remove issued state from matched drawings. |
| `tekla_update_drawings` | Update matched closed drawings from the model; numbering must be up to date. |
| `tekla_place_drawing_views` | Ask Tekla to auto-place views on the matched active drawing. |
| `tekla_export_drawings_pdf` | Print matched closed drawings to PDF with color/orientation/paper/scale options. |

#### Views and drawing content

These tools operate on the **active drawing** and are also preview-by-default. Saved attribute
file names are passed to Tekla and resolved by the running environment.

| Tool | Description |
|---|---|
| `tekla_create_drawing_view` | Create a front/top/back/bottom/3D view on a non-GA drawing. |
| `tekla_create_ga_drawing_view` | Create a GA model view from explicit global view/display coordinate systems and a restriction box. |
| `tekla_create_section_view` | Create a straight or curved section view and section mark from a source view. |
| `tekla_create_detail_view` | Create a detail view and detail mark from a source view. |
| `tekla_modify_drawing_view` | Edit a view's name, sheet origin/frame, scale, and rotations. |
| `tekla_delete_drawing_view` | Delete a view and its children. |
| `tekla_create_drawing_objects` | Batch-create up to 200 structured annotations/graphics/dimensions/marks. |
| `tekla_create_drawing_text` | Create text in a view or on the sheet. |
| `tekla_create_drawing_line` | Create a line. |
| `tekla_create_drawing_rectangle` | Create a rectangle. |
| `tekla_create_drawing_circle` | Create a circle. |
| `tekla_create_drawing_arc` | Create an arc from three points or two points plus radius. |
| `tekla_create_drawing_polyline` | Create a polyline. |
| `tekla_create_drawing_polygon` | Create a closed polygon. |
| `tekla_create_revision_cloud` | Create a revision cloud graphic. |
| `tekla_create_straight_dimension` | Create a straight dimension set. |
| `tekla_create_angle_dimension` | Create an angle dimension. |
| `tekla_create_radius_dimension` | Create a radius dimension. |
| `tekla_create_curved_dimension` | Create a radial or orthogonal curved dimension set. |
| `tekla_create_drawing_mark` | Create a mark for a model object represented in a target view. |
| `tekla_create_level_mark` | Create a level mark. |
| `tekla_create_drawing_symbol` | Create a symbol from a Tekla `.sym` library. |
| `tekla_modify_drawing_objects` | Batch-change text, relative position, visibility, or loaded attributes. |
| `tekla_delete_drawing_objects` | Delete objects by best-effort ID/type/current editor selection. |
| `tekla_merge_drawing_marks` | Merge compatible marks by best-effort IDs/current selection. |
| `tekla_split_drawing_marks` | Split merged mark sets by best-effort IDs/current selection. |

Drawing inputs use three explicit coordinate spaces:

- `view` — coordinates local to the target view/display coordinate system (model millimetres
  before drawing scale);
- `model` — global model coordinates, transformed into the target view through its
  `DisplayCoordinateSystem`;
- `sheet` — paper coordinates in millimetres; target the sheet with `viewIndex=-1` and no
  `viewId`.

View insertion/origin/frame values and dimension-line distances are paper millimetres. Section
cut/detail points are source-view-local; section depths are model millimetres. Geometry returned
by `tekla_list_drawing_objects` is in the object's Tekla view/sheet coordinate system, not
silently converted to global model coordinates. Use the coordinate systems returned by
`tekla_list_drawing_views` when transforming values.

Geometry extraction is richest for graphics, text, marks and angle dimensions. Some complex
dimension sets, level marks, symbols and model-linked drawing objects currently expose only
their available bounding box/identity; use `tekla_run_csharp` for a one-off deeper read and
report recurring gaps.

### C# scripting escape hatch

The Tekla Open API is far larger than this tool set. When no dedicated tool covers a need, an agent can run a short, **policy-checked C# script** against the live model — after verifying the API signatures offline:

| Tool | Description |
|---|---|
| `tekla_search_api` | Keyword search over the locally generated Tekla Open API reference (types + member signatures). |
| `tekla_get_api_doc` | Full reference page for one type: every constructor/property/method signature with summaries. |
| `tekla_get_api_reference_status` | Report whether the local offline reference is ready and how to set it up. |
| `tekla_check_csharp` | Policy-check and compile the exact source without executing it; returns SHA-256 + detected mutations for approval. |
| `tekla_run_csharp` | Run a C# script (top-level statements, Tekla namespaces pre-imported, `Print(...)` for output, last expression = JSON return value). |

Safety model:

- **Read-only by default, writes by consent.** Mutating members (`Insert`/`Modify`/`Delete`/`CommitChanges`/`SetUserProperty`/`Operation.*`) are rejected unless the call sets `allowMutations=true` — and the tool contract obliges the agent to show you the script and get your explicit go-ahead before setting it. Scripted changes should be tagged with the `MCP_ORIGIN` UDA where practical; committed changes are undoable with Tekla's **Ctrl+Z**.
- **No host access.** A syntax-level policy bans file system, network, processes, reflection, threads and `Console` (stdout belongs to the MCP protocol). `#r`/`#load` and `await` are rejected too.
- **Execution deadline** (default 60 s, max 600 s) on a dedicated thread. Abort is best-effort
  around Tekla remoting; the result warns when worker termination cannot be confirmed.
- **Compile before consent.** `tekla_check_csharp` never connects to or executes against the model, so a proposed mutation can be verified before approval; use its `codeSha256` to identify the exact reviewed source. The live backend compiles against every installed managed `Tekla.Structures*.dll` (including Drawing/Dialog when present). Drawing is not a global import because its `Part`/`View` types are ambiguous — use `using TSD = Tekla.Structures.Drawing;`.
- **Bounded output.** `Print` uses private host-owned storage capped at 500 lines / 64,000 characters; the return value is rendered inside the timeout worker as capped, always-valid JSON.
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
- “Show all assembly drawings that are issued but modified, without opening them.”
- “Preview updating the drawings selected in the Drawing List; do not apply.”
- “Open drawing `A-12`, list its views, and show the represented model GUIDs.”
- “In the active view, preview a straight dimension through these global model points.”
- “Create a red revision cloud and note on the active drawing, preview first.”
- “Preview exporting the selected closed drawings to A3 black-and-white PDFs.”

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

### Windows — install from a GitHub Release (recommended)

Releases ship **one zip per Tekla version** — pick the one matching *your* Tekla:

1. Open **[Releases](https://github.com/tempalg1n/tekla-mcp-server/releases)** and download the zip for your Tekla version: `TeklaMcp.Server-vX.Y.Z-tekla2021.zip` … `-tekla2026.zip` (e.g. running Tekla Structures 2023 → `…-tekla2023.zip`).
2. Extract the zip to a folder (keep all `.dll` files next to the `.exe`).
3. Open Tekla Structures with a model, and point your MCP client at `TeklaMcp.Server.exe` (see [MCP client configuration](#mcp-client-configuration)).

> **Why per-version zips?** The Tekla Open API protocol is version-locked, so the server must be compiled for the Tekla it talks to. The zip does **not** bundle Tekla DLLs — it loads them from your installed Tekla at runtime, verifying the version matches. A mismatched zip fails with a clear message naming the right one. If auto-detection of the Tekla `bin` folder fails, set the `TEKLA_BIN_DIR` environment variable. See [docs/tekla-api-notes.md](docs/tekla-api-notes.md#tekla-version-compatibility).
>
> **If Tekla is open but the server says "Not connected":** the error message lists the client channel and the `Tekla.Structures.Model-*` named pipes Tekla actually publishes. The server auto-matches the published channel (some setups publish `…-Console:<version>` instead of the default `…-:<version>`). To force a specific channel, set the `TEKLA_MCP_CHANNEL` environment variable to the exact pipe name.

See [docs/releasing.md](docs/releasing.md) for how maintainers publish releases.

### Windows — build from source

```powershell
# Pass the TeklaVersion matching your installed Tekla (NuGet package version, year first —
# see docs/releasing.md for the exact per-year strings):
dotnet build TeklaMcp.sln -c Release -p:TeklaVersion=2023.0.1

# Open Tekla Structures and load a model, then:
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
- [x] Drawing-list queries, lifecycle, QA, creation, update, issue, and PDF output
- [x] Drawing views, graphics, annotations, dimensions, marks, and editor selection
- [ ] Broader object coverage: bolts, assemblies, rebar, geometry
- [x] Automated tests for Core and Mock layers
- [x] Per-version build matrix for Tekla 2021–2026

Contributions welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).

Release history: [CHANGELOG.md](CHANGELOG.md).

---

## Credits

This project was **designed and developed with [Claude Opus 4.8](https://www.anthropic.com/claude)** (Anthropic), using AI-assisted architecture, implementation, and documentation.

Tekla Structures is a product of [Trimble](https://www.tekla.com/). This project is not affiliated with or endorsed by Trimble.

---

## License

[MIT](LICENSE) — see the LICENSE file for details.
