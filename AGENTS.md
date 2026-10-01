# AGENTS.md — guide for AI agents working in this repo

This file is the contract for any AI agent touching this repository.
Read it fully before editing. It complements [README.md](README.md) (human-facing) and
the notes under [docs/](docs/).

If you change architecture, build flow, or conventions, **update this file in the same
change**.

---

## 1. What this project is

An MCP server exposing Tekla Structures model data to AI assistants.
Written in **C#**, because the Tekla Open API is a Windows/.NET assembly set.

The server multi-targets **`net8.0`** (mock backend, no Tekla required) and **`net48`**
(real Tekla backend on Windows). The Tekla integration project only RUNS on Windows; it
compiles on any OS through `TeklaMcp.sln` (see §5), but the server pulls it in only in its
Windows `net48` build.

---

## 2. Golden rules

1. **Never break the `net8.0` build.** It must compile and run with the Mock backend
   without Tekla installed. Do not add Tekla references outside `src/TeklaMcp.Tekla/`.
2. **All Tekla Open API code lives in `src/TeklaMcp.Tekla/` only.** That project is
   `net48`, Windows-only, and is referenced by the server *only* in its `net48` build.
3. **MCP tools depend on `ITeklaModelService`, never on Tekla types.** Add a method to
   the interface first, implement it in *both* `MockTeklaModelService` and
   `TeklaModelService`, then expose it as a tool.
4. **stdout is sacred.** The stdio transport uses stdout for JSON-RPC. Never
   `Console.WriteLine` to stdout. Logging is already routed to stderr in `Program.cs`.
   Keep it that way.
5. **Keep DTOs flat and serializable.** Tools return `TeklaMcp.Core.Models.*` types
   (plain classes, public get/set). No Tekla objects cross the tool boundary.
6. **Mark unverified Tekla API usage.** Wrap risky calls in `try/catch`, degrade
   gracefully, add `// TODO(windows):` where unsure, and note the API in
   `docs/tekla-api-notes.md`.
7. **Cross-TFM safety.** `Core` and `Mock` are `netstandard2.0`. Do not use APIs or
   language features that don't compile there (e.g. avoid `record`/`init` unless you add
   an `IsExternalInit` polyfill; prefer plain classes).
