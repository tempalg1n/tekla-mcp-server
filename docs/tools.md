# Tool reference

Every tool of Tekla MCP Server, grouped by area. Overview and quick start: [README](../README.md);
installation and configuration: [setup.md](setup.md).

All tools use the `tekla_` prefix. The current build exposes **120 tools** in total, including
**54 drawing-specific tools**.

Every tool declares MCP **tool annotations**, so clients such as Claude Desktop or Codex can
approve reads without asking and ask before writes: 50 tools are `readOnlyHint=true`; the other 70
also say whether they are `destructiveHint` (can modify or delete what is there) and
`idempotentHint`. The hints describe what a tool can do with `apply=true`, not its preview
default. Selection (`tekla_select_objects`, `tekla_select_drawing_objects`), `tekla_capture_view`
and `tekla_open_drawing` change only the Tekla UI and are marked as non-destructive writers; the
file exports, `tekla_export_drawings_pdf` and `tekla_report_gap` write files and are not
read-only either. An operator can also start the server **read-only** or with fewer toolsets — see
[Read-only mode and toolsets](setup.md#read-only-mode-and-toolsets).

**Contents:** [Model tools](#model-tools) ·
[Seeing the model](#seeing-the-model--pictures-for-agents) ·
[File exchange](#file-exchange--bulk-data-in-and-out) ·
[Write tools](#write-tools--create--edit--delete) · [Drawing tools](#drawing-tools) ·
[C# scripting](#c-scripting-escape-hatch) · [Reporting gaps](#reporting-gaps) ·
[Shared filter parameters](#shared-filter-parameters) · [Example prompts](#example-prompts) ·
[UDA write safety](#uda-write-safety)

## Model tools

Read, analyze, select and UDA tools for the open model.

| Tool | Description |
|---|---|
| `tekla_get_connection_info` | Check whether a model is open; return name, path, active backend, and which server answered (version, PID, start time, Tekla build/bin folder, running Tekla processes, `lastReconnect`). A lost connection is explained with cause and action instead of a raw remoting error. |
| `tekla_report_gap` | Report a missing capability / insufficient data; returns a ready-to-file issue draft. Use instead of scripting around gaps. |
| `tekla_get_model_summary` | Model-wide summary: object count, total weight, breakdowns by type/class/profile/material. On huge models use `includeWeights=false` / `maxObjects` to keep it fast. |
| `tekla_get_numbering_status` | Read-only: is numbering of the whole model up to date (`IsNumberingUpToDateAll`), plus the status and current marks of up to 200 parts/assemblies by GUID. Never numbers or saves. |
| `tekla_list_objects` | List objects with core properties (with limit; `useSelection` for the UI selection). |
| `tekla_find_objects` | Search by filters: type, class, profile, material, name, UDA/attribute. `udaIsEmpty=true` selects objects whose UDA is still blank. |
| `tekla_get_object_by_guid` | Fetch a single object by GUID. |
| `tekla_get_properties` | Read any named properties (report props, UDAs, built-ins) for an object by GUID; names that resolve to nothing are listed in `notFound`. |
| `tekla_get_selected_objects` | Return objects currently selected in the Tekla UI. |
| `tekla_get_reference_geometry` | Inspect IFC/reference objects by selection, integer id or IFC GlobalId: external GUID/entity, dimensions, world AABB, placement and capped face polygons (falls back to parsing the IFC file, labelled via `aabbSource`/`placementSource`). |
| `tekla_get_solid_bbox` | Read explicit native-part solid bounding boxes (or current selection). |
| `tekla_get_part_solid` | The real shape of up to 20 parts: faces with outward normals and the part that produced them (cuts), outer/inner loops, vertices — GLOBAL mm, cuts and fittings applied (or `RAW`). Capped (`maxFaces`/`maxPoints`) with an explicit `truncated` flag; faces are never cut in half. |
| `tekla_get_part_curve_geometry` | Exact centerline of one part for dimensioning curved members: a PolyBeam's own lines and arcs, or the arc of a curved Beam (one with a `RADIUS`) (center, start/mid/end, radius, sweep, arc length, chord, sagitta), arcs merged per physical bend, inner/outer arcs of round tubes and bars, the modelled contour with chamfers, and optionally every point in an active-drawing view. |
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

## Seeing the model — pictures for agents

Agents otherwise reason about a model only through property lists and coordinates. These two tools
answer with an **MCP image block** — the agent actually sees the picture — plus a JSON block that
maps what is on it back to GUIDs and model millimetres. One picture costs about 1–1.6k tokens.
For drawings, `tekla_capture_drawing` (in the drawing tables below) does the same for the open
drawing.

| Tool | Description |
|---|---|
| `tekla_capture_view` | Screenshot of a **live Tekla model view**: exactly what Tekla renders (profiles, bolts, welds, cuts, reference models, class colours). Default: the active view as it is, no side effects. With `guids`/`useSelection` it zooms to the targets, colours them red and ghosts everything else; `labels=true` numbers them; `direction` rotates a 3D view. `restore=true` (default) puts camera and colours back right after the capture. Windows + live Tekla; the mock returns a labelled schematic stand-in. |
| `tekla_render_schematic` | **Beta.** Numbered schematic drawn by the server itself — plan, elevations or iso — with focus objects (GUIDs, selection or the usual filters) in colour, context parts in grey, grid axes with their real labels, levels, coordinate ticks and a legend (number → GUID, pixel → mm). No Tekla UI involved; works on the mock too. Geometry is simplified (reference lines, contours, bounding boxes) and dense models can look cluttered — narrow the scope with `region` or filters. |

> **What the capture can see.** Only a view that is visible on screen: the active view, or views
> tiled side by side. A view hidden behind another view, or a minimized Tekla, is refused with the
> list of open views. Pixels come from Tekla's own view window (`PrintWindow`), never from a screen
> copy, so no other application's window can end up in the picture.

## File exchange — bulk data in and out

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

> ⚠️ **PDF export writes wherever you let it.** `tekla_export_drawings_pdf` can write to any folder
> your Windows account can write to, network shares included, and with `overwrite=true` it replaces
> an existing file there whatever its type. This is by design: where drawings are printed is the
> user's decision, and the server does not second-guess it. Check the `outputFiles` in the preview
> before approving `apply=true`, and never approve `overwrite=true` for files you have not looked
> at. The tool description asks agents to do the same.

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

## Write tools — create / edit / delete

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
| `tekla_modify_parts` | Batch form of the above — up to 200 parts in one call (whole-axis re-orientation); can also swap handles per item. After apply every written part is read back and compared with the request (`verification`: verified / mismatch / incomplete). |
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

## Drawing tools

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

**`committed` is not saved.** A write to the open drawing is committed to the editor, not saved
(`drawingSaved=false`): save with `tekla_save_drawing` after each milestone — Tekla drops unsaved
drawing edits when the model is reopened. Model and view IDs can change across a reopen of a
multi-user model; keep GUIDs.

### Drawing discovery and editor selection

| Tool | Description |
|---|---|
| `tekla_get_drawing_status` | Check Drawing API connectivity and active-editor state. |
| `tekla_get_active_drawing` | Return the currently open drawing, or `null` when the editor is closed. Drawing status counts the MCP writes not yet saved. |
| `tekla_list_drawings` | Search drawing-list rows by key/type/mark/name/title/model GUID/status/flags/selection. |
| `tekla_get_selected_drawings` | Return rows selected in Tekla's Drawing List dialog. |
| `tekla_get_drawing_summary` | Aggregate counts by type/status and issued/locked/ready/stale state, with explicit scan truncation. |
| `tekla_find_drawing_issues` | QA heuristics for stale, issued-but-modified, ready-but-stale, and missing mark/name rows. |
| `tekla_get_drawing_model_objects` | Return a bounded/paginated page of full model identifiers represented by a drawing. |
| `tekla_get_drawing_sheet` | Read active-sheet width, height, origin and configured layout size/mode in paper millimetres. |
| `tekla_list_drawing_views` | List active-drawing views with best-effort ID/ID2, paper frame (`frameMin`/`frameMax`), scale, restriction box, coordinate systems and label text. |
| `tekla_list_drawing_objects` | Filter active-drawing objects by ID/index/type/view/model GUID/text/selection, with optional geometry and UDAs. Marks come with their content and the marked model object; `emptyMarksOnly` finds empty marks. |
| `tekla_get_selected_drawing_objects` | Return the current drawing-editor selection. |
| `tekla_select_drawing_objects` | Select/highlight matching active-drawing objects (an immediate UI side effect, not a model mutation). |
| `tekla_capture_drawing` | **Picture of the open drawing** as Tekla draws it — whole sheet, one view's frame (`viewId`) or a sheet region — returned as an image plus JSON (view frames and pixel boxes, pixel → sheet mm). Read-only: nothing is zoomed or selected. Use it to check dimensions, marks and labels after a drawing edit. See the note below. |

> **What the drawing capture can see.** The drawing editor as it is on screen: pixels come from Tekla's
> own drawing window (`PrintWindow`), never from a screen copy. The sheet is located from its edges on
> that picture (the Open API cannot read the editor's zoom), so a view or region can be cut out only
> while the sheet's edges are on screen — a freshly opened drawing qualifies. Zoomed in past them,
> the tool returns the whole window with `scope="visible"` and no coordinates. The picture is never
> sharper than the screen (a whole A2 sheet is ~3 px per mm): capture one view to read small text.
> Live-verified on Tekla 2021; other versions are untested.

### Drawing lifecycle and output

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

### Views and drawing content

These tools operate on the **active drawing** and are also preview-by-default. Saved attribute
file names are passed to Tekla and resolved by the running environment.

| Tool | Description |
|---|---|
| `tekla_create_drawing_view` | Create a front/top/back/bottom/3D view on a non-GA drawing. |
| `tekla_create_ga_drawing_view` | Create a GA model view from explicit global view/display coordinate systems and a restriction box; `placeBy=frame_lower_left/frame_center` places the view frame instead of the model origin. |
| `tekla_create_section_view` | Create a straight or curved section view and section mark from a source view; loads the attribute file after creation, both depths must be > 0. |
| `tekla_create_detail_view` | Create a detail view and detail mark from a source view. |
| `tekla_modify_drawing_view` | Edit a view's name, sheet origin/frame, scale, and rotations, or load a saved view attributes file (filters) keeping scale, restriction box and origin. |
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
| `tekla_create_radius_dimension` | Create a radius dimension from start, a point between and end (either direction; ordered for Tekla). |
| `tekla_create_curved_dimension` | Create a radial or orthogonal curved dimension set. |
| `tekla_create_drawing_mark` | Create a mark for a model object represented in a target view, optionally with a leader start, placing, own text or content copied from another mark. |
| `tekla_create_detail_mark` | Create a standalone detail mark (callout circle + label) without a detail view, e.g. to reference a node on another sheet. |
| `tekla_create_section_mark` | Create a standalone section mark without a section view. |
| `tekla_create_level_mark` | Create a level mark. |
| `tekla_create_drawing_symbol` | Create a symbol from a Tekla `.sym` library. |
| `tekla_modify_drawing_objects` | Batch-change text, relative position, visibility, or loaded attributes; on marks also insertion point, leader start and content. |
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

## C# scripting escape hatch

The Tekla Open API is far larger than this tool set. When no dedicated tool covers a need, an agent can run a short, **policy-checked C# script** against the live model — after verifying the API signatures offline:

| Tool | Description |
|---|---|
| `tekla_search_api` | Keyword search over the locally generated Tekla Open API reference (types + member signatures). |
| `tekla_get_api_doc` | Full reference page for one type: every constructor/property/method signature with summaries. |
| `tekla_get_api_reference_status` | Report whether the local offline reference is ready, where it comes from (generated from the installed Tekla / configured / repository copy) or how to set it up; the first call for a Tekla build starts generating it. |
| `tekla_check_csharp` | Policy-check and compile the exact source without executing it; returns SHA-256 + detected mutations for approval, and a `checkId` to run it by. |
| `tekla_run_csharp` | Run a C# script (top-level statements, Tekla namespaces pre-imported, `Print(...)` for output, last expression = JSON return value). Mutating runs need `allowMutations=true` plus `expectedSha256` from the check; `checkId` runs the checked text without sending it again. |

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

`tekla_search_api` reads an offline Markdown reference of the Tekla Open API. **The server generates it by itself** from the installed Tekla's assemblies and the XML docs Tekla ships next to them — on first use, in the background (~7 s on Tekla 2021), cached per Tekla build under `%LOCALAPPDATA%\TeklaMcp\api-reference`; release installs need no manual step. While it is being generated, `tekla_get_api_reference_status` reports `generating=true` and the search asks to call again shortly. Nothing from Trimble is bundled or uploaded. `TEKLA_MCP_API_REF_DIR` still overrides it, and [tools/TeklaApiDoc](../tools/TeklaApiDoc/README.md) produces the same output for a repository copy (`reference/tekla-api`) or the mock (`TEKLA_MCP_SCRIPT_REF_DIR` also works there).

## Reporting gaps

The server ships MCP **instructions** giving connecting agents an escalation ladder: use the dedicated tools first; bridge one-off needs with the scripting escape hatch (never external ad-hoc automation, never fabricated data); and call `tekla_report_gap` for anything missing or recurring. `tekla_report_gap` returns a ready-to-file GitHub issue draft (and logs the request to `%LOCALAPPDATA%\TeklaMcp\capability-requests.log`) for you to file — recurring scripts are exactly the signal that a first-class tool should be built. The server never creates issues itself (it holds no GitHub credentials).

The instructions also list the known model-layer quirks agents would otherwise trip over (connection up-vectors only stored under auto-direction NA, `Position` canonicalized on commit, one connection per part pair, UI-vs-API component names, custom components that fail after loading an attributes file) — details in [tekla-api-notes.md](tekla-api-notes.md#known-model-layer-quirks-verified-live).

## Shared filter parameters

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

## Example prompts

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

## UDA write safety

`tekla_set_object_udas`, `tekla_set_udas_by_filter` and `tekla_set_udas_from_file` default to **preview mode** (`apply=false`). Pass `apply=true` to commit changes. Filter-based bulk writes are capped by `limit` (default 200 objects); file-based writes page via `maxObjects` + `cursor` and leave non-empty values alone unless `overwriteNonEmpty=true`. Like every write tool they report `outcome`/`target` and accept `expectedModelPath`.
