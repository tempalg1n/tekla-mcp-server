# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Fixes for the two v0.7.0 field reports: `tekla_create_beam` failing on apply with a
`Tekla.Structures.ModuleManager` type-initializer exception, and
`tekla_get_reference_geometry` unable to deliver world geometry for IFC overlay objects.

### Fixed

- **Drawing-object enumeration died on non-serializable objects (e.g. `DetailMark`).**
  After a detail view was created, every `tekla_select_drawing_objects` /
  `tekla_list_drawing_objects` call on that drawing failed with
  `SerializationException: DetailMarkSymbolAttributes … is not marked as serializable` —
  Tekla's remoting channel cannot materialize a `DetailMark`, and one faulting object killed
  the whole enumeration. Drawing-side enumerators now skip objects that fault during
  `MoveNext()` (bounded retries, so a stuck enumerator cannot spin forever). Found live on
  Tekla 2023 while smoke-testing the v0.7.0 drawing layer.
- **`tekla_get_reference_geometry`: duplicate-key crash reading IFC custom attributes.**
  Tekla's own `Operation.GetReferenceModelObjectCustomAttributes` builds a `Dictionary` from
  `"key;value"` remoting rows with `Dictionary.Add`, so an IFC object carrying the same
  attribute name twice (e.g. one property in several psets) threw «Элемент с тем же ключом
  уже был добавлен» and no custom attributes were returned. The backend now replays the same
  internal remoting sequence (confirmed by decompiling Tekla 2023) with duplicate-tolerant
  parsing, falling back to the public wrapper on Tekla versions where the internal surface
  differs. Verified live: 24 attributes (psets, Qto, window style) for the field-report
  window instead of the error.
- **`tekla_get_reference_geometry`: IFC fallback could not read Tekla's `.ifczip` cache.**
  The active revision copy of a reference model (`ReferenceModel.ActiveFilePath`) usually
  points into `DataStorage\ref\<hash>.ifczip` — a zip holding the `.ifc`. The parser read it
  as STEP text, found nothing and reported «Entity with GlobalId … not found». `.ifczip` /
  `.zip` archives (detected by content, not extension) are now unpacked transparently, every
  existing on-disk copy (cache first, then `Filename`, absolute or model-relative) is tried
  until the GlobalId resolves, and files held open by Tekla are shared correctly.
- **`tekla_get_reference_geometry`: placements scaled ×1000 for Renga IFC4 files.** The
  project length unit is the `LENGTHUNIT` referenced from `IFCUNITASSIGNMENT`, but the file
  may declare additional auxiliary length units (Renga: project `MILLI METRE` + bare `METRE`
  later); last-declaration-wins picked the wrong one and scaled every coordinate ×1000. The
  assignment-referenced unit now wins. Verified live against the field-report window
  `0VZkpIecn7$9mG$7iL8u45` in `3219-АР.ifc`: `placementSource: "ifc-file"` origin and the
  `ifc-placement-estimate` AABB match the exact `tekla-faces` AABB.
