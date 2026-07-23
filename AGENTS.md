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
(real Tekla backend on Windows). The Tekla integration project builds only on Windows.

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
   `.github/workflows/release.yml` and `ci.yml`.

---

## 3. Code map

| Path | TFM | Role |
|---|---|---|
| `src/TeklaMcp.Core/` | netstandard2.0 | `ITeklaModelService` + DTOs |
| `src/TeklaMcp.Mock/` | netstandard2.0 | mock backend (synthetic frame) |
| `src/TeklaMcp.Scripting/` | netstandard2.0 | Roslyn script escape hatch (policy, engine, SafeJson) + API reference search. No Tekla references — Tekla assemblies are supplied at runtime |
| `src/TeklaMcp.Tekla/` | net48 | real Tekla Open API backend (Windows) |
| `src/TeklaMcp.Server/` | net8.0 (+net48 on Windows) | MCP host + `tekla_*` tools |
| `tests/TeklaMcp.Tests/` | net8.0 | xUnit tests (mock-only, no Tekla) |

Multi-targeting: in `TeklaMcp.Server.csproj` the `net48` TFM is dropped when
`$(OS) != Windows_NT`, so non-Windows builds never pull in the Tekla project. The
`#if NET48` block in `Program.cs` selects the real backend only in that build.

---

## 4. How to add a new capability

1. Add the method to `ITeklaModelService` (`src/TeklaMcp.Core/ITeklaModelService.cs`),
   with XML docs describing behavior and the "not found" contract.
2. Implement it in `MockTeklaModelService` (make the synthetic result believable).
3. Implement it in `TeklaModelService` using the Tekla Open API.
4. Expose it as a tool in `src/TeklaMcp.Server/Tools/` with `[McpServerTool(Name = "tekla_...")]`
   and a clear `[Description]`. First parameter is `ITeklaModelService model` (DI-injected,
   not shown to the LLM). Remaining parameters become tool inputs — give each a `[Description]`.
5. Update the tool table in [README.md](README.md).

Tool naming: `tekla_<verb>_<noun>`, snake_case. **Do not add write/mutating tools**
without an explicit request and a safety gate (see existing UDA tools for the preview pattern).

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
  don't count via `FindObjects(...).Count`.
- When a query filters by a known type, `EnumerateSource` pre-filters with
  `GetAllObjectsWithType` (see `TypeEnumMap`) — extend the map when you add type-heavy tools.

### Conventions for WRITE tools (create / edit / delete)

Write tools are now in scope (explicitly requested, with safety gates). Rules:

- **Preview-by-default.** Every mutating tool takes `apply` (default false). With `apply=false`
  NOTHING is written — return a `WriteResult` plan (counts + preview). Only `apply=true` commits.
- **Tag origin.** Backends stamp created/modified objects with the `MCP_ORIGIN` UDA so agent
  output is findable and reversible. Keep this behavior.
- **Cap batches.** Mutating-by-filter tools must pass a `limit` (default 200) for safety.
- **Small service surface.** Keep backend write methods primitive and batch-oriented:
  `CreateParts`, `ModifyParts`, `DeleteObjects`, and `CreateConnections`. **Generators, fixers
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
- Shared parsing/query helpers for write tools live in `ToolHelpers.cs`.

New tool files: `ModelGeometryTools.cs`, `ModelWriteTools.cs`, `ModelGeneratorTools.cs`,
`ModelReferenceTools.cs`.
The live-Tekla write path (`CreateParts`/`ModifyParts`/`DeleteObjects`/`CreateConnections`,
grid parsing) is the most under-verified code in the repo — see `docs/tekla-api-notes.md`.

### Conventions for reference-model / IFC geometry

- Reference objects often have `Identifier.GUID == Guid.Empty`; use their integer `Id` as the
  session-local address for `GetReferenceGeometry`.
- Metadata and faces are best-effort. Keep every optional reference API call inside
  `try/catch`; return partial DTOs with `Message` instead of failing the whole tool.
