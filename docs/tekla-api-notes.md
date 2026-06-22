# Tekla Open API notes

Technical reference for Tekla Open API usage in this project.

Official documentation: https://developer.tekla.com/doc/tekla-structures/2026/tekla-structures-64304

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
| Create beam | `new Beam(Point, Point)` + `Profile/Material/Class/Name` + `Insert()` | ⚠️ Unverified — confirm ctor + Insert returns |
| Create plate | `new ContourPlate()` + `AddContourPoint(new ContourPoint(Point, null))` + `Insert()` | ⚠️ Unverified |
| Modify part | set `Profile.ProfileString`/`Material.MaterialString`/`Class`/`Name`, `Beam.StartPoint/EndPoint`, then `Modify()` | ⚠️ Unverified |
| Swap handles | swap `Beam.StartPoint` ↔ `Beam.EndPoint` then `Modify()` | ⚠️ Unverified |
| Delete | `ModelObject.Delete()` | ⚠️ Unverified |
| Commit | `Model.CommitChanges()` once after a batch | ⚠️ Unverified |
| Coordinate system | `Model.GetWorkPlaneHandler()` + `SetCurrentTransformationPlane(new TransformationPlane())` to force global before mutating, restore after | ⚠️ **Critical** — wrong plane = geometry placed wrong |
| Origin tag | `SetUserProperty("MCP_ORIGIN", …)` on created/modified objects | Verify persistence (may need `Modify()` before commit) |
| Grids | `GetAllObjectsWithType(GRID)` → `Grid.CoordinateX/CoordinateY` strings; labels GENERATED by convention (X=1,2,3; Y=А,Б,В) | ⚠️ **Fragile** — verify coord string is absolute (not relative) and map REAL labels incl. Cyrillic |

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