8. **Per-Tekla-version builds — don't fight the GAC.** `TeklaMcp.Tekla` compiles against
   ONE Tekla version (`-p:TeklaVersion`); releases ship one zip per supported version
   (2021–2026, see `docs/releasing.md`), and `TeklaAssemblyResolver` fails fast on a
   version mismatch. Never reintroduce the universal-build tricks: no anti-GAC
   bindingRedirects for `Tekla.*`, no `Assembly.Load(byte[])`/`LoadFile` in the resolver —
   both failed on live machines (issue #11; see "Assembly loading history" in
   `docs/tekla-api-notes.md`). When a new Tekla version ships, extend the matrices in
   `.github/workflows/release.yml` and `ci.yml`. The resolver searches
   `TeklaAssemblyResolver.ProbeDirectories` — the Open API folder, plus its `Net48Runtime`
   subfolder where it exists (Tekla 2026 keeps `Tekla.Structures.dll` only there, issue #17) — for
   ANY name except `*.resources` and the server's own `TeklaMcp.*`; a file answering a
   non-`Tekla*` name must pass `TeklaAssemblyProbe.Accepts` (name, public key token, version ≥
   requested). Never narrow it back to one folder or to `Tekla*` names: the LoadFrom context finds
   a DLL's dependencies only in that DLL's own folder, so every bind across the two folders
   (`Trimble.Remoting`, `DotNetKit`, …) depends on the handler. The CLR caches bind failures:
   - never THROW from the `AssemblyResolve` handler — return null;
   - create `TeklaModelService` only through `TeklaBackendFactory`, i.e. after the version check;
   - keep the 2021–2023 channel alignment (`TeklaRemotingChannel`) away from 2024+ builds, which
     name their Trimble.Remoting channels themselves;
   - create an Open API client (`new Model()`, `new DrawingHandler()`, a script's own
     `new Model()`) only after `EnsureTeklaReady(channel)` — its `EnsurePublished` guard refuses
     while the channel is not published. A client created then is dead for the process (on 2021:
     no exception, just `GetConnectionStatus() == false` forever). The guard must only block on
     evidence: every unknown (2024+, a partial pipe listing) lets the call through;
   - get the Model client through `GetConnectedModel()`/`ConnectModel()` — one real `GetInfo()`
     that reconnects stale clients (`TeklaRemotingChannel.TryReconnect`) BEFORE the tool reads or
     writes. Never retry an operation after a mid-call connection failure (writes: `unknown`,
     §4); never reconnect to a channel with another name (it may be another Tekla); swap clients
     through `DelegateProxy.Delegate`, never `CDelegateSetter.SetInstanceForUnitTesting` (it
     breaks the catalog client — verified).

   Each of these once turned a transient state into "broken until the server restarts".

---

## 3. Code map

| Path | TFM | Role |
|---|---|---|
| `src/TeklaMcp.Core/` | netstandard2.0 | `ITeklaModelService` + DTOs + backend-agnostic logic (aggregation, IFC placement reader, file-exchange formats/policy, curve/solid/grid math, schematic renderer + PNG encoder, write outcomes, connection-error classification) |
| `src/TeklaMcp.Mock/` | netstandard2.0 | mock backend: synthetic frame + arch PolyBeam, stateful drawings; one partial file per area |
| `src/TeklaMcp.Scripting/` | netstandard2.0 | Roslyn script escape hatch (policy, approval gate, reference selection, engine, SafeJson) + API reference generation and search. References Core; no Tekla references — Tekla assemblies are supplied at runtime |
| `src/TeklaMcp.Tekla/` | net48 (x64) | real Tekla Open API backend (runs on Windows only; compiles anywhere) — `TeklaModelService` split into one partial file per area, plus assembly loading and the remoting channel |
| `src/TeklaMcp.Server/` | net8.0 (+net48 on Windows) | MCP host (`Program.cs`, `ToolErrorFilter`, `ShutdownGuard`) + `tekla_*` tools in `Tools/` |
| `tests/TeklaMcp.Tests/` | net8.0 | xUnit tests (mock-only, no Tekla). References Core/Mock/Scripting — NOT the Server, so the tool layer is covered only by the smoke |
| `tests/TeklaMcp.Smoke/` | net8.0 exe | MCP stdio smoke: launches the built server DLL as a real MCP client, checks initialize + `tools/list` + a few calls. Not in `TeklaMcp.sln` |
| `tools/TeklaApiDoc/` | net8.0 exe | CLI over `ApiReferenceGenerator` (writes `reference/tekla-api/`). Not in `TeklaMcp.sln` |
| `integrations/codex/` | PowerShell 5.1 | Codex plugin card installer + card template (issue #18), copied by the Server project into every `net48` build under `codex\`, so it runs from an unpacked zip with no repository around it. Keep it ASCII-only (5.1 reads BOM-less scripts as ANSI), write JSON without a BOM, and change Codex only through its CLI (`codex plugin …`, `codex mcp remove`) — never by editing `config.toml`. Test it with an isolated `CODEX_HOME` |

Multi-targeting: in `TeklaMcp.Server.csproj` the `net48` TFM is dropped when
`$(OS) != Windows_NT`, so non-Windows builds of the SERVER never pull in the Tekla project
(`TeklaMcp.sln` still compiles it). The `#if NET48` block in `Program.cs` selects the real
backend only in that build: `TeklaAssemblyResolver.Register()` → `TeklaRemotingChannel.Align()` →
`TeklaBackendFactory.Create()` (unless `TEKLA_MCP_USE_MOCK=1`).

Where to find things (verify with `ls` — this list is a map, not a contract):

| Area | Core (Tekla-free) | Mock partial | Tekla partial / helper |
|---|---|---|---|
| queries, analytics, UDAs, writes, connections, grids | `Aggregation`, `WriteOutcome.cs` (`WriteOutcome` + `WriteProgress`), `ModelPaths` (write-target check), `Geometry/GridMath` | `MockTeklaModelService.cs` | `TeklaModelService.cs` |
| components | `ComponentSpecs` | `.Components.cs` | `TeklaComponentService.cs` |
| model save/open | `ModelPaths` | `.ModelFile.cs` | `TeklaModelFileService.cs` |
| environment (advanced options, catalogs, API-reference source) | `CatalogListing` | `.Environment.cs` | `TeklaEnvironmentService.cs` |
| part solids | `Geometry/SolidMath.cs` (`SolidMath`, `SolidBuilder`) | `.Solid.cs` | `TeklaSolidService.cs` |
| curve geometry | `Geometry/CurveMath` | `.CurveGeometry.cs` | `TeklaCurveGeometryService.cs` |
| file exchange | `FileExchange/*` (`FilePathPolicy`, `ExportRunner`, `UdaImportRunner`, readers/writers) | `.FileExchange.cs` | `TeklaFileExchangeService.cs` |
| reference models / IFC | `Ifc/IfcPlacementReader` | `MockTeklaModelService.cs` | `TeklaModelService.cs` |
| schematic + view capture | `Rendering/*` (`SchematicRenderer`, `ViewProjection`, `RasterCanvas`, `BitmapFont`, `PngEncoder`) | `.Visual.cs` | `TeklaSchematicService.cs`, `TeklaViewCaptureService.cs`, `TeklaWindowCapture.cs` (Win32) |
| drawings | `Models/DrawingInfo.cs`, `Models/DrawingWriteModels.cs` | `MockTeklaModelService.cs` | `TeklaDrawingService.cs`, `TeklaDrawingObjectService.cs`, `TeklaDrawingContentService.cs` |
| connection lifecycle, assembly loading | `ConnectionErrors`, `ErrorText`, `RemotingChannelNames`, `TeklaBinLayout`, `TeklaAssemblyProbe` | — | `TeklaAssemblyResolver.cs`, `TeklaBackendFactory.cs`, `TeklaRemotingChannel.cs` |

Scripting: `ScriptPolicy` (syntax gate), `ScriptApprovalGate` (`expectedSha256` binding),
`ScriptReferenceSelector` (which Tekla DLLs a script compiles against), `ScriptEngine`,
`ScriptGlobals`, `SafeJson` + `SafeJsonReport` (return values and their caps), `ApiReference`
(search) + `ApiReferenceGenerator` (Markdown reference from the installed Tekla).

Tool files (`src/TeklaMcp.Server/Tools/`):

| File | Tools / role |
|---|---|
| `ModelInfoTools.cs` | `tekla_get_connection_info`, `tekla_get_model_summary` |
| `ModelQueryTools.cs` | list/find/get-by-GUID/selected objects, solid bbox, control lines |
| `ModelWorkflowTools.cs` | count, `sum_weight`, `group_weight_by`, `list_distinct_values`, `select_objects`, `export_objects` |
| `ModelAnalysisTools.cs` | `tekla_analyze_by_material` |
| `ModelAssemblyTools.cs` | assembly list/count/parts |
| `ModelAttributeTools.cs` | `tekla_find_attributes_by_value`, `tekla_discover_udas` |
| `ModelPropertyTools.cs` | `tekla_get_properties` |
| `ModelQaTools.cs` | `tekla_find_modeling_issues` |
| `ModelUdaTools.cs` | get/set object UDAs, `tekla_set_udas_by_filter` |
| `ModelWriteTools.cs` | create beam(s)/column/plate, modify part(s), swap handles, delete |
| `ModelGeneratorTools.cs` | grid-based generators and fixers (`generate_frame`, `straighten_columns`, …) |
| `ModelConnectionTools.cs` | list/find/modify/create/copy connections, `tekla_create_component`, profile-connection analysis |
| `ModelGeometryTools.cs` | grids, `resolve_point`, `get_part_curve_geometry`, `get_part_solid` |
| `ModelReferenceTools.cs` | `tekla_get_reference_geometry` |
| `ModelFileExchangeTools.cs` | `export_parts_file`, `set_udas_from_file`, `export_reference_objects_file` |
| `ModelEnvironmentTools.cs` | `tekla_get_advanced_options`, `tekla_list_catalog` |
| `ModelLifecycleTools.cs` | `tekla_save_model`, `tekla_open_model` |
| `ModelVisualTools.cs` | `tekla_capture_view`, `tekla_render_schematic` (image + JSON answers) |
| `ModelScriptTools.cs` | `run_csharp`, `check_csharp`, `search_api`, `get_api_doc`, `get_api_reference_status` |
| `ModelMetaTools.cs` | `tekla_report_gap` |
| `DrawingQueryTools.cs` / `DrawingWriteTools.cs` / `DrawingContentTools.cs` | drawing reads / drawing lifecycle + batch writes / views, annotations, dimensions, marks |
| `ToolHelpers.cs`, `DrawingToolHelpers.cs` | shared parsing, `BuildQuery`, the `Write(...)` wrappers (target + outcome) |

---

## 4. How to add a new capability

1. Add the method to `ITeklaModelService` (`src/TeklaMcp.Core/ITeklaModelService.cs`),
   with XML docs describing behavior and the "not found" contract.
2. Implement it in `MockTeklaModelService` (make the synthetic result believable).
3. Implement it in `TeklaModelService` using the Tekla Open API.
   A new AREA gets its own partial file on both sides (`MockTeklaModelService.<Area>.cs`,
   `Tekla<Area>Service.cs` — see the map in §3), and derived logic that both backends need goes
   into Core, where it is unit-tested.
4. Expose it as a tool in `src/TeklaMcp.Server/Tools/` with `[McpServerTool(Name = "tekla_...")]`
   and a clear `[Description]`. First parameter is `ITeklaModelService model` (DI-injected,
   not shown to the LLM). Remaining parameters become tool inputs — give each a `[Description]`.
5. Update the tool table AND the tool count in [README.md](README.md) ("exposes **N tools**",
   drawing-specific count included).
6. **Bump the smoke's tool count.** `tests/TeklaMcp.Smoke` asserts the EXACT number of registered
   tools, passed as its second argument in BOTH `.github/workflows/ci.yml` and
   `.github/workflows/release.yml` (currently `116`). Adding or removing a tool without changing
   both fails CI. Keep workflows, README count and `tools/list` equal.
7. Add the user-facing change to `[Unreleased]` in [CHANGELOG.md](CHANGELOG.md), and tests in
   `tests/TeklaMcp.Tests` for any Core/Mock/Scripting logic.

Tool naming: `tekla_<verb>_<noun>`, snake_case. **Do not add write/mutating tools**
without an explicit request and a safety gate (see existing UDA tools for the preview pattern).

Tool errors: the MCP SDK replaces the message of every exception except `McpException` with a
bare "An error occurred invoking '…'." — `ToolErrorFilter` (registered in `Program.cs`) sends
`ErrorText.Flatten(ex)` instead, because a lost Tekla connection used to reach the agent as a
cause-less tool error (DEV-005). So throw exceptions whose messages say what went wrong and what
to do; keep `McpException` for argument/validation errors raised in the tool layer. In the live
build the filter also replaces a lost connection (`Core/ConnectionErrors`: a `RemotingException`
or a failed `Tekla.Structures*` type initializer in the chain) with
`TeklaRemotingChannel.DiagnoseConnectionFailure` — so a list/query tool must let a connection
failure PROPAGATE: `tekla_list_drawings` used to catch it and answer `[]`, i.e. "no drawings".
Acquire the handler/model outside a best-effort `try`.

Process lifetime: `ShutdownGuard` (Server, Windows) ends the process when the parent that launched
it exits and cuts a shutdown that hangs for 10 s (a Tekla call cannot be cancelled). Closing
stdin already stops the host; keep both so orphaned servers cannot pile up and hold Tekla.

### Conventions added for the analytics tools

- **`useSelection` scope switch.** Filter/analytics tools accept `useSelection` (bool). It maps
  to `ObjectQuery.UseSelection`; both backends route the scan through the current UI selection
  instead of the whole model (`EnumerateSource` in `TeklaModelService`). When you add a new
  filter-based tool, plumb `useSelection` through `BuildQuery` for consistency.
- **Assemblies via `ASSEMBLY_POS`.** Assembly grouping/counting uses the assembly mark
  (`ASSEMBLY_POS` report property, already on `ModelObjectInfo.AssemblyPos`) — no per-object
  `GetAssembly()` calls in the hot scan path. Identical marks = identical assembly types.
  Only populated after numbering. Physical main-part detection is a future enhancement.
- **Generic property reader.** Prefer extending `tekla_get_properties` (any report/UDA/built-in
  name) over adding a new tool for each property you want to expose.
- **QA checks** in `tekla_find_modeling_issues` are pure tool-layer heuristics over the DTO —
  tune predicates there; no Tekla API needed.
- **Aggregation goes through `ITeklaModelService.AggregateBy` — never `FindObjects`.**
  `tekla_group_weight_by` / `tekla_sum_weight` / `tekla_list_distinct_values` stream one group
  key + WEIGHT per object; no DTO materialization, no `Enrich`, no solids (the old
  `FindObjects`-based path paid a `GetSolid()` per object to read one double — minutes on real
  models). `groupBy` accepts `uda:NAME` / `attr:NAME`; parsing, cursor handling and row shaping
  are shared between backends in `TeklaMcp.Core.Aggregation` (unit-tested, Tekla-free).
- **Cursor paging for heavy scans.** MCP clients abort requests after ~60 s and the reply is
  lost even if the server finishes, so scan-shaped tools take `maxObjects` + `cursor` and
  return `truncated` + `nextCursor` (today: a plain source offset; the skip phase is bare
  `MoveNext`, no property reads). Pages cover disjoint slices; the agent merges rows by key.
  Use the same contract for any new scan-shaped tool instead of raising timeouts.
- **The parts chain.** "Physical parts" = `PartTypeEnums` (BEAM…CUSTOM_PART) enumerated
  type-by-type. On a 470k-object model the chain enumerates in ~5 s vs ~52 s for
  `GetAllObjects`. (An earlier note here said `GetAllObjectsWithType` has no multi-type
  overload — wrong: a `System.Type[]` overload exists, 2021 included; only the `ArrayList` form
  failed. It is unmeasured against the chain — see `docs/backlog.md` §7.) Analytics and
  attribute-discovery tools default to `partsOnly=true`; keep `Matches()` verifying the type
  either way. The mock mirrors the scope via `PartTypeNames`.
- **Search results must distinguish "not found" from "did not look".**
  `tekla_find_attributes_by_value` returns `AttributeSearchResult` with
  `scannedObjects`/`candidatesTried`/`truncated`/`message` precisely because a bare `[]` once
  sent agents down false trails (a value present on 5k+ objects was reported absent — wrong
  candidate list AND an exhausted object budget). Any new search/discovery tool must report
  its coverage the same way, and scan failures go into `message`, never a silent catch.
- **UDA discovery is sample-based.** `tekla_discover_udas` reads `GetAllUserProperties` on at
  most `sampleSize` objects (~100+ ms per object live — measured 400 parts ≈ 54 s), spreading
  the budget across part types because enumeration order clusters by type. Results say
  SAMPLE ONLY when truncated; never present a sample as a full inventory.

New tool files: `ModelAssemblyTools.cs`, `ModelQaTools.cs`, `ModelPropertyTools.cs`.

### Performance rules for whole-model scans (issue #5)

Real models reach 400k+ objects; a scan with per-object remoting calls takes minutes and can
blow the MCP client timeout. In `TeklaModelService`:

- **Set `AutoFetch = true` on every `ModelObjectEnumerator`** (done centrally in
  `EnumerateSource`) — batches object data instead of one round-trip per property read.
- **Filter cheap, enrich late.** Match objects with `MapBasic` (identity + direct Part
  properties, no remoting extras), and call `Enrich` (report properties + solid bbox) only on
  objects actually returned to the caller. Never call full `Map()` inside a scan loop.
- **`GetSolid()` is the most expensive call in the API** — only `Enrich(..., includeSolid: true)`
  results that need coordinates, never count/summary paths.
- **Counting**: use `ITeklaModelService.CountObjects` (enumerator `GetSize()` when unfiltered);
  don't count via `FindObjects(...).Count`. Caveat (`docs/backlog.md` §7): with `AutoFetch` the
  enumerator may snapshot the whole source up front — one unfiltered count took ~20 s on a live
  Tekla 2023 model. Measure before assuming `GetSize()` or cursor pages are cheap.
- When a query filters by a known type, `EnumerateSource` pre-filters with
  `GetAllObjectsWithType` (see `TypeEnumMap`) — extend the map when you add type-heavy tools.

### Conventions for PART SOLIDS (`tekla_get_part_solid`)

- **A few GUIDs, never a filter** — `GetSolid()` is the most expensive read; the tool caps at 20
  parts. Bulk boxes go through `tekla_export_parts_file` (`solidAabb`).
- **Caps live in `SolidBuilder` (`Core/Geometry/SolidMath.cs`)**: faces beyond `maxFaces` are counted, a face that
  would cross `maxPoints` is left out WHOLE, and `truncated` + reason say the list is not the
  shape. Feed loops lazily (`Func<IEnumerable<…>>`) so a full builder stops remoting reads.
- **Geometry-derived facts only**: the outer loop is the largest by area (`LoopArea`), because
  Tekla does not document loop order. The AABB is always labelled a box.

### Conventions for ENVIRONMENT read tools (advanced options, catalogs)

`tekla_get_advanced_options`, `tekla_list_catalog` (`ModelEnvironmentTools.cs`,
`TeklaEnvironmentService.cs`, `MockTeklaModelService.Environment.cs`).

- **Other remoting clients, same guard.** `TeklaStructuresSettings` talks over the base channel
  and `CatalogHandler` over its own Catalogs channel; neither is in the connection guard, so
  every entry point calls `GetConnectedModel()` first. Never create a `CatalogHandler` (or any
  other `*Handler`) before that.
- **Catalog paging lives in `Core/CatalogListing`**: cursor = catalog offset, `scanned` coverage,
  and only items that land in the page are mapped — mapping is where live remoting cost is. Set
  `ProfileItemEnumerator.SelectInstances = details`: names need no per-item `Select()`.
- **An unknown option or empty catalog is data, not an error** (`found=false`, a message saying
  how much was searched).
- `Tekla.Structures.Catalogs` is referenced like the other Tekla packages (`ExcludeAssets=
  "runtime"`, TeklaBinDir fallback); the resolver supplies it at runtime.

### Conventions for WRITE tools (create / edit / delete)

Write tools are now in scope (explicitly requested, with safety gates). Rules:

- **Preview-by-default.** Every mutating tool takes `apply` (default false). With `apply=false`
  NOTHING is written — return a `WriteResult` plan (counts + preview). Only `apply=true` commits.
- **Tag origin.** Backends stamp created/modified objects with the `MCP_ORIGIN` UDA so agent
  output is findable and reversible. Keep this behavior.
- **Cap batches.** Mutating-by-filter tools must pass a `limit` (default 200) for safety.
- **Outcome + target on every write result** (`WriteResult`, `UdaOperationResult`,
  `UdaFileWriteResult`, `DrawingWriteResult`, `ModelFileOperationResult`; `ScriptResult` gets the
  target via `ToolHelpers.ResolveTarget`). Every mutating
  tool goes through a `ToolHelpers.Write(model, expectedModelPath, () => …)` wrapper and takes the
  optional `expectedModelPath` parameter (`ToolHelpers.ExpectedModelPathDescription`): the
  wrapper resolves `ITeklaModelService.GetWriteTarget()` (model path + Tekla PID), refuses a
  mismatch BEFORE the backend is called, stamps `Target`, derives a missing `Outcome` from the
  counters and turns `unknown` into `isError` with the full result in the text. A new write
  tool that bypasses the wrapper is a bug.
- **Model lifecycle (`tekla_save_model` / `tekla_open_model`).** `ModelHandler.Open` discards
  unsaved changes — `OpenModel` re-checks `IsModelSaved()` immediately before the call and refuses
  unless `discardUnsavedChanges`; never weaken that, and never add an implicit save or close to
  another tool. `expectedModelPath` there means the model being saved or CLOSED.
- **Outcomes come from what happened, not from counters.** The Open API has no transactions —
  an `Insert`/`Modify`/`Delete`/`SetUserProperty` is in the model when it returns and nothing
  rolls back. Live model writes use `WriteProgress` (`Core/WriteOutcome.cs`): `BeginWrite()`
  right before the first mutating call, `Complete()` after the final commit, `Fail(ex)` /
  `ItemFailed(ex)` in the catches, and the backend's `Stamp(result, progress, …)` on every
  return. A failure between the first write and the commit is `unknown` — never "not
  written". Count an object right after its write succeeded (not before, not after follow-up
  reads), and treat a `false` from `Modify()`/`Delete()` as a refusal with
  an error, never silently. Drawing writes are coarse (`DrawingFailure`: a lost connection
  during apply → `unknown`; `CommitDrawingChanges` returning false → `unknown`). The mock never
  fails half-way, so counters are exact there.
- **Small service surface.** Keep backend write methods primitive and batch-oriented:
  `CreateParts`, `ModifyParts`, `DeleteObjects`, `CreateConnections`, `ModifyConnections` and
  `CreateComponents` (ordered `ComponentInput` for plugins; validated by `Core/ComponentSpecs` in
  the tool layer AND the backends; read back with children, because a plugin's Run is not
  observable — zero children is an error). The other mutating members are the UDA writers
  (`SetObjectUdas`, `SetUdas`, `SetUdasFromFile`), `SaveModel`/`OpenModel`, the drawing batches
  and `ExecuteScript`. **Generators, fixers
  and replication workflows** (`tekla_generate_frame`, `tekla_straighten_columns`,
  `tekla_fix_column_handles`, future `tekla_replicate_detail`, …) live in the TOOL layer and
  compose those primitives — do NOT add per-generator interface methods.
- **Global coordinates.** Tool inputs are global model coordinates (mm). The Tekla backend forces
  the global `TransformationPlane` around mutations (`WorkPlaneHandler`); preserve that.
- **Position is a first-class DTO.** `PartPosition` carries Plane/Rotation/Depth + offsets.
  Creation/modification may set fields explicitly or copy the complete Position from
  `MatchPositionGuid`; explicit fields override the copied values.
- **Connections are committed after geometry.** `CreateConnections` performs one
  `CommitChanges()` before resolving primary/secondary GUIDs, preventing the common
  freshly-created-part race. Negative `ConnectionSpec.Number` maps to
  `BaseComponent.CUSTOM_OBJECT_NUMBER`. Arbitrary custom-component attributes cannot be
  enumerated reliably; use `AttributesFile`.
- **Read write results back from the database.** After `CommitChanges()`, re-select every
  committed object by GUID and map *that* — never echo the in-memory object you just wrote.
  Tekla canonicalizes `Part.Position` on commit (`TOP`+180° is stored as `BELOW`+0°), so an echo
  reports values the model does not agree with and the next read looks like a lost write.
  `ModifyParts`, `ModifyConnections` and `CreateConnections` all do this; keep it.
- **Connection orientation goes through `ModifyConnections`.** A written `Connection.UpVector`
  only persists under `AUTODIR_NA`; other modes accept the write and silently recompute. The
  backend switches to NA whenever an explicit vector arrives without a caller-named mode.
- **One connection per primary/secondary pair.** Tekla rejects a second insert on an occupied
  pair, so `ConnectionSpec.ReplaceExisting` deletes the occupants and **commits** before
  inserting. Do not drop that intermediate commit.
- All three quirks are verified live and documented under "Known model-layer quirks" in
  `docs/tekla-api-notes.md`; the server instructions in `Program.cs` warn agents about them.
  When you discover another one, add it in both places in the same change.
- Shared parsing/query helpers for write tools live in `ToolHelpers.cs`.

New tool files: `ModelGeometryTools.cs`, `ModelWriteTools.cs`, `ModelGeneratorTools.cs`,
`ModelReferenceTools.cs`, `ModelConnectionTools.cs`, `ModelLifecycleTools.cs`.
The live-Tekla write path is still the least-verified code in the repo. Verified live on Tekla
2023: the Position/UpVector/one-connection-per-pair behavior of `ModifyParts`/`ModifyConnections`/
`CreateConnections`. NOT yet run live: the `unknown` outcome end to end, `CreateComponents` with a
real plugin, `SaveModel`/`OpenModel` — see "Known limitations" in `CHANGELOG.md`,
`docs/backlog.md` and `docs/tekla-api-notes.md`.

### Conventions for reference-model / IFC geometry

- Reference objects often have `Identifier.GUID == Guid.Empty`; use their integer `Id` as the
  session-local address for `GetReferenceGeometry`.
- Metadata and faces are best-effort. Keep every optional reference API call inside
  `try/catch`; return partial DTOs with `Message` instead of failing the whole tool.
- `ModelInternal.Operation.GetReferenceModelObjectFaces(Identifier)` is used because the public
  `ReferenceModelObject` surface has no geometry. It is version-sensitive and must keep a
  `// TODO(windows):` marker plus notes in `docs/tekla-api-notes.md`.
- Cap faces and custom attributes. Never return an unbounded IFC mesh through MCP.

### Conventions for FILE EXCHANGE tools

`tekla_export_parts_file`, `tekla_set_udas_from_file`, `tekla_export_reference_objects_file`.

These three are the ONLY tools where the server itself reads or writes a data file at a path the
agent chooses — `ScriptPolicy` still bans `File`/`Path`/`Directory` outright, and that stays true.
Other file touches are fixed server-owned locations (`tekla_report_gap`'s
`%LOCALAPPDATA%\TeklaMcp\capability-requests.log`, the API-reference cache) or are done by Tekla
itself (`tekla_export_drawings_pdf` hands its absolute output path to Tekla's print, NOT through
`FilePathPolicy`; model save/open). A new tool that writes where the agent says goes through
`FilePathPolicy`. They exist because a geometric reconciliation moves
tens of MB of geometry out and tens of thousands of GUID→UDA pairs back in, and a tool response is
an LLM context: the wrong pipe by one to two orders of magnitude.

- **Data never enters the response.** Export results carry counters, a path and a field list —
  never rows. If you are tempted to add a `sample` of exported rows, don't; that is what
  `tekla_find_objects` is for.
- **Every path goes through `FilePathPolicy`** (`src/TeklaMcp.Core/FileExchange/`): absolute only,
  extension allow-list, root allow-list from `TEKLA_MCP_FILE_ROOT` (set = replaces the defaults,
  so an operator can narrow it), no UNC/device paths, no wildcards, no reserved DOS names,
  canonicalized BEFORE the root check. Reserved names are checked on the RAW name because
  `Path.GetFullPath("…\CON.csv")` rewrites it to `\\.\CON`. Extend the tests in
  `FilePathPolicyTests` in the same change as any relaxation.
- **The file mechanics are shared, the scan is not.** `ExportRunner` / `UdaImportRunner` own
  format, paging, the status sidecar and the result DTO; backends supply only a row source
  (`Func<skip, ExportScanState, IEnumerable<DataRow>>`) or an `IUdaImportTarget`. That is why a
  Mock file and a live file have identical layout — keep it that way rather than duplicating
  writer logic per backend.
- **Cursors mean SOURCE offsets, not matched rows.** The skip happens before filtering, exactly as
  in `AggregateBy`, so pages cover disjoint slices. Continuation calls must pass `append=true`;
  the CSV header is written only when the file is empty.
- **`<path>.status.json` is written on every call.** MCP clients abort at ~60 s and the reply is
  lost even when the server finished; the sidecar is the only way the user can tell a completed
  export from a half-written one. It is best-effort and must never fail an otherwise good export.
- **Pay only for requested fields.** `ExportFieldSet` carries `NeedsSolid` / `NeedsCog` /
  `NeedsCoordSystem` / `NeedsContour` / `NeedsReportProperties` precisely so a default export does
  not call `GetSolid()`. `uda:*` is rejected on purpose — it means `GetAllUserProperties` per
  object (~100 ms live, hours over a full model); the error points at `tekla_discover_udas`.
- **Writes keep the preview contract** (`apply=false` default) AND add a second guard:
  `overwriteNonEmpty=false` leaves a UDA that already holds a value alone, because those values
  are usually a human's decision. `skippedNonEmpty` reports how many. `unchanged` (already
  correct) and `skip-non-empty` (refused) are different outcomes — do not merge them.
