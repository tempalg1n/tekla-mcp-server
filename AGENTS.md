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

---

## 3. Code map

| Path | TFM | Role |
|---|---|---|
| `src/TeklaMcp.Core/` | netstandard2.0 | `ITeklaModelService` + DTOs |
| `src/TeklaMcp.Mock/` | netstandard2.0 | mock backend (synthetic frame) |
| `src/TeklaMcp.Tekla/` | net48 | real Tekla Open API backend (Windows) |
| `src/TeklaMcp.Server/` | net8.0 (+net48 on Windows) | MCP host + `tekla_*` tools |

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

### Conventions for WRITE tools (create / edit / delete)

Write tools are now in scope (explicitly requested, with safety gates). Rules:

- **Preview-by-default.** Every mutating tool takes `apply` (default false). With `apply=false`
  NOTHING is written — return a `WriteResult` plan (counts + preview). Only `apply=true` commits.
- **Tag origin.** Backends stamp created/modified objects with the `MCP_ORIGIN` UDA so agent
  output is findable and reversible. Keep this behavior.
- **Cap batches.** Mutating-by-filter tools must pass a `limit` (default 200) for safety.
- **Small service surface.** The interface has only five write methods: `GetGrids`,
  `ResolvePoint`, `CreateParts`, `ModifyParts`, `DeleteObjects`. **Generators and fixers
  (`tekla_generate_frame`, `tekla_straighten_columns`, `tekla_fix_column_handles`, …) live in
  the TOOL layer** and compose those five — do NOT add per-generator interface methods.
- **Global coordinates.** Tool inputs are global model coordinates (mm). The Tekla backend forces
  the global `TransformationPlane` around mutations (`WorkPlaneHandler`); preserve that.
- Shared parsing/query helpers for write tools live in `ToolHelpers.cs`.

New tool files: `ModelGeometryTools.cs`, `ModelWriteTools.cs`, `ModelGeneratorTools.cs`.
The live-Tekla write path (`CreateParts`/`ModifyParts`/`DeleteObjects`, grid parsing) is the
most under-verified code in the repo — see `docs/tekla-api-notes.md`.

### Gap-reporting policy (for agents USING the server)

The server sets MCP `ServerInstructions` (in `Program.cs`) telling connecting agents NOT to script
around missing functionality, but to call `tekla_report_gap` (`ModelMetaTools.cs`) — which returns a
ready-to-file issue draft and logs the request locally. The server never files issues itself (no
credentials). When you ADD tools that close such gaps, keep this affordance working; when you find
yourself wanting a workaround script, that's the signal a tool is missing.

---

## 5. Building & testing

**Mock backend (no Tekla):**
```bash
dotnet build src/TeklaMcp.Server
dotnet run   --project src/TeklaMcp.Server
npx @modelcontextprotocol/inspector dotnet run --project src/TeklaMcp.Server
```

**Windows + Tekla:**
```powershell
dotnet build TeklaMcp.sln -c Release
dotnet run --project src/TeklaMcp.Server -f net48 -c Release   # Tekla must be open
```

There are no automated tests yet. If you add logic to `Core`/`Mock`, a small xUnit
project under `tests/` is welcome (keep it `net8.0`, mock-only).

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