- `ModelInternal.Operation.GetReferenceModelObjectFaces(Identifier)` is used because the public
  `ReferenceModelObject` surface has no geometry. It is version-sensitive and must keep a
  `// TODO(windows):` marker plus notes in `docs/tekla-api-notes.md`.
- Cap faces and custom attributes. Never return an unbounded IFC mesh through MCP.

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

### Conventions for the SCRIPT escape hatch (`tekla_run_csharp`)

`ModelScriptTools.cs` + `src/TeklaMcp.Scripting/` let agents run policy-checked C# scripts when no
dedicated tool exists. Rules for maintaining it:

- **`TeklaMcp.Scripting` stays Tekla-free and netstandard2.0.** It receives Tekla references from
  the caller: the net48 backend dynamically passes every managed `Tekla.Structures*.dll` file
  from `TeklaAssemblyResolver.BinDir` (Drawing/Dialog/Datatype/Plugins included when installed);
  the mock passes DLL paths from `TEKLA_MCP_SCRIPT_REF_DIR`. Roslyn stays on the 4.9.x line
  (last to target netstandard2.0). Do not globally import `Tekla.Structures.Drawing`: its
  `Part`/`View` names collide with Model/UI types; scripts use an explicit alias instead.
- **The pipeline is policy → compile → execute** (`ScriptResult.Stage`). `tekla_check_csharp`
  runs the identical policy + compiler path with `compileOnly=true`, permits mutation syntax
  because it never executes, and returns the source SHA-256 + detected mutating members for
  approval. The mock NEVER executes; only the net48 backend runs scripts. `Executed` and
  `ExecutionAttempted` become true as soon as the live worker starts, including failure/timeout.
  Never throw — report failures and partial-mutation warnings in the DTO.
- **Safety gates live in `ScriptPolicy`** (syntax-level whitelist/banlist + mutation detection).
  If you extend the script surface (new imports, new globals), extend the policy AND the tests in
  `tests/TeklaMcp.Tests/ScriptPolicyTests.cs` in the same change. Mutations require
  `allowMutations=true`; the tool description obliges the agent to show the user the script and
  get explicit approval first, and to keep changes traceable (`MCP_ORIGIN` UDA) — keep that
  contract wording intact.
- **Never let scripts touch stdout** — `Console` is banned by policy; script output goes through
  private host-owned `ScriptGlobals.Print` storage (line + total-character caps, snapshot only)
  and `SafeJson` (capped, defensive, always valid JSON). Return-value serialization stays inside
  the timeout worker because Tekla proxy property access can block.
- **`tekla_search_api`/`tekla_get_api_doc`** read the `tools/TeklaApiDoc` output (git-ignored) found
  via `TEKLA_MCP_API_REF_DIR` or by probing for `reference/tekla-api`.
  `tekla_get_api_reference_status` exposes availability/setup explicitly. They must degrade to
  a "how to generate" hint, never an error.
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
dotnet run --project src/TeklaMcp.Server -f net48 -c Release   # Tekla must be open
```

The net48 projects also COMPILE on macOS/Linux (`dotnet build TeklaMcp.sln`) — useful for
checking all `-p:TeklaVersion` values build, they just can't run there.

**Tests (mock-only, any OS):**
```bash
dotnet test tests/TeklaMcp.Tests
```
Add tests when you touch `Core`/`Mock`/`Scripting` logic (keep the project `net8.0`, mock-only).

---

## 6. Reference docs

- **Verify Tekla API calls against the local reference before writing them.** Generate a
  grep-friendly Markdown reference with `tools/TeklaApiDoc` (reflects over the Tekla assemblies,
  any OS) into `reference/tekla-api/` (git-ignored — Trimble content), then
  `grep -rl "<Member>" reference/tekla-api`. Faster and more reliable than the web docs for
  confirming signatures/overloads/enums. See [tools/TeklaApiDoc/README.md](tools/TeklaApiDoc/README.md).
- Tekla Open API 2026: https://developer.tekla.com/doc/tekla-structures/2026
- MCP C# SDK: https://github.com/modelcontextprotocol/csharp-sdk
- Local: [docs/architecture.md](docs/architecture.md), [docs/tekla-api-notes.md](docs/tekla-api-notes.md)
