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
  etc., versions `2021.0.0` .. `2026.0.x`. The build's Tekla version is **configurable**
  (default `2023.0.0`) — see "Tekla version compatibility" below.

## Connection model

The server is a **standalone process** that connects to an **already running** Tekla instance
with an open model. Connection is established via `new Tekla.Structures.Model.Model()` and
checked with `model.GetConnectionStatus()`. This is not an in-process Tekla plugin (that
would use `Tekla.Structures.Plugins`).

## Tekla version compatibility

The Open API DLLs talk to the running Tekla Structures over a **version-locked protocol**, so the
running DLLs must match the running Tekla version. There is no single set of DLLs that works
across all versions — you either build per version, or load the matching DLLs at runtime.

**Build for a specific version** (`src/TeklaMcp.Tekla/TeklaMcp.Tekla.csproj`, default `2023.0.0`):

- From NuGet (versions `2021.0.0` .. `2026.0.x`):
  ```powershell
  dotnet build TeklaMcp.sln -c Release -p:TeklaVersion=2021.0.0
  ```
  Exact version strings: https://api.nuget.org/v3-flatcontainer/tekla.structures.model/index.json
- From a local Tekla install `bin` (any version, incl. ones not on NuGet, or an exact patch):
  ```powershell
  dotnet build TeklaMcp.sln -c Release -p:TeklaBinDir="C:\Program Files\Tekla Structures\2021.0\bin"
  ```

Each build ships the matching Tekla DLLs, so run the build whose version matches your installed
Tekla. (`net8.0` mock builds are unaffected — they never reference Tekla.)

**One build for all versions** (not implemented yet): compile against a baseline version with
`<Private>false</Private>` and add an `AppDomain.AssemblyResolve` handler that loads
`Tekla.Structures*.dll` from the installed Tekla's `bin` at runtime. Locating that folder needs
the Windows registry (`SOFTWARE\Tekla\Structures`) or a `TEKLA_BIN_DIR` env var, because the MCP
server is launched separately from Tekla and does not inherit its process environment.

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