- **Writes failing with «Инициализатор типа "Tekla.Structures.ModuleManager" выдал
  исключение» while reads work.** Root cause (confirmed by decompiling the Tekla
  assemblies): every Open API channel name is `{Assembly}-{SESSIONNAME}:{version}`, and an
  MCP server launched by an MCP client usually has no `SESSIONNAME` environment variable —
  so the Model channel was aligned (issue #7 fix) but the BASE `Tekla.Structures` channel,
  which every `Insert`/`Modify` touches through `ModuleManager`, was not.
  `TeklaRemotingChannel.Align()` now derives the session suffix from the published pipes,
  sets `SESSIONNAME` for the server process and aligns all three channels (base, Model,
  Drawing). Write-path proxies are additionally warmed up right after the first successful
  connection, when the channels are known-good, instead of lazily inside the first write.
- **Opaque wrapper errors.** «Адресат вызова создал исключение» /
  «Инициализатор типа … выдал исключение» are never reported as-is anymore: every error
  surfaced by the Tekla backend goes through `ErrorText.Flatten`, which unwraps
  `TargetInvocationException` / `TypeInitializationException` / `AggregateException` chains
  and keeps the real cause (e.g. the failing remoting channel name) visible.
- **MCP protocol protection.** The Tekla Open API writes "Connection failed : …" to stdout
  when a remoting channel fails; `Console.Out` is now routed to stderr so a Tekla connection
  failure can no longer corrupt the MCP stdio framing.
- **Total apply-failures are now protocol errors (`isError=true`).** A write tool that was
  asked to commit (`apply=true`) but wrote nothing while reporting per-item errors now
  raises an MCP tool error with the flattened error list, instead of returning a
  normal-looking result with `createdCount: 0`. Previews and partial successes are
  unchanged. (The requested `tekla_create_beams_batch` already exists as
  `tekla_create_beams` — up to 200 beams in one commit.)

### Added

- **IFC placement fallback in `tekla_get_reference_geometry`.** When the Tekla API cannot
  deliver reference-object geometry (the reported case for IFC overlay windows), the server
  now parses the reference IFC file itself: resolves the `IFCLOCALPLACEMENT` chain by IFC
  GlobalId, applies the project length unit and the reference-model insertion
  (Position/Scale/Rotation), and returns world placement `placementOrigin` +
  `placementX/Y/ZAxis` (GLOBAL mm, `placementSource: "ifc-file"`), plus
  `OverallWidth`/`OverallHeight`, entity and name when Tekla did not provide them. If no
  exact face AABB is available, an estimated AABB is derived from placement + overall
  dimensions and labeled `aabbSource: "ifc-placement-estimate"` (exact face AABBs are
  labeled `"tekla-faces"`). Agents no longer need to parse IFC files manually.
- `tekla_get_reference_geometry` accepts `externalGuids` — address reference objects
  directly by IFC GlobalId (e.g. `0VZkpIecn7$9mG$7iL8u45`) via
  `ReferenceModel.GetReferenceModelObjectByExternalGuid` where the Tekla version provides
  it.

## [0.7.0] - 2026-07-23

First-class tools for the three biggest gaps observed in a real end-to-end modeling session:
IFC/reference geometry, beam Position, and custom Connections. The same release adds batched
beam creation, explicit native-geometry helpers, a broad first-class Drawing API surface, and
a hardened `tekla_run_csharp` workflow so routine modeling and drawing work no longer depends
on ad-hoc scripts.

### Added

- `tekla_get_reference_geometry` — selected or integer-ID-addressed
  `ReferenceModelObject` metadata and geometry: external/IFC GUID, entity/object type,
  `OverallWidth`/`OverallHeight`, reference model source, world AABB, capped face polygons and
  capped custom attributes. Reference objects no longer rely on their commonly empty Tekla GUID.
- First-class part Position (`Plane`, `Rotation`, `Depth` and all offsets) in
  `ModelObjectInfo`, `PartSpec` and `PartModification`.
  - `tekla_create_beam` and `tekla_modify_part` accept explicit Position fields.
  - `matchPositionGuid` copies the complete Position from an exemplar before explicit overrides.
- Real connection/component primitives:
  - `tekla_list_connections(partGuid)` reads exact Unicode name/number, primary/secondaries,
    `UpVector`, auto-direction and status.
  - `tekla_create_connection` creates system/custom connections with optional attributes file.
  - `tekla_copy_connection` copies identity/orientation from an existing connection.
  - Component writes commit pending geometry once before resolving GUIDs, avoiding the common
    freshly-created-part insertion race.
- `tekla_create_beams` — structured, capped batch creation (up to 200 beams per call).
- `tekla_get_solid_bbox` — explicit native-part solid bounding-box helper.
- `tekla_list_control_lines` — ControlLine start/end coordinates.
- `tekla_get_api_reference_status` — explicit offline-reference availability/setup diagnostics.
- `tekla_check_csharp` — policy-check and compile the exact escape-hatch source without live
  execution; returns a stable SHA-256 and detected mutating API members so scripted writes can
  be verified before user approval.
- Drawing-list discovery and QA:
  - `tekla_get_drawing_status`, `tekla_get_active_drawing`, `tekla_list_drawings`,
    `tekla_get_selected_drawings`, `tekla_get_drawing_summary`, and
    `tekla_find_drawing_issues`.
  - `tekla_get_drawing_model_objects` returns full model ID/ID2/GUID identifiers represented by
    one drawing.
- Drawing-editor discovery:
  - `tekla_get_drawing_sheet` returns active-sheet paper dimensions/origins and the configured
    layout size/mode so agents can place views and sheet annotations inside known bounds.
  - `tekla_list_drawing_views` returns paper frames, scale, restriction box, coordinate systems,
    and best-effort DrawingInternal ID/ID2 values.
  - `tekla_list_drawing_objects`, `tekla_get_selected_drawing_objects`, and
    `tekla_select_drawing_objects` cover type/view/model GUID/text filters, geometry, UDAs, and
    editor selection.
- Preview-by-default drawing lifecycle/output:
  - open/save/close and batch create/modify/delete;
  - assembly, single-part, cast-unit, and GA drawings plus saved AutoDrawing rules;
  - issue/unissue/update, automatic view placement, and PDF export.
- Preview-by-default active-drawing content editing:
  - front/top/back/bottom/3D, straight/curved section, and detail views;
  - GA model views from explicit global view/display coordinate systems and a restriction box;
  - text, line, rectangle, circle, arc, polyline, polygon, revision cloud, and symbol graphics;
  - straight/angle/radius/radial/orthogonal curved dimensions, object marks, and level marks;
  - batch object creation plus text/relative-move/visibility/attribute edits, deletion, and
    merge/split operations for compatible marks.
- Explicit drawing coordinate-space contract: view-local, global-model-to-view transformation,
  and sheet/paper millimetres.
- Mock coverage for drawing discovery, preview/apply state, Position merge/copy, reference
  geometry, connections, scripting hardening, and API-reference status (79 tests total).
- The v0.7 MCP surface contains 100 registered tools, 51 of them drawing-specific.

### Changed

- `TeklaMcp.Tekla` now references the version-matched `Tekla.Structures.Drawing` package/assembly
  for every per-Tekla build (2021 baseline through 2026).
- `ModelObjectInfo` now maps part Position, selected reference-object semantic summary, and
  ControlLine coordinates.
- Type-prefiltering now recognizes `ControlLine` and `ReferenceModelObject`.
- `WriteResult` can return created integer IDs and component previews.
- Mock part previews no longer consume IDs or expose fake committed GUIDs.
- Drawing model-object links are bounded and paginated; summaries report truncation. Drawing
  deletion refuses an empty scope, object/view IDs fail closed, and PDF/enum inputs are
  validated instead of silently selecting defaults.
- `tekla_run_csharp` now compiles against every installed managed `Tekla.Structures*.dll`
  (Drawing/Dialog included when present), while Drawing stays an explicit script alias to avoid
  `Part`/`View` ambiguity. Script results expose honest execution-attempt semantics and
  partial-mutation warnings; `Print` storage is private and total-size capped, return values are
  serialized inside the execution deadline, and truncated results remain valid JSON.

### Known limitations

- Reference faces use the version-common
  `ModelInternal.Operation.GetReferenceModelObjectFaces(Identifier)` API. Signatures compile
  across the supported matrix, but geometry/metadata still needs live validation on rotated,
  scaled and base-point IFCs and across exporters.
- Arbitrary custom-connection attributes cannot be enumerated reliably by the Tekla API;
  `tekla_copy_connection` copies identity/orientation and accepts an `attributesFile` for the
  parameter set.
- `tekla_replicate_detail` is intentionally deferred until the new Position/Connection
  primitives have been validated on live models.
- Generated Tekla API documentation is still not redistributed (Trimble content);
  `tekla_get_api_reference_status` now makes missing setup explicit.
- The v0.7 drawing implementation is signature-checked against the common 2021 API surface but
  has not yet been validated against a live Windows drawing editor. DrawingInternal
  drawing/view/object IDs are best-effort; keys fall back to public drawing properties, and
  enumeration indices must be re-read after structural edits.
- Drawing creation/update/printing and view/object edits inherit Tekla editor preconditions.
  AutoDrawing rules, saved attribute files, printers/PDF paths, drawing UDAs, and exact
  coordinate behavior remain environment-dependent until live validation.
- Complex drawing-object geometry is best-effort: straight/radius/curved dimension sets,
  level marks, symbols and model-linked parts may expose only identity and bounding boxes.

## [0.6.0] - 2026-07-08

Two big ones: releases are now **per-Tekla-version builds** (download the zip matching your Tekla — no more GAC lottery), and agents get a **policy-checked C# scripting escape hatch** for Open API capabilities that have no dedicated tool yet.

### Changed

- **Per-Tekla-version release builds replace the universal build** ([#11](https://github.com/tempalg1n/tekla-mcp-server/issues/11)). Releases now ship one zip per supported Tekla version — `TeklaMcp.Server-vX.Y.Z-tekla2021.zip` … `-tekla2026.zip` (plus the unchanged net8.0 mock zip); download the one matching your Tekla. The universal single-exe scheme proved unreliable: redirect-based GAC avoidance is incompatible with every load API usable from `AssemblyResolve`, and without redirects a stale GAC copy at the compile baseline silently hijacked the bind ([#7](https://github.com/tempalg1n/tekla-mcp-server/issues/7)). `TeklaAssemblyResolver` is now policy-free: it locates the installed Tekla's `bin`, verifies the DLL major version matches the version the build was compiled for, and `Assembly.LoadFrom`s the DLLs — on a mismatch every Tekla operation fails fast with a "wrong build for this Tekla version" message naming the right zip. A stale different-version GAC copy can no longer hijack a bind (strong-named binds need the exact version), and `Assembly.Location` is real again. Building from source now takes `-p:TeklaVersion=<NuGet version>` matching your Tekla; CI compiles the whole version matrix.

### Added

- **C# scripting escape hatch** — agents can now cover Tekla Open API capabilities that have no dedicated tool yet:
  - `tekla_run_csharp` — run a short C# script against the live model (Roslyn scripting; Tekla namespaces pre-imported; `Print(...)` for output; the script's last expression is returned as JSON). Pipeline: syntax-level safety policy → compile → execute, with every failure reported back so the agent can self-correct.
  - `tekla_search_api` / `tekla_get_api_doc` — offline keyword search and full type pages over the locally generated Tekla Open API reference (`tools/TeklaApiDoc` output; `TEKLA_MCP_API_REF_DIR` to relocate), so agents verify signatures instead of guessing them.
  - Safety: scripts are **read-only by default** — mutating members are rejected unless the call passes `allowMutations=true`, which the agent is instructed to set only after showing the user the script and getting their explicit go-ahead (changes tagged with the `MCP_ORIGIN` UDA where practical; Tekla Ctrl+Z undoes them). No file/network/process/reflection/thread/`Console` access, no `#r`/`#load`, hard timeout (60 s default), capped output.
  - The mock backend validates + compiles scripts (point `TEKLA_MCP_SCRIPT_REF_DIR` at extracted Tekla DLLs) but never executes them; execution happens only on the real net48 backend.
  - New `src/TeklaMcp.Scripting/` project (netstandard2.0, Tekla-free) and a `tests/TeklaMcp.Tests` xUnit suite (policy, JSON rendering, reference search, mock pipeline).
- Server instructions now describe the escalation ladder: dedicated tools → script escape hatch (with signature verification) → `tekla_report_gap` for anything recurring.

### Fixed

- **Live Tekla startup crash (StackOverflow) on assembly resolve**: `TeklaAssemblyResolver` now byte-loads the Tekla assemblies into the default context (with a cache and re-entrancy guard) instead of `LoadFile`, the broken 2999.9.9.9 binding redirects are gone from `App.config`, and Tekla-touching initialization moved out of the type initializer into lazy `EnsureTeklaReady()`. Verified against live Tekla 2023.
- Follow-up hardening of that fix: the resolver cache is thread-safe (script execution binds on its own thread); remoting-channel alignment retries until Tekla publishes its pipes and the resolver re-probes for the Tekla bin on demand, so "start server first, open Tekla later" connects without a restart; a stale-GAC bind logs a loud stderr warning; and `tekla_run_csharp` compiles against the DLL files in the resolver's bin directory — byte-loaded assemblies have an empty `Assembly.Location`, which would have silently dropped the Tekla references from script compilation.
- Anti-GAC binding redirects were briefly restored and then **removed for good**: on .NET Framework `Assembly.Load(byte[])` applies binding policy, so the `2999.9.9.9` redirect makes the resolver's own loads circular (FileNotFound/StackOverflow — verified live). That failure mode is closed for good by the per-Tekla-version builds above ([#11](https://github.com/tempalg1n/tekla-mcp-server/issues/11)).

## [0.5.0] - 2026-07-07

Reliability and scale: the live server now connects to Tekla setups that publish a non-default Open API channel (and ignores stale GAC assemblies), and whole-model tools are fast enough for 400k+-object models. Plus a formal gap-reporting affordance for agents.

### Added

- MCP **server instructions** that tell connecting agents to do the work with the provided tools and to report missing functionality instead of scripting around it or fabricating data.
- New tool `tekla_report_gap` — lets an agent formally report a missing capability / insufficient data; returns a ready-to-file GitHub issue draft (title + body), logs the request locally, and points to the issues URL (configurable via `TEKLA_MCP_ISSUES_URL`). The server never files issues itself.
- `tekla_get_model_summary` options for huge models: `includeWeights=false` skips the per-part weight lookup; `maxObjects` caps the scan (the result is then marked `truncated`) ([#5](https://github.com/tempalg1n/tekla-mcp-server/issues/5)).

### Changed

- **Large-model traversal is dramatically faster** ([#5](https://github.com/tempalg1n/tekla-mcp-server/issues/5); ~420k-object models previously took ~10 min to count and dropped the MCP connection on summary):
  - `AutoFetch` is enabled on every Tekla object enumeration — object data is fetched in batches instead of one remoting round-trip per property read.
  - Scans filter on cheap direct properties first (`MapBasic`) and read report properties / solids (`Enrich`) only for objects actually returned; `GetSolid()` — the most expensive call — is no longer executed for every object in the model.
  - New `ITeklaModelService.CountObjects`: `tekla_count_objects` no longer materializes DTOs; a completely unfiltered count uses the enumerator size and returns instantly.
  - `tekla_get_model_summary` streams cheap reads (type, class, profile, material + optional `WEIGHT`) instead of fully mapping every object.
  - Queries filtering on a known type (`Beam`, `PolyBeam`, `ContourPlate`, `Grid`) pre-filter at the Tekla API level via `GetAllObjectsWithType`.

### Fixed

- **Live connection to Tekla whose Open API channel is published under a non-default name** (e.g. `Tekla.Structures.Model-Console:2023.0.0.0`, seen with Tekla 2023 SP7 — [#7](https://github.com/tempalg1n/tekla-mcp-server/issues/7)). Before the first `Model()` the server now probes the machine's named pipes and aligns the client channel with the one Tekla actually publishes (`TeklaRemotingChannel`); override with the `TEKLA_MCP_CHANNEL` env var.
- **Stale Tekla assemblies in the GAC no longer break the universal build** ([#7](https://github.com/tempalg1n/tekla-mcp-server/issues/7)). If the compile-baseline Tekla version (2021) was installed in the GAC, .NET bound to it silently and the server spoke the wrong protocol to a newer running Tekla. `App.config` now redirects `Tekla.Structures`/`Tekla.Structures.Model` to an unreachable version so every bind goes through `TeklaAssemblyResolver`, which always supplies the running Tekla's DLLs.
- "Not connected" errors now include diagnostics: the client channel name, the loaded Tekla API version and its path, and the `Tekla.Structures.Model-*` pipes published on the machine.

## [0.4.0] - 2026-06-22

Multi-version support: one **universal** live build works with any installed Tekla (2021+), so users and contributors no longer pick a version at build time. Plus a cross-platform generator for an offline Tekla Open API reference.

### Added

- **Universal multi-version Tekla support.** One live build now works with any installed Tekla (2021+): it compiles against a baseline API and loads the Tekla DLLs from the running Tekla at runtime (`TeklaAssemblyResolver`), so no per-version artifact or download is needed. The server auto-detects the running Tekla (override with the `TEKLA_BIN_DIR` env var). Build overrides: `-p:TeklaVersion=` (compile baseline) and `-p:TeklaBinDir=` (local install).
- `tools/TeklaApiDoc` — cross-platform generator (metadata-only via `MetadataLoadContext`) that emits a grep-friendly Markdown reference of the Tekla Open API for offline signature verification. Output (`reference/`) is git-ignored. Developer/agent aid; no change to the server.

### Changed

- `docs/tekla-api-notes.md` now points to the local API reference and marks the write-path API calls as signature-verified (runtime behavior still to confirm on a live model)

## [0.3.0] - 2026-06-22

First release with **write** capability: agents can now create, edit and delete model objects — preview-by-default with `apply=true` to commit, and `MCP_ORIGIN` tagging for traceability. Adds geometry/grid resolution, parametric generators, and "find & fix" tools for columns.

### Added

- **Write operations (create / edit / delete), preview-by-default with `apply=true` to commit; created/modified objects tagged with the `MCP_ORIGIN` UDA:**
  - Geometry/grids: `tekla_list_grids`, `tekla_resolve_point` (translate axis labels like `1`/`Д` + elevation into coordinates)
  - Primitives: `tekla_create_beam`, `tekla_create_column`, `tekla_create_plate`, `tekla_modify_part`, `tekla_swap_handles`, `tekla_delete_objects`
  - Generators: `tekla_generate_frame`, `tekla_create_column_grid`, `tekla_create_beam_between_grids`
  - Fixers: `tekla_straighten_columns` (re-plumb crooked columns), `tekla_fix_column_handles` (re-orient flipped columns)
  - Core: `ITeklaModelService.GetGrids/ResolvePoint/CreateParts/ModifyParts/DeleteObjects` and new DTOs (`PartSpec`, `PartModification`, `WriteResult`, `Point3D`, `GridLineInfo`, `PointResult`)
- Grid coordinate parsing supports repeat syntax (e.g. `4*6000`) for more robust axis resolution

## [0.2.2] - 2026-06-22

Capability expansion: assembly analytics, a model-quality (QA) check battery, a generic property reader, table export, and a `useSelection` scope switch across the query/analytics tools.

### Added

- New tool `tekla_get_properties` — read any named properties (report properties, UDAs, or built-ins like `VOLUME`/`AREA`/`WEIGHT`) for an object by GUID, without a dedicated tool per field
- New tool `tekla_export_objects` — export filtered objects as a CSV or Markdown table (bill-of-materials handoff)
- New tool `tekla_list_assemblies` — assembly marks (`ASSEMBLY_POS`) with part count and total weight per mark
- New tool `tekla_count_assemblies` — count distinct assembly marks (unique assembly types)
- New tool `tekla_get_assembly_parts` — list all parts sharing a given assembly mark
- New tool `tekla_find_modeling_issues` — QA battery (missing material/profile/class, zero weight, not-numbered) grouped with sample GUIDs
- `useSelection` scope switch on query/analytics tools to operate on the current Tekla UI selection instead of the whole model
- `assembly` group key for `tekla_group_weight_by` and `tekla_list_distinct_values`
- Core: `ITeklaModelService.GetProperties`, `ObjectQuery.UseSelection`, and a new `QaReport` DTO

### Changed

- Tekla and Mock backends route filter-based scans (`FindObjects`, `SelectObjects`, `SetUdas`) through a shared source selector so every filter tool honors `useSelection`

## [0.2.1] - 2026-06-19

Patch release focused on improving `tekla_select_objects` usability for agent workflows.

### Added

- `tekla_select_objects` now accepts `guidIn` (comma/semicolon/newline separated list of GUIDs) for explicit object allow-list selection

### Changed

- `tekla_select_objects` description now explicitly documents UDA/attribute-capable filtering
- Object query pipeline in both Mock and Tekla backends now honors GUID allow-list filtering, enabling deterministic selection passes after discovery

## [0.2.0] - 2026-06-19

Major MCP capability expansion focused on attribute discoverability, spatial context, and profile connection analytics.

### Added

- New tool `tekla_find_attributes_by_value` to discover likely attribute names from a known value (for cases like `BK1`)
- New tool `tekla_analyze_profile_connections` to estimate unique connection/node types for a profile using beam-end proximity
- Generic attribute filters in query/workflow tools (`attributeName`, `attributeEquals`, `attributeContains`)
- Generic UDA + report property lookup support in `ITeklaModelService` backends
- New core DTOs for attribute match results and profile connection summaries

### Changed

- `ModelObjectInfo` now includes spatial data (`Center*`, `Start*`/`End*`, `Min*`/`Max*`) so agents can reason about coordinates
- `tekla_find_objects`, `tekla_count_objects`, `tekla_sum_weight`, `tekla_group_weight_by`, `tekla_list_distinct_values`, and `tekla_select_objects` now support UDA + generic attribute filtering
- Mock model now provides deterministic geometric coordinates and sample UDA values (including `RU_FN1_MRK=BK1` on columns)
- Tekla backend mapping now reads beam endpoints and solid bounding boxes (best effort, with graceful fallback)

## [0.1.1] - 2026-06-19

Recommended public release. Adds open-source documentation and project polish that were not included in v0.1.0.

### Added

- MIT [LICENSE](LICENSE)
- [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/releasing.md](docs/releasing.md)
- English [README.md](README.md) as the primary documentation
- Russian [README.ru.md](README.ru.md) as a secondary translation
- CI status badge in README

### Changed

- Rewrote project documentation for GitHub open-source audience
- [AGENTS.md](AGENTS.md), [docs/architecture.md](docs/architecture.md), and [docs/tekla-api-notes.md](docs/tekla-api-notes.md) cleaned up and aligned with public release
- Updated `.csproj` comments to remove internal dev-machine references

### Removed

- `TeklaMcp.Mac.slnf` (platform-specific solution filter no longer needed in public docs)

## [0.1.0] - 2026-06-19

Initial tagged release. Core MCP server and release automation.

### Added

**MCP tools (15):**

- `tekla_get_connection_info` — connection status and active backend
- `tekla_get_model_summary` — model-wide object count, weight, and breakdowns
- `tekla_list_objects` / `tekla_find_objects` — list and search with filters
- `tekla_get_object_by_guid` — single object lookup
- `tekla_get_selected_objects` — read current Tekla UI selection
- `tekla_analyze_by_material` — material breakdown (count + weight)
- `tekla_count_objects` / `tekla_sum_weight` — filtered count and weight sum
- `tekla_group_weight_by` / `tekla_list_distinct_values` — grouped analytics
- `tekla_select_objects` — programmatic UI selection by filter
- `tekla_get_object_udas` — read user-defined attributes
- `tekla_set_object_udas` / `tekla_set_udas_by_filter` — UDA writes with preview-by-default (`apply=false`)

**Architecture:**

- `ITeklaModelService` abstraction with Mock and Tekla backends
- Multi-target server: `net8.0` (mock, no Tekla) and `net48` (live Tekla on Windows)
- MCP stdio transport via the official C# SDK

**Release infrastructure:**

- GitHub Actions CI and automated release workflow
- Issue and pull request templates
- `CHANGELOG.md` and release notes template

### Notes

- Tekla NuGet packages pinned to `2023.0.0` for tested environments
- UDA write tools require explicit `apply=true` to modify the model
- Not affiliated with Trimble or Tekla Structures

[Unreleased]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.7.0...HEAD
[0.7.0]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.2.2...v0.3.0
[0.2.2]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/tempalg1n/tekla-mcp-server/releases/tag/v0.1.0
