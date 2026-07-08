# Tekla Open API notes

Technical reference for Tekla Open API usage in this project.

Official documentation: https://developer.tekla.com/doc/tekla-structures/2026/tekla-structures-64304

## Local API reference (preferred for verifying signatures)

Generate a grep-friendly Markdown reference of the Tekla Open API with **`tools/TeklaApiDoc`**
(see its [README](../tools/TeklaApiDoc/README.md)). It reflects over the Tekla assemblies
(metadata-only, any OS) and emits one `*.md` per type — full signatures + XML-doc summaries —
into `reference/tekla-api/` (git-ignored; Trimble content, do not publish).

```bash
# one-time: fetch the Tekla DLLs (see tools/TeklaApiDoc/README.md), then:
dotnet run --project tools/TeklaApiDoc -c Release -- \
  --dll-dir /tmp/tekla.structures.model/lib/net40 \
  --dll-dir /tmp/tekla.structures/lib/net40 --out reference/tekla-api
grep -rl "GetReportProperty" reference/tekla-api    # find declaring types
```

This verifies the API **surface** (signatures, overloads, enum values) offline — use it before
writing new Tekla calls. Runtime behavior (units, coordinate effects, grid string format) still
needs a live model.

## Tekla 2026 API facts

- API assemblies target **.NET Framework 4.8 / .NET Standard 2.0**.
- **x64 only** for extensions (new in Tekla 2026).
- **COM support removed** from Open API assemblies; assemblies are **no longer GAC-registered**.
- Gradual migration from .NET Framework to modern .NET started in 2024; this project
  currently targets **`net48`** for live Tekla integration.
- NuGet packages: `Tekla.Structures`, `Tekla.Structures.Model`, `Tekla.Structures.Plugins`,
  etc., versions `2021.0.0` .. `2026.0.x`. The build compiles against a baseline (default
  `2021.0.0`) but works with any installed version at runtime — see "Tekla version compatibility".

## Connection model

The server is a **standalone process** that connects to an **already running** Tekla instance
with an open model. Connection is established via `new Tekla.Structures.Model.Model()` and
checked with `model.GetConnectionStatus()`. This is not an in-process Tekla plugin (that
would use `Tekla.Structures.Plugins`).

## Tekla version compatibility

The Open API DLLs talk to the running Tekla over a **version-locked protocol**, so the running
DLLs must match the running Tekla version. The live build solves this with **one universal build**
(no per-version artifacts):

- `TeklaMcp.Tekla` compiles against a **baseline** API (`TeklaVersion`, default `2021.0.0` — the
  lowest supported version) so the code never uses members absent from older versions.
- It does **not** ship the Tekla DLLs (`ExcludeAssets="runtime"`). At runtime,
  `TeklaAssemblyResolver` (registered in `Program.cs` for the net48 build) handles
  `AppDomain.AssemblyResolve` and loads `Tekla.*` (and their dependency closure) from the
  installed Tekla's `bin`.

So `dotnet build TeklaMcp.sln -c Release` produces one EXE that works with any installed Tekla
(2021+); it auto-binds to whatever version is running. (`net8.0` mock builds never reference Tekla.)

**Locating the Tekla `bin`** (in order): the `TEKLA_BIN_DIR` env var → the running
`TeklaStructures.exe` process's folder (best — matches the open instance) → the Windows registry
(`SOFTWARE\Tekla\Structures\<version>`). If none is found, connection fails with a clear message.