- **Geometry honesty carries into the file.** The reference export writes `aabbSource` per row and
  rewrites `tekla-faces` to `tekla-faces-truncated` when the face budget was hit, because an AABB
  spanned by a truncated face set is too SMALL. Never widen that to a plain "exact".
- **`includeFaceAabb` stays OFF by default — do not flip it back.** Running
  `GetReferenceModelObjectFaces` across an IFC overlay wedged live Tekla (2021, model 3155,
  2026-08-13): one core pegged, UI unresponsive, and killing the MCP client did NOT release it.
  See the field report in `docs/tekla-api-notes.md`. The same caution applies to any new
  per-object internal `Operation.*` call over a whole model.
- **`maxSeconds` bounds many slow objects, never one wedged call.** It is checked between rows in
  `ExportRunner`, so a single Open API call that does not return is still unbounded — the Open API
  has no cancellation. Never describe it as protection against a hang, in code comments or in tool
  descriptions.

`ObjectQuery.UdaIsEmpty` was added for the same workflow ("parts whose USER_FIELD_1 is still
blank") — `UdaEquals` ignores a blank expected value and could never express it. Both backends
implement it in their `MatchesUda`/`MatchesFilters`; wire it into new filter tools too.

New files: `src/TeklaMcp.Core/FileExchange/*`, `src/TeklaMcp.Core/Models/FileExchangeModels.cs`,
`src/TeklaMcp.Mock/MockTeklaModelService.FileExchange.cs`,
`src/TeklaMcp.Tekla/TeklaFileExchangeService.cs`,
`src/TeklaMcp.Server/Tools/ModelFileExchangeTools.cs`.

### Conventions for DRAWING tools

The drawing layer is experimental (new in v0.7.0) and has had limited live-model testing —
treat field reports as expected, harden defensively (e.g. enumeration must survive objects
that fault during remoting materialization), and keep user-facing docs marked accordingly.

Drawing tools use `Tekla.Structures.Drawing` but follow the same boundary rules as model tools:
no Tekla types outside `src/TeklaMcp.Tekla/`, flat Core DTOs, and believable stateful Mock
behavior.

- **Keep the package version-locked.** `Tekla.Structures.Drawing` uses the same
  `$(TeklaVersion)` as Model/Core in every per-version build. New drawing calls must compile
  against the common 2021 baseline and remain wrapped with graceful errors/TODO(windows) notes
  until live-verified.
- **Respect editor preconditions.** List/status operations may run with no drawing open;
  view/object/content operations require an active drawing; creation/AutoDrawing require the
  editor closed; update/delete/print cannot target the active drawing. Never close or replace an
  active drawing implicitly.
- **Preview every persistent mutation.** Drawing/view/object creates, edits, lifecycle changes,
  issue/update/delete/place/print operations take `apply=false` by default, cap batches, and
  return `DrawingWriteResult`. `save=false` on close is destructive and must stay explicit.
  `tekla_select_drawing_objects` is an immediate UI-only side effect, like model selection.
- **Commit on the correct side.** Active-drawing content uses `Drawing.CommitChanges`, not
  `Model.CommitChanges`. Attempt `MCP_ORIGIN` on created/modified drawing database objects, but
  tolerate UDA support varying by object/environment.
- **Treat DrawingInternal IDs as best-effort.**
  `DatabaseObjectExtensions.GetIdentifier` may fail or yield zero on some versions/objects.
  It supplies an object's own ID; `GetViewIdentifier` means the containing view and must not be
  used as a placed View's own identity. Drawing keys therefore need a public-property fallback;
  object/view indices are ephemeral and must never be described as durable. Prefer non-zero
  ID/ID2, and re-list after structural edits.
- **Keep coordinate spaces explicit.** `view` is target-view local, `model` is global model
  coordinates transformed through `DisplayCoordinateSystem`, and `sheet` is paper millimetres
  with the sheet target (`viewIndex=-1`, no view ID). View insertion/frame and dimension-line
  distances are paper millimetres; section depths are model millimetres. Never silently mix or
  relabel these spaces.
- **Saved settings stay declarative.** Attribute, AutoDrawing-rule, symbol-library, printer, and
  output names are passed to Tekla for environment-specific resolution; MCP code does not
  search arbitrary files to guess them.
- Keep the drawing surface split between `DrawingQueryTools.cs`, `DrawingWriteTools.cs`,
  `DrawingContentTools.cs` and the live partials `TeklaDrawingService.cs`,
  `TeklaDrawingObjectService.cs`, `TeklaDrawingContentService.cs`.

### Conventions for PART CURVE GEOMETRY (`tekla_get_part_curve_geometry`)

Issue #15: dimensioning a curved PolyBeam needs exact arcs, and nothing else exposes them — a
drawing `Part` has no geometry of its own, and `Part.GetCenterLine` is a segmented polyline.

- **Backends deliver raw pieces, Core derives everything.** The live backend
  (`TeklaCurveGeometryService.cs`) reads `PolyBeam.GetCenterLinePolycurve()` pieces, the contour
  with chamfers, `PROFILE_TYPE`, `PROFILE.DIAMETER` and `LENGTH`; bends, chord, sagitta, inner/outer
  arcs, view projection and rounding are `TeklaMcp.Core.Geometry.CurveMath`, shared with the mock
  and unit-tested. Add new derived values there, not in a backend.
- **Arcs are start → mid → end** with mid the angular midpoint and a right-handed normal —
  exactly Tekla's `Arc` semantics (verified live, see `docs/tekla-api-notes.md`).
  `CurveMath.CheckReportedArc` compares Tekla's `Radius`/`Angle`/`Length` with the points on
  every call; keep it, it is the tripwire for a per-version semantic change.
- **`arcs` are physical bends.** Tekla splits a bend at each contour point on it (`ARC_POINT` →
  two arcs); `MergeArcs` joins contiguous co-circular arcs. Dimensions use the merged bend.
- **Round sections only, from `PROFILE.DIAMETER`.** Inner/outer arcs are radius ∓ D/2 for
  `PROFILE_TYPE` RO/RU. Never size a section from `HEIGHT`/`WIDTH` — on a bent PolyBeam they are
  not profile dimensions. Non-round offsets would need the profile orientation or the solid;
  say so instead of guessing.
- **The centerline is not the solid.** Cuts, fittings and boolean parts are not in it; a
  `LENGTH` that differs by more than 0.5 mm becomes a warning. Keep that honesty.
- **One model → view transform.** The view projection uses `GlobalToViewMatrix`, the same helper
  as `coordinateSpace=model` in the drawing content tools, so points read in view space equal
  what the dimension tools compute from model points. View problems are warnings next to valid
  model geometry, never a failed call.
- Mock: the `PD168.3*6` arch PolyBeam (appended last in `BuildSampleModel` so older fixture
  ids/GUIDs stay stable) is served as two arcs split at its apex, like Tekla does.

New files: `src/TeklaMcp.Core/Geometry/CurveMath.cs`, `src/TeklaMcp.Core/Models/PartCurveGeometry.cs`,
`src/TeklaMcp.Mock/MockTeklaModelService.CurveGeometry.cs`, `src/TeklaMcp.Tekla/TeklaCurveGeometryService.cs`.

### Conventions for VISUAL tools (`tekla_capture_view`, `tekla_render_schematic`)

Agents were "blind": they found objects by indirect signs and never saw the model. Both tools return
an MCP `ImageContentBlock` (the agent sees it) plus a JSON text block, built in
`ModelVisualTools.Answer`; the image bytes travel in `RenderedImage` and are nulled out of the DTO
before it is serialized.

- **Never capture the screen.** `tekla_capture_view` copies pixels ONLY from Tekla's own view window
  with `PrintWindow(…, PW_RENDERFULLCONTENT)` (`TeklaWindowCapture`). A screen copy
  (`BitBlt`/`CopyFromScreen`) was tried once while probing and captured an overlapping Explorer
  window with the user's private file names instead of the model. Do not add it as a fallback.
- **Only on-screen views.** `PrintWindow` returns the main window's composition, so an MDI view
  hidden behind another view comes back as whatever covers it. Covered/minimized views are refused
  with the list of open views; never "bring a view forward" behind the user's back.
- **Side effects are brief and restored.** Zoom (`ViewHandler.ZoomToBoundingBox`), rotation
  (`ViewCamera`), highlight (`ModelObjectVisualization`) and numbers (`GraphicsDrawer.DrawText`,
  active view only) happen only when the agent passes targets/direction, and `restore=true` (default)
  puts the camera back and clears temporary states in a `finally`. Without targets the capture touches
  nothing. Keep that split; a new visual option must be opt-in and restorable.
- **Settle before capturing.** Tekla redraws asynchronously: `CaptureSettled` waits for two equal
  frame fingerprints that also differ from the pre-change frame (or ~1 s of no change) and warns when
  the budget runs out. Do not replace it with a fixed sleep.
- **The schematic is BETA** (user decision, 2026-09-29: "useful, but looks so-so for now"). Keep
  the BETA marker in the tool description, the server instructions and `SchematicRenderResult.Stage`
  until its picture quality is reviewed. Known weak spots: stick geometry without section widths,
  crowded overviews of dense models, the 8×8 bitmap font.
- **Backends deliver geometry, Core draws.** `GetSchematicScene` returns raw pieces (reference
  lines, contours, bolt points, solid AABB fallback capped at 300 `GetSolid` calls, grids);
  `TeklaMcp.Core.Rendering.SchematicRenderer` does projection, colours, labels and PNG encoding
  (hand-rolled rasterizer, font and encoder — Core stays dependency-free), identical for mock and
  live. GUID focus is looked up directly; context comes from `GetObjectsByBoundingBox`, never a
  whole-model walk; a `region` limits the focus too and frames the picture.
- **A view is the direction the camera LOOKS along** — `ViewProjection`, the schematic's `view` and
  the capture's `direction` share presets and the `"dx,dy,dz"` convention of
  `ViewCamera.DirectionVector` (verified live), so a camera read from a capture can be redrawn.
- **Coverage is reported.** Legend entries say `labelPlaced`; caps set `focusTruncated`/
  `contextTruncated`/`scanTruncated`; missing GUIDs are listed. Same rule as the search tools.
- **The mock never pretends.** Its capture is a schematic stand-in with a red "MOCK BACKEND" banner,
  `source="mock-schematic"`, and no side-effect flags set.
- **Grids** (shared with `tekla_list_grids`/`tekla_resolve_point`): `GridMath` parses X/Y as spacings,
  Z as absolute levels, reads real `LabelX/Y/Z` and each grid's own `GetCoordinateSystem()`; labels
  repeat across grids, so `Resolve` prefers a pair from the same grid and says when it had to choose.

New files: `src/TeklaMcp.Core/Rendering/*`, `src/TeklaMcp.Core/Geometry/GridMath.cs`,
`src/TeklaMcp.Core/Models/SchematicModels.cs`, `src/TeklaMcp.Core/Models/ViewCaptureModels.cs`,
`src/TeklaMcp.Mock/MockTeklaModelService.Visual.cs`, `src/TeklaMcp.Tekla/TeklaSchematicService.cs`,
`src/TeklaMcp.Tekla/TeklaViewCaptureService.cs`, `src/TeklaMcp.Tekla/TeklaWindowCapture.cs`,
`src/TeklaMcp.Server/Tools/ModelVisualTools.cs`.

### Conventions for the SCRIPT escape hatch (`tekla_run_csharp`)

`ModelScriptTools.cs` + `src/TeklaMcp.Scripting/` let agents run policy-checked C# scripts when no
dedicated tool exists. Rules for maintaining it:

- **`TeklaMcp.Scripting` stays Tekla-free and netstandard2.0.** It receives Tekla references from
  the caller: the net48 backend passes the Tekla assemblies LOADED in the process (authoritative —
  what the live connection binds, possibly from the GAC) plus the `Tekla.Structures*.dll` /
  `Tekla.Dialog*.dll` files in the resolver's folders (`TeklaAssemblyResolver.ProbeDirectories`:
  Drawing/Dialog/Datatype/Plugins when installed; on 2026 `bin\Net48Runtime` holds
  `Tekla.Structures.dll`); the mock passes DLL paths from `TEKLA_MCP_SCRIPT_REF_DIR`. Both go through
  `ScriptReferenceSelector`, which drops non-managed files, duplicate assembly names (the loaded
  one wins) and other Tekla years, and records every decision: `ScriptResult.ReferenceSummary`
  always, `ScriptResult.References` on compile-only checks and compile failures. Never make the
  folder the ONLY source again — with the folder at Tekla 2021's `nt\bin` (seven unrelated
  `Tekla.Structures.*.dll`; the API lives in `nt\bin\plugins`) every script compiled without
  `Tekla.Structures.Model` while connecting worked from the GAC (DEV-005 field report). The
  globs also match native DLLs (Tekla 2025 ships `Tekla.Structures.Native.DbvDatabase.dll`),
  and `MetadataReference.CreateFromFile` accepts them silently — every compile then fails with
  `CS0009` (issue #15). `ScriptEngine.BuildReferences` therefore admits only PE images with
  managed metadata and an assembly manifest (`IsReferenceableAssembly`); keep every reference
  path going through it, and prefer that check over a name blocklist. Roslyn stays on the 4.9.x line
  (last to target netstandard2.0). Do not globally import `Tekla.Structures.Drawing`: its
  `Part`/`View` names collide with Model/UI types; scripts use an explicit alias instead.
- **The pipeline is policy → compile → execute** (`ScriptResult.Stage`). `tekla_check_csharp`
  runs the identical policy + compiler path with `compileOnly=true`, permits mutation syntax
  because it never executes, and returns the source SHA-256 + detected mutating members for
  approval. `tekla_run_csharp` binds a run to that approval: `ScriptApprovalGate` (tool layer,
  before the backend) requires `expectedSha256` when `allowMutations=true` and rejects any
  mismatch, read-only runs included — keep it in front of every execution path. The mock NEVER executes; only the net48 backend runs scripts. `Executed` and
  `ExecutionAttempted` become true as soon as the live worker starts, including failure/timeout.
  Never throw — report failures and partial-mutation warnings in the DTO.
- **Safety gates live in `ScriptPolicy`** (syntax-level whitelist/banlist + mutation detection).
  A name belongs in `MutatingMembers` only if a FALSE POSITIVE is unlikely: bare `Split` was
  removed because it only ever fired on `string.Split` while the real target, `Operation.Split`,
  is already caught by the `Operation` token — do not re-add it. When a banned identifier is also
  DECLARED by the script (a local function named `Process`), the violation message says "rename
  your declaration" rather than accusing the script of process access; the ban itself still
  applies, because a syntax-only check cannot tell the two apart once the name is in scope.
  If you extend the script surface (new imports, new globals), extend the policy AND the tests in
  `tests/TeklaMcp.Tests/ScriptPolicyTests.cs` in the same change. Mutations require
  `allowMutations=true`; the tool description obliges the agent to show the user the script and
  get explicit approval first, and to keep changes traceable (`MCP_ORIGIN` UDA) — keep that
  contract wording intact.
- **Never let scripts touch stdout** — `Console` is banned by policy; script output goes through
  private host-owned `ScriptGlobals.Print` storage (line + total-character caps, snapshot only)
  and `SafeJson` (capped, defensive, always valid JSON). Return-value serialization stays inside
  the timeout worker because Tekla proxy property access can block.
- **Every SafeJson cap is reported out of band.** Valid JSON hides a cut: a 100-item list parses
  like a complete one (a field report accepted 100 of 141 rows as a full audit). Any cap that
  drops data must bump a counter in `SafeJsonReport`, which surfaces as
  `ScriptResult.ReturnValueTruncated` / `ReturnValueTruncation`. If you add a cap, report it
  there and test it in `SafeJsonTests`; never rely on an in-band marker alone.
- **`tekla_search_api`/`tekla_get_api_doc`** read a Markdown reference produced by
  `ApiReferenceGenerator` (Scripting; `tools/TeklaApiDoc` is only a CLI over it). Lookup order in
  `ApiReference.Resolve`: `TEKLA_MCP_API_REF_DIR` → the cache for the backend's
  `GetApiReferenceSource()` (exact Tekla build, `%LOCALAPPDATA%\TeklaMcp\api-reference\<key>-r<FormatVersion>`)
  → generate it in the background (metadata-only `MetadataLoadContext`, temp folder + rename, a
  call waits ≤ 40 s, or not at all when a repository copy can be served meanwhile) → the repository
  `reference/tekla-api`. A failed generation is reported, not retried per call. Bump
  `ApiReferenceGenerator.FormatVersion` when the output changes (r3: a 2026 reference cached by
  v0.8.0 lacks `Tekla.Structures`). `GetApiReferenceSource()` looks the assemblies up across
  `TeklaAssemblyResolver.ProbeDirectories`, must
  never touch Tekla types or remoting (it reads files only), and `CoreAssemblyNames` stays short —
  generation has to fit the ~60 s client budget. The generated reference stays on the machine: it
  is Trimble content. They must degrade to a "how to generate" hint, never an error.
- **A recurring script is a roadmap signal**: promote it to a first-class tool (interface + both
  backends + dedicated tool) and keep `tekla_report_gap` pointing that way.

### Gap-reporting policy (for agents USING the server)

The server sets MCP `ServerInstructions` (in `Program.cs`) giving connecting agents an escalation
ladder: dedicated tools first; then the sanctioned script escape hatch (`tekla_search_api` →
`tekla_run_csharp`) for one-off needs; and `tekla_report_gap` (`ModelMetaTools.cs`) for anything
missing or recurring — it returns a ready-to-file issue draft and logs the request locally. The
server never files issues itself (no credentials). When you ADD tools that close such gaps, keep
this affordance working; external ad-hoc automation and fabricated data remain forbidden.

---

## 5. Building & testing

**Mock backend (no Tekla):**
```bash
dotnet build src/TeklaMcp.Server
dotnet run   --project src/TeklaMcp.Server
npx @modelcontextprotocol/inspector dotnet run --project src/TeklaMcp.Server
```

**Windows + Tekla** (pass the `TeklaVersion` matching the installed Tekla — NuGet package
version, see the table in `docs/releasing.md`; the default `2021.0.0` build only talks to
Tekla 2021):
```powershell
dotnet build TeklaMcp.sln -c Release -p:TeklaVersion=2023.0.1
dotnet run --no-build --project src/TeklaMcp.Server -f net48 -c Release   # Tekla must be open
```
`--no-build` matters: a plain `dotnet run` rebuilds first WITHOUT your `-p:TeklaVersion`, i.e.
against the 2021 default, and the resolver then refuses the running Tekla as a wrong build.

The net48 projects also COMPILE on macOS/Linux (`dotnet build TeklaMcp.sln`) — useful for
checking all `-p:TeklaVersion` values build, they just can't run there.

**Tests (mock-only, any OS):**
```bash
dotnet test tests/TeklaMcp.Tests
```
Add tests when you touch `Core`/`Mock`/`Scripting` logic (keep the project `net8.0`, mock-only).

**MCP stdio smoke (any OS, after a Release build of the server):**
```bash
dotnet build src/TeklaMcp.Server -c Release
dotnet run --project tests/TeklaMcp.Smoke -c Release -- \
  src/TeklaMcp.Server/bin/Release/net8.0/TeklaMcp.Server.dll 116
```
It is what CI and the release workflow run after the unit tests, and it asserts EXACT values:

- **tool count** = the second argument, hard-coded in `.github/workflows/ci.yml` AND
  `.github/workflows/release.yml`. Adding/removing a tool means changing both (and the README
  count) in the same change.
- **server version** = the string hard-coded in `tests/TeklaMcp.Smoke/Program.cs`. A version bump
  touches three places: `<Version>` in `Directory.Build.props`, the smoke's version check, and
  the `?? "x.y.z"` fallback in `src/TeklaMcp.Server/Program.cs`.
- plus: server name `tekla-mcp`, non-empty server instructions, no duplicate names, a few
  required tools with descriptions/schemas, `tekla_get_connection_info` reporting the server
  identity, and `tekla_run_csharp` refusing a mutating run without `expectedSha256`.

**Compile check of every Tekla version (any OS):** CI builds `TeklaMcp.sln` once per
`TeklaVersion` in the matrix (2021.0.0, 2022.0.10715, 2023.0.1, 2024.0.4, 2025.0.0, 2026.0.3 —
table in `docs/releasing.md`). Before a PR that touches `src/TeklaMcp.Tekla/`, run
`dotnet build TeklaMcp.sln -c Release -p:TeklaVersion=<v>` for each; the build has 0 warnings,
keep it that way.

All executables and the test project set `<RollForward>Major</RollForward>`, so a machine with
only a newer .NET runtime (e.g. .NET 10) runs the `net8.0` outputs without extra settings.

---

## 6. Reference docs

- **Verify Tekla API calls against the local reference before writing them.** Generate a
  grep-friendly Markdown reference with `tools/TeklaApiDoc` (a CLI over
  `ApiReferenceGenerator`; reflects metadata-only over the Tekla assemblies, any OS) into
  `reference/tekla-api/` (git-ignored — Trimble content), then
  `grep -rl "<Member>" reference/tekla-api`. Faster and more reliable than the web docs for
  confirming signatures/overloads/enums. See [tools/TeklaApiDoc/README.md](tools/TeklaApiDoc/README.md).
  (The live server builds the same reference by itself for `tekla_search_api`, cached under
  `%LOCALAPPDATA%\TeklaMcp\api-reference` — see the script conventions in §4.)
- Tekla Open API 2026: https://developer.tekla.com/doc/tekla-structures/2026
- MCP C# SDK: https://github.com/modelcontextprotocol/csharp-sdk
- Local: [docs/architecture.md](docs/architecture.md), [docs/tekla-api-notes.md](docs/tekla-api-notes.md)
- Deferred work with its findings (reconnect after a Tekla restart, instance selection, write
  outcomes, component-development read tools): [docs/backlog.md](docs/backlog.md) — read it
  before starting any of those, and move an item out once it is done.
