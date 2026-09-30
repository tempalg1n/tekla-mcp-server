# Tekla MCP Server

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![CI](https://github.com/tempalg1n/tekla-mcp-server/actions/workflows/ci.yml/badge.svg)](https://github.com/tempalg1n/tekla-mcp-server/actions/workflows/ci.yml)

An [MCP](https://modelcontextprotocol.io/) server that connects AI assistants to **Tekla Structures** models via the [Tekla Open API](https://developer.tekla.com/doc/tekla-structures/2026/tekla-structures-64304).

Ask natural-language questions about steel structures — weights, counts, materials, profiles, selections — and let the assistant query the open model for you.

> **Status: early development (v0.8.0).** The server builds and runs on Windows and has been exercised on live Tekla 2021 and 2023 models. The Tekla 2024–2026 builds compile but are untested — testers wanted. The tool set is growing; APIs and behavior may change between releases. See [Known limitations](#known-limitations) and [Roadmap](#roadmap).

**Languages:** English (this file) · [Русский](README.ru.md)

---

## Why this project

Tekla Structures holds rich BIM data — parts, assemblies, weights, classes, user-defined attributes — but that data is locked behind a Windows-only .NET API. AI assistants need a structured, safe bridge to read (and selectively update) model data without manual export or custom macros.

This server provides that bridge through the **Model Context Protocol**: a standard way for Claude, Cursor, and other MCP clients to call typed tools against the open Tekla model.

**What you can do today:**

- Inspect connection status, model name, object counts, and which server and Tekla build answered
- Filter and search parts by type, class, profile, material, and name
- Filter objects by UDA or arbitrary attribute value — including still-blank UDAs — and discover attribute names by value
- Compute weights and counts with the same filters used across tools, streamed and paged for 400k+-object models
- Group metrics by field (type, class, profile, material, name, assembly mark, any UDA or attribute)
- Analyze material breakdowns (bill-of-materials style)
- Discover which UDA fields exist; read environment catalogs (profiles, materials, components, UDA definitions) and advanced options
- Read exact geometry: part solids with cuts, curved-member arcs for dimensioning, bounding boxes, grids, IFC reference objects
- See the model: screenshots of the live Tekla view and server-drawn numbered schematics
- Move bulk data through files: export parts or IFC objects with geometry, write UDAs from a GUID-keyed file
- Analyze connections: actual components on a part or along an axis, and unique connection types for a profile (beam-end proximity heuristic)
- Read the current UI selection in Tekla
- Select objects in the Tekla UI by filter
- Create, modify, and delete parts, connections, and plugin components; save and switch models — preview-by-default, with explicit write outcomes
- Read and write user-defined attributes (UDAs) with safe preview-by-default writes
- Run policy-checked C# scripts against the Open API when no dedicated tool exists
- Search and QA the drawing list without opening drawings *(experimental)*
- Create, update, issue, print, and manage assembly/single-part/cast-unit/GA drawings *(experimental)*
- Inspect and edit drawing views, annotations, graphics, dimensions, marks, and symbols *(experimental)*

---

## Architecture

The server is written in **C#** because Tekla Open API ships as .NET assemblies (`.NET Framework 4.8`, x64). All MCP tools depend on a single abstraction — `ITeklaModelService` — with two backends:

| Backend | Build | Purpose |
|---|---|---|
| `MockTeklaModelService` | `net8.0` | Synthetic steel frame (43 objects, including a curved `PolyBeam` arch) for development and testing without Tekla |
| `TeklaModelService` | `net48` (Windows) | Live data from the open Tekla model (exercised on Tekla 2021 and 2023) |

Details: [docs/architecture.md](docs/architecture.md).

---

## Repository layout

```
tekla-mcp-server/
├── README.md              ← you are here
├── README.ru.md           ← Russian translation
├── CHANGELOG.md
├── LICENSE
├── CONTRIBUTING.md
├── AGENTS.md              ← conventions for AI-assisted development
├── docs/
│   ├── architecture.md
│   ├── tekla-api-notes.md
│   ├── backlog.md         ← analysed and deferred work
│   ├── releasing.md
│   └── releases/          ← per-version release notes
├── TeklaMcp.sln
├── src/
│   ├── TeklaMcp.Core/      ← interface + DTOs (netstandard2.0)
│   ├── TeklaMcp.Mock/      ← mock backend (netstandard2.0)
│   ├── TeklaMcp.Scripting/ ← C# script escape hatch + API reference generation/search (netstandard2.0)
│   ├── TeklaMcp.Tekla/     ← Tekla Open API backend (net48, Windows)
│   └── TeklaMcp.Server/    ← MCP host + tools (net8.0; +net48 on Windows)
├── tests/
│   ├── TeklaMcp.Tests/     ← unit tests (net8.0, mock-only)
│   └── TeklaMcp.Smoke/     ← MCP stdio smoke test (initialize, tools/list, queries)
├── tools/
│   └── TeklaApiDoc/        ← CLI for the offline Tekla Open API reference
└── scripts/                ← build helper + Python smoke test
```

---

## Tools

All tools use the `tekla_` prefix. The current build exposes **116 tools** in total, including
**51 drawing-specific tools**.

| Tool | Description |
|---|---|
| `tekla_get_connection_info` | Check whether a model is open; return name, path, active backend, and which server answered (version, PID, start time, Tekla build/bin folder, running Tekla processes, `lastReconnect`). A lost connection is explained with cause and action instead of a raw remoting error. |
| `tekla_report_gap` | Report a missing capability / insufficient data; returns a ready-to-file issue draft. Use instead of scripting around gaps. |
| `tekla_get_model_summary` | Model-wide summary: object count, total weight, breakdowns by type/class/profile/material. On huge models use `includeWeights=false` / `maxObjects` to keep it fast. |
| `tekla_list_objects` | List objects with core properties (with limit; `useSelection` for the UI selection). |
| `tekla_find_objects` | Search by filters: type, class, profile, material, name, UDA/attribute. `udaIsEmpty=true` selects objects whose UDA is still blank. |
| `tekla_get_object_by_guid` | Fetch a single object by GUID. |
| `tekla_get_properties` | Read any named properties (report props, UDAs, built-ins) for an object by GUID; names that resolve to nothing are listed in `notFound`. |
| `tekla_get_selected_objects` | Return objects currently selected in the Tekla UI. |
| `tekla_get_reference_geometry` | Inspect IFC/reference objects by selection, integer id or IFC GlobalId: external GUID/entity, dimensions, world AABB, placement and capped face polygons (falls back to parsing the IFC file, labelled via `aabbSource`/`placementSource`). |
| `tekla_get_solid_bbox` | Read explicit native-part solid bounding boxes (or current selection). |
| `tekla_get_part_solid` | The real shape of up to 20 parts: faces with outward normals and the part that produced them (cuts), outer/inner loops, vertices — GLOBAL mm, cuts and fittings applied (or `RAW`). Capped (`maxFaces`/`maxPoints`) with an explicit `truncated` flag; faces are never cut in half. |
| `tekla_get_part_curve_geometry` | Exact centerline of one part for dimensioning curved members: a PolyBeam's own lines and arcs (center, start/mid/end, radius, sweep, arc length, chord, sagitta), arcs merged per physical bend, inner/outer arcs of round tubes and bars, the modelled contour with chamfers, and optionally every point in an active-drawing view. |
| `tekla_list_control_lines` | List ControlLine start/end coordinates. |
| `tekla_list_grids` | List grid lines and levels with their real Tekla labels: axis (X/Y/Z), label, global coordinate, grid id and end points. Handles several grids with their own origins. |
| `tekla_resolve_point` | Resolve a point from axis labels + elevation (e.g. `1` × `Д` × `6000`). |
| `tekla_find_attributes_by_value` | Find likely attribute names by known value (`BK1` -> matching fields). Reports scan coverage (`scannedObjects`/`truncated`) so "no match" is never mistaken for "absent". Scans physical parts by default (`partsOnly`). |
| `tekla_discover_udas` | Sample objects and report which UDA fields actually exist: fill counts, distinct values, top values. The "which field holds X?" starting point. |
| `tekla_get_advanced_options` | Read advanced options (`XS_MACRO_DIRECTORY`, `XS_FIRM`, …) as the running Tekla resolves them; optionally split into paths. Unknown options come back as `found=false`. |
| `tekla_list_catalog` | Page through an environment catalog: library/parametric profiles, materials, components (with numbers) or UDA definitions (optionally per object type). `details=true` adds dimensions, densities, labels. The authoritative "what exists" list next to the sample-based `tekla_discover_udas`. |
| `tekla_analyze_by_material` | Material breakdown (count + weight per steel grade) over the whole model, unpaged — on large models prefer `tekla_group_weight_by` with `groupBy=material`. |
| `tekla_count_objects` | Count objects matching filters, including `udaIsEmpty` and `useSelection`. No per-object data is materialized. |
| `tekla_sum_weight` | Sum weight for objects matching filters. Streams without materializing objects; physical parts by default (`partsOnly=false` widens); pages huge models via `maxObjects` + `cursor`. |
| `tekla_group_weight_by` | Group count + weight by field (`type`, `class`, `profile`, `material`, `name`, `assembly`, `uda:NAME`, `attr:NAME`). Streams (no solids); physical parts by default; pages via `maxObjects` + `cursor`; empty values land in `(none)`, groups beyond `limit` in `(other)`. |
| `tekla_list_distinct_values` | Distinct values for a field with count + weight (same engine and paging as `tekla_group_weight_by`). |
| `tekla_list_assemblies` | List assembly marks (ASSEMBLY_POS) with part count + total weight. Requires a numbered model. |
| `tekla_count_assemblies` | Count distinct assembly marks (unique assembly types). |
| `tekla_get_assembly_parts` | List all parts sharing a given assembly mark. |
| `tekla_find_modeling_issues` | QA battery: missing material/profile/class, zero weight, not-numbered; grouped with sample GUIDs. |
| `tekla_export_objects` | Export filtered objects as a CSV/Markdown table (bill-of-materials), capped by `limit`. For more rows or geometry use `tekla_export_parts_file`. |
| `tekla_analyze_profile_connections` | Estimate unique connection/node types for members of a profile. |
| `tekla_list_connections` | List actual Connection/component objects attached to a part: exact API name/number (the UI shows a different form), primary/secondary GUIDs, UpVector, status. |
| `tekla_find_connections` | Find connections across many parts (filter/selection/bbox), filter by component name/number and aggregate them by name (`byName`). Its GUIDs feed `tekla_modify_connections`. |
| `tekla_select_objects` | Select matching objects in the Tekla UI; supports UDA/attribute filters and `guidIn`; `useSelection=true` narrows the current selection; returns `selectedCount` + preview. |
| `tekla_get_object_udas` | Read UDA fields for an object by GUID. |
| `tekla_set_object_udas` | Set UDAs on one object (`apply=false` by default). |
| `tekla_set_udas_by_filter` | Bulk UDA update by filter (`apply=false` by default). |

### Seeing the model — pictures for agents

Agents otherwise reason about a model only through property lists and coordinates. These two tools
answer with an **MCP image block** — the agent actually sees the picture — plus a JSON block that
maps what is on it back to GUIDs and model millimetres. One picture costs about 1–1.6k tokens.

| Tool | Description |
|---|---|
| `tekla_capture_view` | Screenshot of a **live Tekla model view**: exactly what Tekla renders (profiles, bolts, welds, cuts, reference models, class colours). Default: the active view as it is, no side effects. With `guids`/`useSelection` it zooms to the targets, colours them red and ghosts everything else; `labels=true` numbers them; `direction` rotates a 3D view. `restore=true` (default) puts camera and colours back right after the capture. Windows + live Tekla; the mock returns a labelled schematic stand-in. |
| `tekla_render_schematic` | **Beta.** Numbered schematic drawn by the server itself — plan, elevations or iso — with focus objects (GUIDs, selection or the usual filters) in colour, context parts in grey, grid axes with their real labels, levels, coordinate ticks and a legend (number → GUID, pixel → mm). No Tekla UI involved; works on the mock too. Geometry is simplified (reference lines, contours, bounding boxes) and dense models can look cluttered — narrow the scope with `region` or filters. |

> **What the capture can see.** Only a view that is visible on screen: the active view, or views
> tiled side by side. A view hidden behind another view, or a minimized Tekla, is refused with the
> list of open views. Pixels come from Tekla's own view window (`PrintWindow`), never from a screen
> copy, so no other application's window can end up in the picture.

### File exchange — bulk data in and out

Some work does not fit through an MCP response, because a response is an LLM context. Reconciling
a model against an IFC reference means moving ~65 000 parts **with geometry** out (megabytes) and
tens of thousands of GUID→value pairs back in. These three tools stream through a **file on disk**
instead; the response carries counters only.

| Tool | Description |
|---|---|
| `tekla_export_parts_file` | Stream matching objects to a jsonl/csv file: identity, bill-of-materials fields, and on request `solidAabb`, `startPoint`/`endPoint`, `contourPoints`, `coordSystem`, `cog`, `finish` and any `uda:NAME`. The usual filters plus `udaIsEmpty`; physical parts by default. Pages via `maxObjects` + `cursor` + `append`; `maxSeconds` stops a slow page between objects. |
| `tekla_set_udas_from_file` | Apply UDA values to many objects from a GUID-keyed jsonl/csv file. `apply=false` by default; `overwriteNonEmpty=false` by default, so values a human already set are left alone (`skippedNonEmpty`). Pages via `maxObjects` + `cursor`. |
| `tekla_export_reference_objects_file` | Same, for reference-model (IFC) objects: external GUID, entity, names, world AABB and placement, with `entityFilter` and per-row `aabbSource`/`placementSource` honesty. Same paging, including `maxSeconds`. |

> ⚠️ **`includeFaceAabb` is off by default.** Exact AABBs for reference objects come from an
> internal, version-sensitive Tekla call. Running it across an IFC overlay on Tekla 2021 (model
> 3155, 2026-08-13) left **Tekla itself** unresponsive with a core pegged, and killing the MCP
> client did not release it — the work had already been handed to Tekla. `maxObjects`/`maxSeconds`
> stop a scan *between* objects; they cannot interrupt a call that never returns. Probe an untried
> reference model with `maxObjects=5` before enabling it. The default path uses IFC-file placement
> estimates, which each row labels via `aabbSource`.

**Where files may go.** Paths must be absolute, carry a data extension (`.jsonl`, `.csv`, `.json`,
`.txt`), and sit under an allowed root. Roots come from `TEKLA_MCP_FILE_ROOT` (`;`-separated); when
it is unset the defaults are `%LOCALAPPDATA%\TeklaMcp\exchange` **plus the open model's folder**.
Setting the variable replaces the defaults, so the allow-list can be narrowed. UNC/device paths,
wildcards, reserved device names and `..` escapes are rejected. The script escape hatch has no file
access at all. The only other tool that writes a file an agent names is `tekla_export_drawings_pdf`:
it prints through Tekla to an explicit absolute `outputFile`, preview-by-default and never
overwriting unless `overwrite=true` — that path is not checked against this allow-list.

**Timeouts.** MCP clients abort a request after ~60 s and the reply is lost even when the server
finished, so pass `maxObjects` (~30 000 is a safe page with `solidAabb`) and, while `truncated` is
true, repeat with `cursor=nextCursor` and `append=true`. Every call also mirrors its counters into
`<path>.status.json`, so a timed-out call can still be verified.

Typical reconciliation loop:

```text
tekla_export_reference_objects_file  → KMD geometry to a file
tekla_export_parts_file              → KM parts (e.g. udaName=USER_FIELD_1, udaIsEmpty=true) to a file
        …match the two files offline…
tekla_set_udas_from_file apply=false → check matched/updated/skippedNonEmpty/notFound
tekla_set_udas_from_file apply=true  → write
```

### Write tools — create / edit / delete

> ⚠️ **Mutating tools.** Every write tool runs in **preview mode by default** (`apply=false`): it returns a plan (counts + preview) and changes nothing. Pass `apply=true` to commit. Created/modified objects are tagged with the `MCP_ORIGIN` UDA so they can be found and reverted; Tekla's native **Ctrl+Z** also undoes committed changes. Coordinates are **global** model coordinates (mm).

**Every write result says what happened and where** — model, UDA, file-based UDA, drawing and
save/open writes alike:

- `outcome`: `planned` (preview), `not_written`, `committed`, `partial` (committed; some items were
  refused, see `errors`) or `unknown`. The Open API has no transactions, so a failure after writing
  began — a lost connection, a failed commit — is `unknown`: a tool error that carries the full
  result (GUIDs, counters) and says to read the objects back before retrying. A model or drawing
  write that fails entirely under `apply=true` is a tool error too, not a quiet `createdCount=0`.
- `target`: the model name and path the write went to, plus the Tekla PID and start time when the
  instance can be told apart.
- `expectedModelPath` (optional, on every write tool and on `tekla_run_csharp`): the model folder
  (or its `.db1`) the write is meant for — e.g. the `modelPath` from `tekla_get_connection_info`. If
  the connected Tekla has another model open, the call is refused before anything is written.
  Recommended whenever several Tekla instances or models are open.
- Modified parts and new connections are read back from the model after the commit. Tekla
  canonicalizes `Position` on commit (`TOP` + 180° reads back as `BELOW` + 0), so a result may name a
  rotation differently than it was requested — the physical orientation is what was asked for.

Axis labels for the grid-based tools come from `tekla_list_grids` / `tekla_resolve_point` (read-only, listed with the model tools above).

| Tool | Description |
|---|---|
| `tekla_create_beam` | Create a beam between two points, with optional Plane/Rotation/Depth or Position copied from an exemplar. |
| `tekla_create_beams` | Create up to 200 beams in one structured batch. |
| `tekla_create_column` | Create a vertical column at (x,y) from bottomZ to topZ. |
| `tekla_create_plate` | Create a contour plate from 3+ points. |
| `tekla_modify_part` | Edit properties/endpoints/Position, or copy Position from an exemplar. |
| `tekla_modify_parts` | Batch form of the above — up to 200 parts in one call (whole-axis re-orientation); can also swap handles per item. |
| `tekla_create_connection` | Create a system/custom Connection (primary, secondaries, UpVector, attributes file); `replaceExisting` swaps an occupied pair. |
| `tekla_create_component` | Insert a plugin / custom / system component with an ordered input list (objects, points, point pairs, polygons) and typed attributes. After apply each component is read back with its children — `childCount=0` (the plugin created nothing) is an error, not success. |
| `tekla_copy_connection` | Copy a Connection's exact name/number/orientation to new parts; `replaceExisting` swaps the node type. |
| `tekla_modify_connections` | Re-orient existing connections (UpVector / auto-direction / attributes file) in bulk without deleting them. An explicit `upVector` switches the component to `AUTODIR_NA` unless a mode is named — under `BASIC` Tekla silently discards the vector. |
| `tekla_swap_handles` | Swap start/end handles of matching parts (capped by `limit`). |
| `tekla_delete_objects` | Delete objects by filter or GUID list (capped by `limit`, default 200). |
| `tekla_save_model` | Save the open model with a save-history comment (preview says whether there are unsaved changes). |
| `tekla_open_model` | Switch Tekla to another model folder. Tekla discards unsaved changes on open, so the call is refused while there are any unless `discardUnsavedChanges=true` (only after the user agreed). `expectedModelPath` names the model being closed. Reports the model actually open afterwards. |
| `tekla_create_beam_between_grids` | Create a beam between two grid intersections at an elevation. |
| `tekla_create_column_grid` | Create columns at every X×Y coordinate intersection. |
| `tekla_generate_frame` | Generate a full bayed frame (columns + per-story beams). |
| `tekla_straighten_columns` | Re-plumb crooked columns (align top over bottom). |
| `tekla_fix_column_handles` | Re-orient flipped columns (swap inverted handles). |

### Drawing tools

> **Experimental.** The drawing layer is new in v0.7.0 and has had far less field testing than
> the model tools. Expect rough edges: individual tools may fail on object types or drawing
> states that have not been exercised yet (Tekla's Drawing API surfaces version-specific
> remoting/serialization quirks). Preview-by-default still protects your drawings, but keep
> Ctrl+Z handy and please report failures — `tekla_report_gap` produces a ready-to-file issue.

Drawing tools use Tekla's separate Drawing API and its editor state. Drawing-list queries work
with the editor open or closed. View/object inspection and content editing require an **active
drawing**; creating drawings, updating them from the model, deleting them, and PDF export have
stricter closed-editor/closed-drawing preconditions noted below.

Drawing, view, and object addresses should be obtained from the corresponding list tool.
`DrawingInternal` ID/ID2 values are exposed when Tekla provides them; they are a best-effort,
version-sensitive API. Drawing keys fall back to a composite of public properties when IDs are
unavailable. View/object indices are explicitly ephemeral — re-list after insert/delete or other
structural edits, and prefer non-zero `ID:ID2` pairs.

Persistent drawing changes are preview-by-default, and their results carry the same
`outcome`/`target` fields and accept `expectedModelPath` like the model write tools (drawing
writes are tracked more coarsely: a lost connection or failed commit reads as `unknown`). The
backend attempts to stamp `MCP_ORIGIN`, but drawing-side UDA support varies by object and
environment; do not rely on the tag as the only rollback mechanism. Pay particular attention to
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
| `tekla_export_drawings_pdf` | Print matched closed drawings to PDF with color/orientation/paper/scale options; `outputFile` is an explicit absolute path (`{mark}`/`{name}` keep several files apart), existing files are kept unless `overwrite=true`. |

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
report recurring gaps. A drawing `Part` has no geometry of its own in the Drawing API: for the
exact arcs of a curved part call `tekla_get_part_curve_geometry` with the object's `modelGuid`
(plus `viewId` to get the points in that view) and feed them to the radius/curved dimension
tools.

### C# scripting escape hatch

The Tekla Open API is far larger than this tool set. When no dedicated tool covers a need, an agent can run a short, **policy-checked C# script** against the live model — after verifying the API signatures offline:

| Tool | Description |
|---|---|
| `tekla_search_api` | Keyword search over the locally generated Tekla Open API reference (types + member signatures). |
| `tekla_get_api_doc` | Full reference page for one type: every constructor/property/method signature with summaries. |
| `tekla_get_api_reference_status` | Report whether the local offline reference is ready, where it comes from (generated from the installed Tekla / configured / repository copy) or how to set it up; the first call for a Tekla build starts generating it. |
| `tekla_check_csharp` | Policy-check and compile the exact source without executing it; returns SHA-256 + detected mutations for approval. |
| `tekla_run_csharp` | Run a C# script (top-level statements, Tekla namespaces pre-imported, `Print(...)` for output, last expression = JSON return value). Mutating runs need `allowMutations=true` plus `expectedSha256` from the check. |

Safety model:

- **Read-only by default, writes by consent.** Mutating members (`Insert`/`Modify`/`Delete`/`CommitChanges`/`SetUserProperty`/`Operation.*`, catalog imports, `ModelHandler.Open`/`Close`) are rejected unless the call sets `allowMutations=true` — and the tool contract obliges the agent to show you the script and get your explicit go-ahead before setting it. Scripted changes should be tagged with the `MCP_ORIGIN` UDA where practical; committed changes are undoable with Tekla's **Ctrl+Z**.
- **No host access.** A syntax-level policy bans file system, network, processes, reflection, threads and `Console` (stdout belongs to the MCP protocol). `#r`/`#load` and `await` are rejected too.
- **Execution deadline** (default 60 s, max 600 s) on a dedicated thread. Abort is best-effort
  around Tekla remoting; the result warns when worker termination cannot be confirmed. Most MCP
  clients abort a request after ~60 s anyway, so big scans should be chunked rather than given a
  longer deadline.
- **Compile before consent, run only what was approved.** `tekla_check_csharp` never connects to or executes against the model, so a proposed mutation can be verified before approval; its `codeSha256` identifies the exact reviewed source. `tekla_run_csharp` with `allowMutations=true` **requires** that hash as `expectedSha256` and refuses, before compiling, a script whose source no longer matches it (new in 0.8.0 — clients that ran mutating scripts without the check step must add it). The live backend compiles against every installed managed `Tekla.Structures*.dll` (including Drawing/Dialog when present). Drawing is not a global import because its `Part`/`View` types are ambiguous — use `using TSD = Tekla.Structures.Drawing;`.
- **Right model.** `expectedModelPath` refuses the run before anything is compiled when the connected Tekla has another model open; the result reports the `target` model and Tekla process. A script's own writes cannot be observed, so there is no `outcome` — `executed`/`executionAttempted` are the signal.
- **Bounded output.** `Print` uses private host-owned storage capped at 500 lines / 64,000 characters; the return value is rendered inside the timeout worker as capped, always-valid JSON (6 nesting levels, 100 items per list, 4,000 characters per string). Any cap that fires sets `returnValueTruncated` (+ `returnValueTruncation`), so a cut result is never mistaken for the whole.
- On the **mock backend** scripts are validated and compiled but never executed (compilation needs Tekla DLLs — point `TEKLA_MCP_SCRIPT_REF_DIR` at a folder with `Tekla.Structures*.dll`, e.g. extracted from the NuGet packages).

This is a pragmatic barrier for well-behaved agents, not a security sandbox — the script runs with the server's privileges. Prefer the dedicated write tools (preview-by-default) for changes; the scripted-write path is for cases they don't cover.

`tekla_search_api` reads an offline Markdown reference of the Tekla Open API. **The server generates it by itself** from the installed Tekla's assemblies and the XML docs Tekla ships next to them — on first use, in the background (~7 s on Tekla 2021), cached per Tekla build under `%LOCALAPPDATA%\TeklaMcp\api-reference`; release installs need no manual step. While it is being generated, `tekla_get_api_reference_status` reports `generating=true` and the search asks to call again shortly. Nothing from Trimble is bundled or uploaded. `TEKLA_MCP_API_REF_DIR` still overrides it, and [tools/TeklaApiDoc](tools/TeklaApiDoc/README.md) produces the same output for a repository copy (`reference/tekla-api`) or the mock (`TEKLA_MCP_SCRIPT_REF_DIR` also works there).

### Reporting gaps

The server ships MCP **instructions** giving connecting agents an escalation ladder: use the dedicated tools first; bridge one-off needs with the scripting escape hatch (never external ad-hoc automation, never fabricated data); and call `tekla_report_gap` for anything missing or recurring. `tekla_report_gap` returns a ready-to-file GitHub issue draft (and logs the request to `%LOCALAPPDATA%\TeklaMcp\capability-requests.log`) for you to file — recurring scripts are exactly the signal that a first-class tool should be built. The server never creates issues itself (it holds no GitHub credentials).

The instructions also list the known model-layer quirks agents would otherwise trip over (connection up-vectors only stored under auto-direction NA, `Position` canonicalized on commit, one connection per part pair, UI-vs-API component names, custom components that fail after loading an attributes file) — details in [docs/tekla-api-notes.md](docs/tekla-api-notes.md#known-model-layer-quirks-verified-live).

### Shared filter parameters

Most query and analytics tools accept:

- `type` — exact object type (`Beam`, `ContourPlate`, `Bolt`, …)
- `class` — exact class
- `profile` — profile substring
- `material` — material substring
- `nameContains` — name substring
- `udaName` + `udaEquals` — exact UDA match
- `udaName` + `udaIsEmpty=true` — objects whose UDA is unset or blank (`tekla_find_objects`, `tekla_count_objects`, `tekla_export_parts_file`, `tekla_render_schematic`)
- `attributeName` + `attributeEquals` / `attributeContains` — exact/substring match for any known attribute
- `guidIn` — explicit GUID allow-list (`tekla_select_objects`, `tekla_find_connections`, `tekla_swap_handles`, `tekla_delete_objects`)
- `useSelection` — scope the tool to the **current Tekla UI selection** instead of the whole model (faster; enables "analyze what I selected" workflows)

Whole-model scans also take:

- `partsOnly` — scan physical parts only (default `true`, much faster on large models); `false` adds bolts, welds, assemblies (`tekla_sum_weight`, `tekla_group_weight_by`, `tekla_list_distinct_values`, `tekla_find_attributes_by_value`, `tekla_discover_udas`, `tekla_export_parts_file`)
- `maxObjects` + `cursor` — page a scan so each call fits inside the MCP client's ~60 s request timeout: while the result says `truncated=true`, repeat with `cursor=nextCursor` and otherwise identical arguments, then add up (or merge by key) the pages (`tekla_sum_weight`, `tekla_group_weight_by`, `tekla_list_distinct_values` and the file-exchange tools)

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
- “Group all parts by `USER_FIELD_1` and show the weight per approval status (`groupBy: 'uda:USER_FIELD_1'`).”
- “Export every part whose `USER_FIELD_1` is still empty, with `solidAabb`, to a jsonl file.”
- “Show me what is attached along axis Д besides the usual node, and capture the view.”
- “Give me the radius, chord and arc length of this curved tube.”
- “Show all assembly drawings that are issued but modified, without opening them.”
- “Preview updating the drawings selected in the Drawing List; do not apply.”
- “Open drawing `A-12`, list its views, and show the represented model GUIDs.”
- “In the active view, preview a straight dimension through these global model points.”
- “Create a red revision cloud and note on the active drawing, preview first.”
- “Preview exporting the selected closed drawings to A3 black-and-white PDFs.”

### UDA write safety

`tekla_set_object_udas`, `tekla_set_udas_by_filter` and `tekla_set_udas_from_file` default to **preview mode** (`apply=false`). Pass `apply=true` to commit changes. Filter-based bulk writes are capped by `limit` (default 200 objects); file-based writes page via `maxObjects` + `cursor` and leave non-empty values alone unless `overwriteNonEmpty=true`. Like every write tool they report `outcome`/`target` and accept `expectedModelPath`.

---

## Requirements

**Production (live Tekla model, release zip):**

- Windows x64
- Tekla Structures 2021–2026 installed, **running with a model open** (2024–2026 untested, see [Known limitations](#known-limitations))
- .NET Framework 4.8 runtime (already present wherever Tekla runs)

**Building the live backend from source** additionally needs:

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
4. Ask the assistant to call `tekla_get_connection_info`: it shows the open model, the Tekla version the zip was built for, and where the Tekla assemblies were loaded from.

No further setup is needed — the offline API reference used by the scripting tools is generated from your Tekla on first use. To try the server without Tekla, the `TeklaMcp.Server-X.Y.Z-net8.0-mock.zip` runs the mock backend (needs the .NET 8 runtime; `dotnet TeklaMcp.Server.dll`).

> **Why per-version zips?** The Tekla Open API protocol is version-locked, so the server must be compiled for the Tekla it talks to. The zip does **not** bundle Tekla DLLs — it loads them from your installed Tekla at runtime, verifying the version matches. A mismatched zip fails with a clear message naming the right one. If auto-detection of the Tekla Open API folder fails, set `TEKLA_BIN_DIR` (see [Configuration](#configuration)). See [docs/tekla-api-notes.md](docs/tekla-api-notes.md#tekla-version-compatibility).
>
> **If Tekla is open but the server says "Not connected"** (Tekla 2021–2023): the error message lists the client channel and the `Tekla.Structures.Model-*` named pipes Tekla actually publishes. The server auto-matches the published channel (some setups publish `…-Console:<version>` instead of the default `…-:<version>`). To force a specific channel, set the `TEKLA_MCP_CHANNEL` environment variable to the exact pipe name.
>
> **Starting the MCP client before Tekla is fine** (Tekla 2021–2023): until Tekla publishes its channel, tools answer "not reachable … nothing was sent to Tekla" and work as soon as the model is open — no server restart. **Restarting Tekla is fine too** (2021–2023; accepted live on Tekla 2023 with three restarts, 2021 still to be run): the next tool call notices the dead connection and reconnects on the same channel (`tekla_get_connection_info` shows `lastReconnect`). Nothing is retried, so a write is never repeated. The call that reconnects while Tekla is still opening the model waits for it (up to ~70 s measured), so a client may time out on that one call — the next one works. Only a Tekla that comes back under another Windows session or instance name (e.g. a second instance) needs a server restart, and so does Tekla 2024+ for now.
>
> **The server exits with its MCP client** (Windows) — when the process that launched it is gone, or when a shutdown hangs for 10 s (a Tekla call cannot be cancelled). If your launcher exits while keeping the server's stdio open, set `TEKLA_MCP_EXIT_WITH_PARENT=0`.
>
> **Tekla 2024–2026 users: testing and issues are very welcome.** The 2024+ zips are built and compiled against the official Open API packages, but the maintainers have no 2024+ install to run them on. Those versions talk to the server over a different transport (Trimble.Remoting instead of named pipes), so the connection guard and the automatic reconnect are not active there yet, and everything connection-related is untested. If you work with 2024, 2025 or 2026, please [open an issue](https://github.com/tempalg1n/tekla-mcp-server/issues) with what worked and what did not — the `tekla_get_connection_info` output and the server's stderr log help most. Agents can draft the report for you with `tekla_report_gap`.

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
Pass `-TeklaVersion` with the NuGet version of your Tekla (table in
[docs/releasing.md](docs/releasing.md)); without it the script builds the default `2021.0.0`, which
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

### Configuration

All settings are optional environment variables, set in the client's `env` block or the system
environment.

| Variable | Default | Meaning |
|---|---|---|
| `TEKLA_BIN_DIR` | auto: the running Tekla process, then the registry | Folder holding the Tekla Open API (`Tekla.Structures.Model.dll` — `nt\bin\plugins` on 2021, `bin` on 2023); its parent or the install root work too. A folder without the Model DLL is ignored with a warning. |
| `TEKLA_MCP_CHANNEL` | auto-matched to the pipes Tekla publishes (2021–2023); Tekla's own names on 2024+ | Exact Model remoting channel (pipe) name to force when auto-matching picks the wrong one; the base and Drawing channels follow. |
| `TEKLA_MCP_FILE_ROOT` | `%LOCALAPPDATA%\TeklaMcp\exchange` + the open model's folder | `;`-separated roots the file-exchange tools may read and write. Setting it replaces the defaults. |
| `TEKLA_MCP_EXIT_WITH_PARENT` | on (Windows) | `0` stops the server from exiting when the process that launched it exits. |
| `TEKLA_MCP_API_REF_DIR` | generated on first use, cached in `%LOCALAPPDATA%\TeklaMcp\api-reference` | Folder with a pre-generated Open API reference (`*.md`); wins over the cache. |
| `TEKLA_MCP_SCRIPT_REF_DIR` | unset | Mock backend only: folder with `Tekla.Structures*.dll` so `tekla_check_csharp` can compile and the API reference can be generated without Tekla. |
| `TEKLA_MCP_USE_MOCK` | unset | `1` forces the mock backend in the Windows (`net48`) build. |
| `TEKLA_MCP_ISSUES_URL` | this repository's new-issue page | Where `tekla_report_gap` drafts point, e.g. an internal tracker. |

---

## Known limitations

- **Tekla 2024, 2025 and 2026 are built but untested — testers and issue reports wanted.** The
  maintainers have no 2024+ install. Those versions use Trimble.Remoting instead of named pipes, so
  the connection guard and the reconnect after a Tekla restart are not active there (a lost
  connection still needs a server restart), and the channel handling is based on decompiled
  assemblies only. If you run 2024+, please
  [open an issue](https://github.com/tempalg1n/tekla-mcp-server/issues) with the
  `tekla_get_connection_info` output and the server's stderr — `tekla_report_gap` drafts one.
- **Several running Tekla instances of one version** cannot be chosen between yet: the server binds
  to one and never switches to another by itself. Pass `expectedModelPath` to writes so a wrong
  model refuses them.
- **Not yet run on live Tekla** with this release: the reconnect on Tekla 2021, the `unknown` write
  outcome, `tekla_get_part_solid`, `tekla_save_model` / `tekla_open_model`,
  `tekla_create_component` with a real plugin, and UDA definitions with `details=true`. What is
  live-verified is stated per entry in [CHANGELOG.md](CHANGELOG.md).
- **The drawing layer is experimental** (new in v0.7.0, limited field testing) — see
  [Drawing tools](#drawing-tools).
- **`tekla_render_schematic` is beta**: simplified geometry that can look cluttered on dense models.
- **Whole-model scans are bounded by the client's ~60 s request timeout**, not by the server — page
  them with `maxObjects` + `cursor`. Even an unfiltered `tekla_count_objects` took ~20 s on one live
  Tekla 2023 model (first measurement; see [docs/backlog.md](docs/backlog.md)).

## Roadmap

- [ ] Explicit Tekla instance selection — analysed, deferred; see [docs/backlog.md](docs/backlog.md)
- [ ] Connection guard and reconnect for Tekla 2024+ (Trimble.Remoting) — **needs testers with a
  2024+ install**; see [docs/backlog.md](docs/backlog.md)
- [ ] Live runs of the 0.8.0 items listed above, and field testing of the drawing layer
- [ ] Measure and tune whole-model scan cost on 400k+-object models (enumerator snapshot, paging)
- [ ] Broader object coverage: bolt, weld and rebar details

Contributions welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).

Release history: [CHANGELOG.md](CHANGELOG.md).

---

## Credits

This project was **designed and developed with [Claude Opus 4.8](https://www.anthropic.com/claude)** (Anthropic), using AI-assisted architecture, implementation, and documentation.

Tekla Structures is a product of [Trimble](https://www.tekla.com/). This project is not affiliated with or endorsed by Trimble.

---

## License

[MIT](LICENSE) — see the LICENSE file for details.