**Keeping the GAC out of the picture.** `AppDomain.AssemblyResolve` only fires when the normal
bind FAILS — and if the baseline Tekla version (e.g. 2021) happens to sit in the GAC, .NET
Framework binds to it silently and the resolver never runs, so the server speaks the wrong
protocol version to the running Tekla (issue #7). `src/TeklaMcp.Server/App.config` therefore
redirects `Tekla.Structures` / `Tekla.Structures.Model` to an unreachable version, forcing every
bind through the resolver (which uses `Assembly.LoadFile` — no binding policy re-applied).

**Remoting channel name.** The Open API client connects to a named pipe
`Tekla.Structures.Model-{app}:{apiVersion}`; the baked-in default has an empty `{app}` suffix,
but some Tekla setups publish e.g. `Tekla.Structures.Model-Console:2023.0.0.0` (issue #7).
`TeklaRemotingChannel.Align()` (called before the first `Model()`) probes the machine's named
pipes and patches the internal `Remoter.ChannelName` to the published name when they disagree.
Override with the `TEKLA_MCP_CHANNEL` env var. "Not connected" errors now include the client
channel, loaded API version/path, and the published `Tekla.Structures.Model-*` pipes.

**Build overrides** (rarely needed): `-p:TeklaVersion=2024.0.0` (a different baseline from NuGet;
exact strings at https://api.nuget.org/v3-flatcontainer/tekla.structures.model/index.json) or
`-p:TeklaBinDir="...\bin"` (compile against a local install, e.g. a version not on NuGet). Neither
bundles the DLLs — the runtime resolver still supplies them.

## APIs used in `TeklaModelService.cs`

| Area | API | Status |
|---|---|---|
| Connection | `new TSM.Model()` + `GetConnectionStatus()` | Verified (Tekla 2023) |
| Model info | `model.GetInfo()` → `ModelInfo.ModelName`, `ModelPath` | Verified |
| Enumeration | `GetModelObjectSelector().GetAllObjects()` | Verified |
| Parts | Cast `mo is TSM.Part`; read `Name`, `Class`, `Profile`, `Material`, `Finish` | Verified |
| Identifiers | `part.Identifier.ID`, `part.Identifier.GUID` | Verified |
| Report props | `GetReportProperty("WEIGHT"`, `"LENGTH"`, `"ASSEMBLY_POS"`, …) | Verified; confirm units per template |
| Lookup by GUID | `new Identifier(guid)` + `SelectModelObject(identifier)` | Verified |
| UI selection read | `Model.UI.ModelObjectSelector().GetSelectedObjects()` | Verified |
| UI selection write | `ModelObjectSelector.Select(ArrayList)` | Verified |
| UDA read | `GetUserProperty(name, ref …)` | Implemented; verify on your template |
| UDA write | `SetUserProperty` + `Modify()` | Implemented; verify on your template |
| Scope = selection | `EnumerateSource` switches scan to `Model.UI.ModelObjectSelector().GetSelectedObjects()` when `ObjectQuery.UseSelection` | Implemented; verify selection scan |
| Generic property read | `tekla_get_properties` → `TryGetAttributeValue` (report props + UDA + built-ins) | Implemented; verify report-property names |
| Assembly grouping | `ASSEMBLY_POS` report property as assembly mark (no `GetAssembly()` in hot path) | Verify `ASSEMBLY_POS` is populated after numbering |
| Create beam | `new Beam(Point, Point)` + `Profile/Material/Class/Name` + `Insert()` | ✅ Signatures verified via reference (`Beam(Point, Point)`, `Boolean Insert()`) |
| Create plate | `new ContourPlate()` + `AddContourPoint(new ContourPoint(Point, null))` + `Insert()` | ✅ Verified (`AddContourPoint(ContourPoint)`, `ContourPoint(Point P, Chamfer C)`) |
| Modify part | set `Profile.ProfileString`/`Material.MaterialString`/`Class`/`Name`, `Beam.StartPoint/EndPoint`, then `Modify()` | ✅ Verified (`StartPoint/EndPoint { get; set; }`, `Modify()`) |
| Swap handles | swap `Beam.StartPoint` ↔ `Beam.EndPoint` then `Modify()` | ✅ Signatures verified |
| Delete | `ModelObject.Delete()` | ✅ Verified (`Boolean Delete()`) |
| Commit | `Model.CommitChanges()` once after a batch | ✅ Verified (`Boolean CommitChanges()`) |
| Coordinate system | `Model.GetWorkPlaneHandler()` + `SetCurrentTransformationPlane(new TransformationPlane())` to force global before mutating, restore after | ✅ Signatures verified (`TransformationPlane()` ctor, `Get/SetCurrentTransformationPlane`); ⚠️ **runtime: confirm placement on model** |
| Origin tag | `SetUserProperty("MCP_ORIGIN", …)` on created/modified objects | ✅ `SetUserProperty(String, String)` verified; confirm persistence after commit |
| Grids | `GetAllObjectsWithType(GRID)` → `Grid.CoordinateX/CoordinateY` strings; labels GENERATED by convention (X=1,2,3; Y=А,Б,В) | ✅ `CoordinateX/Y` (String) verified; ⚠️ **runtime: coord string absolute vs relative + REAL labels (incl. Cyrillic)** |

### Useful report properties

`WEIGHT`, `WEIGHT_NET`, `LENGTH`, `HEIGHT`, `WIDTH`, `AREA`, `VOLUME`, `PROFILE`, `MATERIAL`,
`CLASS`, `NAME`, `ASSEMBLY_POS`, `PART_POS`, `PHASE`. Full list in Tekla documentation
(Template/Report properties section).

## Windows build environment

- Windows x64; Tekla Structures installed and running with a model open.
- .NET SDK 8+ and .NET Framework 4.8 Developer Pack.
- `Tekla.Structures.*` NuGet versions should match the installed Tekla version, or use
  local `<Reference>` with `<HintPath>` to Tekla installation DLLs.

## MCP SDK on .NET Framework 4.8

The MCP C# SDK targets `netstandard2.0` + `net8.0`. It should work on `net48`, but
transitive dependency conflicts (`System.Text.Json`, `Microsoft.Extensions.*`) may require
binding redirects (`AutoGenerateBindingRedirects` is enabled in the server project).

If the single-process `net48` build fails to start, consider the two-process fallback
described in [architecture.md](architecture.md).

## Roslyn scripting on .NET Framework 4.8 (tekla_run_csharp)

The script escape hatch uses `Microsoft.CodeAnalysis.CSharp.Scripting` 4.9.2 (the last line
targeting `netstandard2.0`, so one package serves both server builds).

**Verified on live Tekla 2023 (2026-07-08)**: read-only scripts compile and execute against a
real ~400k-object model; `Print(...)` + JSON return value work; results match the dedicated
tools (e.g. beam count 4367 == `tekla_count_objects`); policy blocks (`Console`, `System.IO`,
`#r`) and compile errors (CS1061 + guidance) behave as designed. Also verified on macOS against
the 2023 NuGet DLLs: policy → compile pipeline, all default imports resolve (mock backend,
`TEKLA_MCP_SCRIPT_REF_DIR`).

### Assembly loading interplay (important)

`TeklaAssemblyResolver` **byte-loads** the Tekla assemblies (`Assembly.Load(byte[])`) — the
`LoadFile`/binding-redirect scheme stack-overflowed on live Tekla (fixed in cbe716b). Two
consequences to keep in mind:

- **Byte-loaded assemblies have an empty `Assembly.Location`.** Roslyn metadata references for
  scripts must come from the DLL **files** in `TeklaAssemblyResolver.BinDir`, not from
  `typeof(...).Assembly` — see `TeklaModelService.BuildScriptReferences`.
- **The GAC must lose every Tekla bind.** Reproduced live (2026-07-08): with the redirects
  removed, a host with stale Tekla 2021 in the GAC silently bound 2021 against a running
  Tekla 2023 — scripts kept working (they compile/bind against BinDir 2023) while every
  dedicated tool failed on the remoting channel version. The 2999.9.9.9 redirects in
  `App.config` are therefore restored — they are safe with the byte-loading resolver (cache +
  preload + re-entrancy guard turn the redirected bind into a single AssemblyResolve hop;
  worst case is a bind error, not the old LoadFile StackOverflow). `EnsureTeklaReady` keeps a
  stderr tripwire for the case where the redirects were stripped from the deployed config.

**Still TODO(windows):**

- Re-verify dedicated tools on the GAC-polluted machine after restoring the redirects
  (rebuild required — the redirects live in `TeklaMcp.Server.exe.config`).
- Timeout abort uses `Thread.Abort` (supported on net48, no-op catch on net8) — verify an
  aborted script doesn't wedge the Tekla remoting channel.
- The mutation path (`allowMutations=true`) has not been run against a live model.
- "Server started before Tekla" flow: `Align()` retries until Tekla publishes its pipes, and
  the resolver re-probes for the Tekla bin on demand — verify a connection succeeds without
  restarting the server.
