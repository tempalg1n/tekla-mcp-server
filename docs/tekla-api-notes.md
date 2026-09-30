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
  --dll-dir /tmp/tekla.structures.drawing/lib/net40 \
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
- NuGet packages: `Tekla.Structures`, `Tekla.Structures.Model`,
  `Tekla.Structures.Drawing`, `Tekla.Structures.Plugins`, etc., versions `2021.0.0` ..
  `2026.0.x`. Each release build compiles against ONE version (`-p:TeklaVersion`) and only
  works with that Tekla — see "Tekla version compatibility".

## Connection model

The server is a **standalone process** that connects to an **already running** Tekla instance
with an open model. Connection is established via `new Tekla.Structures.Model.Model()` and
checked with `model.GetConnectionStatus()`. This is not an in-process Tekla plugin (that
would use `Tekla.Structures.Plugins`).

## Tekla version compatibility

The Open API DLLs talk to the running Tekla over a **version-locked protocol**, so the loaded
DLLs must match the running Tekla version. The live build solves this with **per-Tekla-version
builds** ([#11](https://github.com/tempalg1n/tekla-mcp-server/issues/11)) — one release zip per
supported version (2021–2026):

- `TeklaMcp.Tekla` compiles against the Tekla API selected by `-p:TeklaVersion` (a
  `Tekla.Structures[.Model]` NuGet package version, default `2021.0.0`; exact strings at
  https://api.nuget.org/v3-flatcontainer/tekla.structures.model/index.json). The release
  matrix in `.github/workflows/release.yml` builds every supported version.
- It does **not** ship the Tekla DLLs (`ExcludeAssets="runtime"` — Trimble's binaries are not
  redistributable). At runtime `TeklaAssemblyResolver` (registered in `Program.cs` for the
  net48 build) handles `AppDomain.AssemblyResolve` for `Tekla.*`, verifies the installed
  Tekla's **major version matches the compiled one**, and `Assembly.LoadFrom`s the DLLs from
  the installed Tekla's `bin`. On a mismatch every Tekla operation fails fast with a clear
  "wrong build for this Tekla version — download …-tekla&lt;year&gt;.zip" message
  (`TeklaAssemblyResolver.EnsureVersionMatch`, also called from `EnsureTeklaReady` so binds
  the resolver never saw — e.g. a same-version GAC copy — are covered too).

**Why per-version instead of one universal build.** The universal scheme (compile against the
2021 baseline, resolve the running Tekla's assemblies at runtime) was abandoned after the
failure matrix collected in PR #10 (2026-07-08, live Tekla 2023 with stale 2021 assemblies in
the GAC): redirect-based GAC avoidance is fundamentally incompatible with every load API usable
from `AssemblyResolve` (see "Assembly loading history" below), and without redirects the GAC
cannot be beaten — fusion consults it before the event is raised. With per-version builds the
GAC stops being a lottery: a strong-named bind needs the exact compiled version, so a stale
different-version copy never matches, and a same-version copy is protocol-compatible by
definition. The universal build also silently relied on undocumented binary compatibility
between the 2021 API surface and newer Tekla DLLs — Trimble's own model is per-version.

**Locating the Tekla `bin`** — really the Open API folder, the one holding
`Tekla.Structures.Model.dll`: `bin` on 2023+, **`nt\bin\plugins` on 2021** (its `nt\bin` holds
seven unrelated `Tekla.Structures.*.dll`). `TeklaBinLayout` (Core) resolves any given folder —
the API folder itself, its `plugins`/`bin` subfolder, or an install root — to that folder. Order:
the `TEKLA_BIN_DIR` env var (or its API subfolder; a value without the Model DLL is logged and
IGNORED — it used to be trusted blindly, which broke every script on 2021, see below) → the
running `TeklaStructures.exe` processes' folders → the Windows registry
(`SOFTWARE\Trimble\Tekla Structures\<version>\setup`: `MainDir` + `TSVersionDir`, verified on
2021 and 2023; the older `SOFTWARE\Tekla\Structures\<version>` is still probed). Among processes
and registry installs, an Open API whose major version matches this build wins: with 2021 and
2023 running side by side the first process found used to win, and a 2021 build without
`TEKLA_BIN_DIR` reported "wrong build". If nothing matches, another version is returned so
`EnsureVersionMatch` can say "wrong build" instead of "not found". If none is found at startup,
the resolver re-probes whenever a `Tekla.*` bind occurs, so "start the server first, open Tekla
later" recovers without a restart.

**Script references (DEV-005 field report, Tekla 2021, verified 2026-09-29 on this machine's
2021 install).** With `TEKLA_BIN_DIR=…\2021.0\nt\bin`, connecting and reading worked — the core
API bound from the GAC (2021.0.0.0 is there) — but `tekla_check_csharp` failed every script with
CS0234 for `Tekla.Structures.Model`/`Geometry3d`/`Filtering`: references were globbed from that
folder only, and the loaded-assembly fallback ran only for an EMPTY glob. Reproduced with the
v0.7.0-3 build; fixed by `ScriptReferenceSelector` (loaded assemblies authoritative, folder adds,
mixed Tekla years rejected, every decision reported) plus the folder resolution above.

**Remoting channel names (SESSIONNAME).** Confirmed by decompiling the Tekla 2023
assemblies: every Open API assembly computes its channel name as

```
{AssemblyName}-{SESSIONNAME}:{AssemblyVersion}
```

where `{SESSIONNAME}` is the `SESSIONNAME` environment variable of the process computing the
name (`TeklaStructuresInternal.Remoter.GetSessionName()`). Tekla's own process has the
Windows session variable (`Console`, `RDP-Tcp#47`, …); a server launched by an MCP client
frequently has none — that is the real mechanism behind issue #7's mysterious `-Console`
suffix. THREE assemblies each have their own Remoter + lazily-connected static
`DelegateProxy`:

- `Tekla.Structures.dll` (base) — hosts `ModuleManager`, whose static ctor connects over
  this channel. **Every `Insert`/`Modify` calls `ModelModuleManager.CheckModules` →
  `ModuleManager`**, so a misaligned base channel produces the deceptive "reads work,
  `apply=true` fails with a ModuleManager type-initializer exception" pattern (v0.7.0 field
  report). A static proxy whose type initializer failed once is dead until the process
  restarts (the API's own doc: "Currently, there's no way to re-establish the connection").
- `Tekla.Structures.Model.dll` — model reads/writes.
- `Tekla.Structures.Drawing.dll` — drawing tools.

`TeklaRemotingChannel.Align()` (called before the first `Model()`) probes the published
`Tekla.Structures*` pipes, derives the session suffix, sets `SESSIONNAME` for this process
(so every Remoter computes the right name itself) and belt-and-braces patches the three
internal `Remoter.ChannelName` fields. `WarmUpWriteProxies()` then force-initializes
`ModuleManager` right after the first successful connection — the only moment the channels
are known-good. Override with the `TEKLA_MCP_CHANNEL` env var (model channel; its session
suffix is applied to the others). "Not connected" errors include the model + base channels,
`SESSIONNAME`, loaded API version/path, and the published Tekla pipes.

Alignment is a 2021–2023 mechanism with two rules (pure logic in `Core/RemotingChannelNames`,
unit-tested):

- **Only the build's own version counts.** With only another Tekla version publishing, `Align()`
  waits (returns "retry") instead of adopting that Tekla's session. Adopting it used to end
  alignment for the process.
- **2024+ builds are never aligned.** Trimble.Remoting names the channels itself —
  `{Assembly}-{ProductName}-{SESSIONNAME|Console}:{FileVersion}`, no pipes. Writing 2021–2023-style
  names into those Remoters (which happened whenever an older Tekla ran alongside) poisons the
  proxies. `TEKLA_MCP_CHANNEL` on 2024+ patches the exact Model name and the same name with the
  base and Drawing assembly prefixes, and leaves `SESSIONNAME` alone. This is from decompiled
  2024–2026 assemblies. TODO(windows): verify on a live 2024+ install.

**Two different dead connections** (probed on live Tekla 2023 with a scratch client,
2026-09-29; `tekla_get_connection_info` classifies them via
`TeklaRemotingChannel.DiagnoseConnectionFailure`):

- *Stale* — connected once, then Tekla restarted/closed. `Model.GetConnectionStatus()` is only
  `DelegateProxy.Delegate != null` in 2021–2026 (decompiled), so it keeps returning **true**;
  the first real call (`GetInfo()`) throws `RemotingException` ("Requested service not found",
  then "Failed to write to an IPC port"). **Reconnected in-process since 2026-09-30**
  (`TeklaRemotingChannel.TryReconnect`, backlog §1): `ConnectModel` makes one real `GetInfo()` at
  the start of every tool call; on a connection failure it recreates every LOADED client —
  `{Model, TeklaStructures, Drawing, Catalog}Internal.DelegateProxy`, identical members in 2021 and
  2023 (reflection on both installs) — with a new CAO from the public
  `Tekla.Structures.Internal.RemotingProxyHelper.CreateInstance<CDelegate>("ipc://" + Remoter.ChannelName)`
  (two attempts: the IPC client may hold a connection to the dead pipe) assigned through the
  non-public `DelegateProxy.Delegate` property. Same channel names only — a channel under another
  session/instance name may be a different Tekla (§2), so that still says "restart the server".
  **Do not use the public `CDelegateSetter.SetInstanceForUnitTesting`** for the swap: verified on
  live Tekla 2023 that after it the first `CatalogHandler.GetMaterialItems()` throws
  `NullReferenceException` inside `MaterialItemEnumerator.GetMaterialsFromDB`; the property setter
  does not. The swap itself is verified live on 2023 WITHOUT a restart (three rounds; model,
  drawing and catalog clients keep working, catalogs also when first used after a swap).
  Real restarts: **Tekla 2023 passed** three close/reopen cycles (2026-09-30, see backlog §1); the
  reconnect happens as soon as the restarted Tekla publishes its pipe, and the next real call then
  blocks until Tekla has opened the model (22–70 s measured). TODO(windows): the same on Tekla
  2021; whether the first activation after a restart needs the second attempt is not logged yet. 2024+ (Trimble.
  Remoting) is not reconnected: `DelegateProxy.Initialize()` exists there, unverified (§3).
- *Poisoned* — the first touch happened while the channel did not exist. Fixing `SESSIONNAME` /
  `Remoter.ChannelName` afterwards does not help; only a new process (or AppDomain) recovers.
  Two faces: a cached `TypeInitializationException` (the `DelegateProxy` static ctor threw), or —
  **verified on Tekla 2021 (NuGet 2021.0.0 from the GAC), 2026-09-30, no Tekla running** — no
  exception at all: `new Model()` succeeds, `GenericDelegateProxy`'s ctor catches the
  `RemotingException` ("Failed to connect to an IPC port: file not found" from
  `CDelegate..ctor`), prints `Connection failed : …` to stdout and leaves `Delegate` null, so
  `GetConnectionStatus()` answers **false** for the rest of the process, Tekla running or not.

**The poisoning guard** (`TeklaRemotingChannel.EnsurePublished`, called from
`EnsureTeklaReady` for the Model client and from `GetDrawingHandler` for the Drawing client)
prevents the second case instead of diagnosing it: before a client is created it reads that
client's `Remoter.ChannelName` (the Remoter's own static ctor only computes the name — `Align()`
reads it too) and looks for exactly that pipe. Not published → a
`TeklaChannelUnavailableException` saying "not reachable, nothing was sent to Tekla, call again
once the model is open" and no proxy is created, so the next call can still connect. Published
under another suffix while no client has dialed yet → align again first. It blocks only on
evidence: 2024+ builds (no pipes — see §3 of `docs/backlog.md`), a pipe listing that broke off,
or an unreadable channel name let the call through as before. Verified 2026-09-30 on a 2021
build with no Tekla running: every model, drawing and script tool refuses cleanly and stderr
shows no `Connection failed`. TODO(windows): the positive half — start Tekla afterwards and
connect without a server restart — is part of the live acceptance.

With the guard in place, `GetConnectionStatus() == false` while the channel IS published means a
dead client (a fail-open pass, or Tekla still registering its services when the pipe appeared);
`NotConnectedMessage` then says "restart the MCP server" instead of "is Tekla running?".

Classification is by exception type, never message text: the .NET remoting messages are
localized (RU installs). The same classification (`Core/ConnectionErrors`) decides which tool
errors `ToolErrorFilter` replaces with `DiagnoseConnectionFailure`'s cause + action: a
`RemotingException` or a failed `Tekla.Structures*` type initializer anywhere in the chain — not
IO errors, which the file-exchange tools raise for ordinary file problems. TODO(windows): Tekla 2024+ uses Trimble.Remoting (shared memory +
named kernel objects, not named pipes — from decompiled assemblies, unverified live), so the
pipe list in `Describe()` is empty there by design.

Also: the Open API writes `Connection failed : …` to **stdout** when a channel connect
fails (`GenericDelegateProxy` ctor). `Program.cs` routes `Console.Out` to stderr before any
Tekla type loads — the MCP transport itself uses the raw `Console.OpenStandardOutput()`
stream and is unaffected.

**Build overrides**: `-p:TeklaVersion=<nuget version>` picks the Tekla version to compile for
(see [docs/releasing.md](releasing.md) for the version-per-year table);
`-p:TeklaBinDir="...\bin"` compiles against a local install's DLLs (e.g. a version not on
NuGet). Neither bundles the DLLs — the runtime resolver still supplies them.

## APIs used in `TeklaModelService.cs`

| Area | API | Status |
|---|---|---|
| Connection | `new TSM.Model()` + `GetConnectionStatus()` | Verified (Tekla 2023) |
| Model info | `model.GetInfo()` → `ModelInfo.ModelName`, `ModelPath` | Verified |
| Components with input | `new Component(ComponentInput)` with `AddInputObject` / `AddOneInputPosition` / `AddTwoInputPositions` / `AddInputPolygon(Polygon)`, `Number` = `PLUGIN_OBJECT_NUMBER` / `CUSTOM_OBJECT_NUMBER` / system number, `LoadAttributesFromFile`, `SetAttribute(name, string/int/double)`, `Insert()`; read-back `Component.Select()` + `GetChildren()` | Compiles 2021–2026; TODO(windows) live with a real plugin |
| Save / open model | `new ModelHandler()`: `IsModelSaved()`, `Save(comment, user)`, `IsModelAutoSaved(folder)`, `Open(folder, openAutoSaved)` — Open DISCARDS unsaved changes (API doc); outcome read back from `Model.GetInfo().ModelPath` | Compiles 2021–2026; TODO(windows) live: blocking dialogs |
| Part solids | `Part.GetSolid(SolidCreationTypeEnum)` (all 7 values exist in 2021) → `MinimumPoint`/`MaximumPoint`, `GetFaceEnumerator()` → `Face.Normal`, `Face.OriginPartId`, `GetLoopEnumerator()` → `Loop.GetVertexEnumerator()`; all "in the current plane" → read inside `InGlobalWorkPlane` | Compiles 2021–2026; TODO(windows) live: loop order/winding |
| Advanced options | `TeklaStructuresSettings.GetAdvancedOption(name, ref string/bool/int/double)`, `GetAdvancedOptionPaths(name, out List<string>, InvalidPathCallback)` | Compiles 2021–2026; TODO(windows) live |
| Catalogs | `new CatalogHandler()` + `GetConnectionStatus()`; `GetLibraryProfileItems` / `GetParametricProfileItems` (`ProfileItemEnumerator.SelectInstances`), `GetMaterialItems`, `GetComponentItems`, `GetUserPropertyItems([CatalogObjectTypeEnum])`; `UserPropertyItem.GetLabel()` / `GetObjectTypes(ref List)` | Compiles 2021–2026; TODO(windows) live — own Catalogs channel, see `TeklaEnvironmentService.cs` |
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
| Grids | `GetAllObjectsWithType(GRID)` → `Grid.CoordinateX/Y/Z`, `LabelX/Y/Z`, `GetCoordinateSystem()`; parsed by Core `GridMath` | ✅ **verified live (Tekla 2023, 3219, 2026-09-29)**: real labels are space-separated in line order (`"1 2 … 20"`, `"А Б В Г Д"`, `"0.000 +0.200 …"`; extra labels beyond the lines are ignored); Z is ABSOLUTE (`"0.00 200.00 300.00"` labelled `0.000 +0.200 +0.300`); `n*step` repeats a spacing; a second grid had its own origin (−4950, −7250, −260) with axes returned as 1000-mm vectors (normalized). ⚠️ X/Y plain tokens are treated as SPACINGS per Tekla's documentation — the live grids used only repeats, so an irregular plain X/Y string is not yet verified |
| Part position | `Part.Position` → `Plane/Rotation/Depth` + offsets; copied field-by-field or parsed from DTO strings | ✅ Signatures/enums verified; ⚠️ **runtime: verify LEFT/RIGHT and FRONT/BEHIND orientation on live beams** |
| Control lines | `ControlLine.Line.Point1/Point2` | ✅ Signatures verified; ⚠️ live enumeration not yet verified |
| List components | `Part.GetComponents()`; `Connection.GetPrimaryObject/GetSecondaryObjects`, `UpVector`, `AutoDirectionType`, `Status` | ✅ Signatures verified; ⚠️ custom connection runtime behavior not yet verified |
| Create connection | `new Connection`, `Name`, `Number`, `SetPrimaryObject`, `SetSecondaryObjects`, `UpVector`, `LoadAttributesFromFile`, `Insert` | ✅ verified live (Tekla 2023, 2026-07-27): system connection inserted, `MapComponent` round-trips name/number/pair/UpVector. A geometry commit runs before insert. One connection per pair — see "Known model-layer quirks" |
| Modify connection | `Connection.UpVector`, `AutoDirectionType`, `BaseComponent.LoadAttributesFromFile`, `Modify` | ✅ verified live (2026-07-27): UpVector persists ONLY under `AUTODIR_NA`; `AUTODIR_BASIC` returns true and discards it |
| Replace connection | `FindComponentsOnPair` → `Delete()` → `CommitChanges()` → `Insert()` | ✅ verified live (2026-07-27): insert on an occupied pair fails; succeeds once the delete is committed |
| Reference metadata | `ReferenceModelObject.GetReferenceModel()`, `ReferenceModelObjectAttributeEnumerator`, report-property fallbacks; newer custom-attribute API invoked reflectively | ✅ common signatures compile against Tekla 2021; ⚠️ exporter/version key names vary |
| Reference custom attributes | Duplicate-tolerant replay of the internal sequence `DelegateProxy.Delegate.GetReferenceModelObjectCustomAttributes` → `ListExporter.ImportStringList` (all reflective; confirmed by decompiling Tekla 2023). Tekla's own public wrapper `Dictionary.Add`s `"key;value"` rows and THROWS on duplicate attribute names — the internal replay keeps every row; the public wrapper stays as fallback for other versions | ✅ verified live (Tekla 2023, 2026-07-23): 24 attributes for an IFC window that previously errored; ⚠️ internal surface must be re-checked per Tekla version |
| Reference faces | `ModelInternal.Operation.GetReferenceModelObjectFaces(Identifier)` → capped global point lists + derived AABB (`aabbSource: "tekla-faces"`) | ✅ common overload compiles against Tekla 2021; ⚠️ **internal API and world-coordinate behavior require live verification on rotated/scaled/base-point IFCs**; v0.7.0 field report: throws for IFC overlay windows — see IFC fallback |
| Reference IFC fallback | `IfcPlacementReader` (TeklaMcp.Core, pure C#) parses the reference IFC by GlobalId: `IFCLOCALPLACEMENT` chain → world origin + axes, unit-scaled to mm; insertion via `ReferenceModel.Position/Scale` (+ `Rotation`/`ActiveFilePath`/`BasePointGuid` read reflectively — newer members). Reads `.ifczip`/`.zip` (Tekla's `DataStorage\ref` cache — often the ONLY on-disk copy; `ActiveFilePath` points there) and plain `.ifc`; tries every existing copy (cache first, then `Filename`) until the GlobalId resolves. Project length unit = the `LENGTHUNIT` referenced from `IFCUNITASSIGNMENT` — files carry auxiliary length units (Renga IFC4: project `MILLI METRE` + bare `METRE`) and last-wins scales ×1000 | ✅ verified live (Tekla 2023, 2026-07-23) against `3219-АР.ifc` window `0VZkpIecn7$9mG$7iL8u45`: `ifc-file` placement + estimate AABB match the exact `tekla-faces` AABB; ⚠️ **runtime: verify Rotation semantics, base-point offsets and `Scale ≠ 1` overlays live** |
| Reference lookup by IFC GUID | `ReferenceModel.GetReferenceModelObjectByExternalGuid(String)` invoked reflectively over `GetAllObjectsWithType(REFERENCE_MODEL)` | ⚠️ API availability varies by Tekla version; falls back with a clear message |
| Export: coordinate system | `ModelObject.GetCoordinateSystem()` → `Origin`, `AxisX`, `AxisY`; Z derived as X×Y (documented definition), all normalized | ✅ Signature verified via reference (`CoordinateSystem GetCoordinateSystem()` on `ModelObject`); ⚠️ **runtime: not yet verified per part type** |
| Export: contour polygon | `ContourPlate.Contour` / `PolyBeam.Contour` → `Contour.ContourPoints` (an `ArrayList` of `ContourPoint : Point`) | ✅ Signatures verified (`ArrayList ContourPoints { get; set; }`, `ContourPoint` inherits `Point`); ⚠️ **runtime: bent/lofted plates not verified** |
| Export: centre of gravity | `GetReportProperty("COG_X"/"COG_Y"/"COG_Z")` | ⚠️ standard part report properties; **confirm they are populated for `CustomPart`/`Brep` live** |
| Reference export source | `ReferenceModel.GetChildren()` when one model is named, else `GetAllObjectsWithType(REFERENCE_MODEL_OBJECT)`; model matched via `ReferenceModel.Identifier` (it inherits `ModelObject`) | ✅ Signatures verified (`ModelObjectEnumerator GetChildren()`); ⚠️ **runtime: not yet verified against a large IFC overlay** |
| Curve geometry: centerline | `PolyBeam.GetCenterLinePolycurve()` → `Polycurve` (`IEnumerable<ICurve>`) of `LineSegment` / `Arc`; `Arc.StartPoint/ArcMiddlePoint/EndPoint/CenterPoint/Radius/Angle/Length/Normal` | ✅ compiles against 2021–2026; ✅ **verified live (Tekla 2023, 3219, 2026-09-29)** on I30M monorails, D30/D24 anchors and PL40*4 flats: `Angle` is RADIANS, `Length` = R·Angle, `ArcMiddlePoint` is the ANGULAR midpoint, `Normal` = normalize((start−center)×(mid−center)), `StartDirection` = unit(start−center), `StartTangent` = direction of travel. The polycurve is the true centerline, offset from the reference line by the position (PL40*4 with Plane=RIGHT: centerline R 344 vs rounding X = 346). An `ARC_POINT` bend comes back as TWO arcs split at the arc point (merged by `CurveMath.MergeArcs`). ⚠️ Boolean parts / fittings are NOT applied: a D30 anchor with a `BooleanPart` has centerline 1380.1 mm vs `LENGTH` 1480.1 mm (the tool warns). `CheckReportedArc` cross-checks Radius/Angle/Length against the points on every call, so a per-version semantic change shows up as a warning |
| Curve geometry: straight parts | `Part.GetCenterLine(false)` → first/last point | ✅ used for `Beam`; ⚠️ on a PolyBeam it returns a SEGMENTED polyline (19 points for one 90° bend) — never derive arcs from it |
| Curve geometry: chamfers | `ContourPoint.Chamfer` → `Type` (`CHAMFER_NONE/LINE/ROUNDING/ARC/ARC_POINT/SQUARE/SQUARE_PARALLEL/LINE_AND_ARC`), `X`, `Y`, `DZ1`, `DZ2` | ✅ verified live: `ROUNDING` X = fillet radius on the reference line, tangent points X from the corner along both legs of a 90° corner; `ARC_POINT` marks the point an arc passes through |
| Curve geometry: view projection | `MatrixFactory.ByCoordinateSystems(global, View.DisplayCoordinateSystem)` via `GlobalToViewMatrix`, the SAME helper `coordinateSpace=model` uses in the content tools | ✅ shared code path; ⚠️ **not yet exercised live on an open drawing** (no drawing was open during the 2026-09-29 run); verify on a rotated single-part drawing |
| Schematic: context | `Model.ModelObjectSelector.GetObjectsByBoundingBox(Point, Point)` (Tekla's spatial index; approximate boxes, every hit re-checked) | ✅ compiles 2021–2026; ✅ verified live (3219): a 18 × 5 × 20 m region → 546 parts in ~2 s |
| Schematic: geometry | `Beam.StartPoint/EndPoint`, `ContourPlate/PolyBeam.Contour.ContourPoints`, `PolyBeam.GetCenterLine(false)` (segmented — right for drawing, wrong for dimensions), `BoltGroup.BoltPositions`, `Part.GetSolid()` AABB as a fallback capped at 300 per scene | ✅ verified live (3219): 20 000 parts ≈ 5.7 s (whole-model overview, capped) |
| Views | `ViewHandler.GetVisibleViews()` → `View.Name/DisplayType/ViewProjection/WorkArea` | ✅ verified live (Tekla 2023): returns every OPEN view, covered ones included; `Name` is the bare name ("3D"), the window title is "View 4 - 3D"; the section views of 3219 are `DISPLAY_3D` with narrow work areas |
| Camera | `ViewCamera { View }` + `Select()` → `Location`, `DirectionVector`, `UpVector`, `ZoomFactor`, `FieldOfView`; `Modify()` to apply | ✅ `Select()` verified live: `DirectionVector` is the direction the camera LOOKS along (3D view looked along (−0.37, −0.93, 0.04) from +Y); `ZoomFactor` is documented as m/px but the live values (8.9 for a close 3D view, 98–152 for whole sections) behave like **mm per pixel**; ⚠️ **`Modify()` (rotate + restore) not yet exercised live** |
| Zoom | `ViewHandler.ZoomToBoundingBox(View, AABB)` | ✅ compiles 2021–2026; ⚠️ **not yet exercised live** |
| Temporary colours | `ModelObjectVisualization.SetTransparencyForAll(TemporaryTransparency)`, `SetTemporaryState(List<Identifier>, Color)`, `ClearAllTemporaryStates()` | ✅ compiles 2021–2026; ⚠️ **not yet exercised live**; docs: permanent representation returns on redraw or clear |
| Temporary text | `GraphicsDrawer.DrawText(Point, String, Color)` (active rendered view only), removed with `ViewHandler.RedrawView(View)` | ✅ compiles 2021–2026; ⚠️ **not yet exercised live** — that the redraw removes it is an assumption |
| View pixels | Win32 `PrintWindow(renderSurface, hdc, PW_RENDERFULLCONTENT)` — not a Tekla API; see "Viewport capture" below | ✅ **verified live (Tekla 2023, Windows 10 19045)**: clean DirectX view in ~25 ms, overlapping windows excluded |

### Viewport capture (`tekla_capture_view`)

The Open API has no screenshot call (no member mentions one, 2021–2026). Probed read-only on Tekla
2023 (model 3219, 2026-09-29):

- Window tree: `HwndWrapper[TeklaStructures.exe;…]` (main, title "Tekla Structures - <model path> -
  <active view>") → `MyFakeWindow1` → `AfxMDIFrame140` → `MDIClient` → one `AfxFrameOrView140` per
  open view, titled "View N - <View.Name>" → the render surface (class `Afx:…`, title "dakit external
  view for zkit"), the largest visible descendant. `WM_MDIGETACTIVE` on the MDI client names the
  active view. The model path in the main title picks the right Tekla when several run.
- `PrintWindow(surface, hdc, PW_RENDERFULLCONTENT)` → the rendered view, ~25 ms, **even with other
  applications and Tekla's own dialogs on top**. `PrintWindow` without the flag → a blank frame.
- It returns the main window's composition in the surface rectangle: a view covered by another MDI
  view comes back as the covering view + side panel. Covered views are therefore refused; only the
  active view or tiled views can be captured.
- ⚠️ A desktop copy (`CopyFromScreen`/`BitBlt`) of the same rectangle captured an overlapping
  Explorer window listing the user's private files. The server must never capture the screen.
- The capture runs under per-monitor DPI awareness (`SetThreadDpiAwarenessContext`) so window
  rectangles and pixels agree on scaled displays — ⚠️ only exercised at 100 % scaling so far.
- The server must run in the user's desktop session (a stdio child of the MCP client does); a
  service in session 0 cannot see Tekla's windows.

### Cost of a whole-model export (why the exchange tools page)

Measured on the field model (3155, ~65 700 parts, Tekla 2021): `Part.GetSolid()` + AABB ≈ **1.5 ms
per part**, so a full geometric export is ~90–100 s of pure API time. MCP clients abort the request
at ~60 s and the reply is lost even when the server finishes — hence `maxObjects` + `cursor` +
`append` on `tekla_export_parts_file`, and the `<path>.status.json` sidecar as the out-of-band
completion record. `GetAllObjects()` alone costs ~50 s on a 400k-object model; the part-type chain
(`GetAllObjectsWithType` × 8) does the same scan in ~5 s.

### ⚠️ `GetReferenceModelObjectFaces` can wedge Tekla itself (field report, 2026-08-13)

Running `ModelInternal.Operation.GetReferenceModelObjectFaces` in a loop over an IFC overlay in
model 3155 (Tekla 2021 SP7) left **Tekla unresponsive with one core pegged**. Killing the MCP
client process did **not** release it: by then the work had been handed to Tekla, and nothing on
the client side can interrupt it. Resident memory fell in steps (2043 → 1476 → 958 MB) while CPU
kept climbing, so it was computing rather than deadlocked — but it had not returned 35+ minutes
later. Same internal API that the v0.7.0 report saw *throw* for IFC overlay windows.

Consequences baked into the code:

- `tekla_export_reference_objects_file` has **`includeFaceAabb=false` by default**. The exact
  face-derived AABB is opt-in; the default path uses IFC-file placement estimates, and every row
  says which it got via `aabbSource`.
- The tool description tells the agent to probe an untried reference model with `maxObjects=5`
  first and report the cost.
- `maxSeconds` bounds a page that is merely *slow* — it is checked between objects and therefore
  **cannot** bound one call that never returns. Do not present it as protection against this.

There is no server-side fix for the underlying behavior: the Open API offers no cancellation, and
the script escape hatch's execution deadline has the same blind spot (it can abort the worker
thread, not the computation inside Tekla). Treat any per-object internal `Operation.*` call over a
whole reference model as capable of taking the application down, and gate it accordingly.

An AABB spanned by a face set that hit the per-object budget is too small and is labelled
`aabbSource="tekla-faces-truncated"` rather than reported as exact.

## Known model-layer quirks (verified live)

The drawing layer is labelled experimental, but the MODEL layer has its own sharp edges. These
were reproduced on live Tekla 2023 (2026-07-27) after a field report; each one previously cost an
agent a long debugging detour because the API reports success while doing something else.

### 1. `Connection.UpVector` is only stored under `AUTODIR_NA`

Setting `UpVector` and calling `Modify()` returns **true** under `AUTODIR_BASIC`, but the vector
is silently recomputed from the members and the written value is lost. The identical write with
`AutoDirectionType = AUTODIR_NA` persists.

```
BASIC: Modify()=true, CommitChanges()=true → re-read UpVector = (0,0,1000)   // the OLD value
NA:    Modify()=true, CommitChanges()=true → re-read UpVector = (1000,0,0)   // as written
```

`ModifyConnections` therefore switches a component to `AUTODIR_NA` whenever an explicit
`UpVector` is supplied and the caller did not name a mode; `tekla_modify_connections` /
`tekla_create_connection` warn when a caller pins a non-NA mode *and* passes a vector.

### 2. Tekla canonicalizes `Part.Position` on commit

A written Position comes back in an equivalent-but-renamed form, so a naive read-back looks like
the write was lost. Verified on a live beam:

| Written | Stored and read back |
|---|---|
| `Rotation=TOP`, `RotationOffset=45` | `Rotation=BACK`, `RotationOffset=-45` |
| `Rotation=TOP`, `RotationOffset=180`, `Plane=LEFT` | `Rotation=BELOW`, `RotationOffset=0`, `Plane=RIGHT` |

The physical orientation is correct in every row — the offset is folded into the enum quadrant.
**Do not treat `RotationOffset == 0` as proof the write failed.** This affects the Open API and
the dedicated tools identically; there is no discrepancy between them.

### 3. Write results must be read back from the database

Because of (2), echoing the in-memory object after `Modify()` reports values the model does not
agree with. `ModifyParts`, `ModifyConnections` and `CreateConnections` re-select every committed
object by GUID after `CommitChanges()` and map *that*. Keep this when adding write paths — the
alternative is a tool that confidently contradicts the next read.

The same honesty applies to the outcome (backlog §4, `Core/WriteProgress`): there are no
transactions — `Insert`/`Modify`/`Delete`/`SetUserProperty` are in the model as soon as they
return, and `CommitChanges()` flushes rather than commits. A failure after the first of those
calls is therefore reported as `unknown`, never as "not written". `ModifyParts` now treats
`Modify() == false` as a refusal and `DeleteObjects` treats `Delete() == false` the same way.
TODO(windows): confirm live that a no-op `Modify()` of an unchanged part returns true, and
break the connection mid-batch on a scratch model to see the `unknown` path end to end.

### 4. One connection per primary/secondary pair

Tekla **rejects** `Connection.Insert()` when the pair already carries a connection (the failure
surfaces as a plain "rejected connection insert"). Swapping a node type is therefore
delete → `CommitChanges()` → insert, and the intermediate commit is required. This is what
`ConnectionSpec.ReplaceExisting` does; verified live (insert on an occupied pair failed, the same
insert succeeded after the delete was committed).

### 5. Component names differ between the UI and the API

The Tekla UI shows a component as `…ГК (1)` where the API's `Name` is `…ГК 1` (and custom
components carry Cyrillic names in full). Filtering on a name copied from the UI silently matches
nothing. Read names from `tekla_list_connections` / `tekla_find_connections` and copy identity
from an existing detail rather than retyping it.

### 6. Custom-component insertion is attribute-file sensitive

Field report: `LoadAttributesFromFile("standard")` followed by `Insert()` failed for custom
components that inserted fine without the attributes call, or when copied from an existing
detail. Arbitrary custom-component attributes cannot be enumerated through the API, so the
reliable path is **copy from a working source detail** (`tekla_copy_connection`) and only pass
`attributesFile` when a specific saved set is genuinely required. Not yet reduced to a minimal
repro — treat as environment/component dependent.

## Drawing API implementation notes

`Tekla.Structures.Drawing.dll` is referenced only by the `net48` live backend and uses the same
`$(TeklaVersion)` as the other Tekla packages. The calls below are signature-checked against the
common 2021 reference surface. Unless explicitly stated otherwise, the v0.7 Drawing API path has
**not** been exercised against a live Windows drawing editor.

| Area | API | Status |
|---|---|---|
| Connection/editor state | `new DrawingHandler()`, `GetConnectionStatus()`, `GetActiveDrawing()` | ✅ 2021 signatures; ⚠️ live editor transitions unverified |
| Drawing enumeration | `DrawingHandler.GetDrawings()`, `DrawingSelector.GetSelected()`, `DrawingEnumeratorBase.AutoFetch` | ✅ 2021 signatures; ⚠️ Drawing List selection behavior unverified |
| Drawing identity | `DrawingInternal.DatabaseObjectExtensions.GetIdentifier(DatabaseObject)` → ID/ID2/GUID | ✅ 2021 signature; ⚠️ internal/version-sensitive and best-effort |
| View identity | `DrawingInternal.DatabaseObjectExtensions.GetIdentifier(DatabaseObject)` (own ID); `GetViewIdentifier` means containing view | ✅ 2021 signatures; ⚠️ internal/version-sensitive and best-effort |
| Drawing metadata | `Drawing.Mark/Name/Title*`, issue/lock/freeze/master/ready flags, dates, `UpToDateStatus`, `GetPlotFileName` | ✅ 2021 signatures; ⚠️ nullable/default semantics unverified |
| Model links | assembly/part/cast-unit identifiers; `DrawingHandler.GetModelObjectIdentifiers(Drawing)` | ✅ 2021 signatures; ⚠️ GUID/ID coverage by drawing type unverified |
| Open/save/close | `SetActiveDrawing`, `SaveActiveDrawing`, `CloseActiveDrawing(save)` | ✅ 2021 signatures; ⚠️ UI and unsaved-change behavior requires live validation |
| Drawing creation | `AssemblyDrawing`, `SinglePartDrawing`, `CastUnitDrawing`, `GADrawing`, then `Insert()` / `CommitChanges()` | ✅ 2021 constructors/signatures; ⚠️ live numbering/editor preconditions unverified |
| AutoDrawing | `Automation.AutoDrawingRule`, `Automation.DrawingCreator.CreateDrawings` | ✅ 2021 signatures; ⚠️ saved rule resolution/status is environment-dependent |
| Metadata edit | assign drawing properties, `Modify()`, `CommitChanges(message)` | ✅ 2021 signatures; ⚠️ writable-field restrictions vary by drawing/status |
| Lifecycle | `IssueDrawing`, `UnissueDrawing`, `UpdateDrawing`, `Drawing.Delete`, `Drawing.PlaceViews` | ✅ 2021 signatures; ⚠️ exact active-editor/numbering restrictions need live validation |
| PDF output | `DPMPrinterAttributes`, `PrintDrawing` with output/color/orientation/paper/scaling enums | ✅ 2021 signatures; ⚠️ printers, paths, naming and enum behavior are environment-dependent |
| View enumeration | `Drawing.GetSheet().GetViews()`, `View` frame/scale/restriction/coordinate systems | ✅ 2021 signatures; ⚠️ units and view-type behavior need live validation |
| Sheet geometry/layout | `Drawing.GetSheet()` width/height/origin/frame + drawing `Layout.SheetSize`/size mode | ✅ 2021 signatures; ⚠️ auto-size semantics need live validation |
| Basic view creation | `View.CreateFrontView/CreateTopView/CreateBackView/CreateBottomView/Create3dView` | ✅ 2021 signatures; ⚠️ sheet placement and default attributes unverified |
| GA model-view creation | `new View(ContainerView, ViewCoordinateSystem, DisplayCoordinateSystem, AABB[, attributes])`, `Insert()` | ✅ 2021 signatures; ⚠️ axes/restriction/paper placement need live validation |
| Section/detail views | `CreateSectionView`, `CreateCurvedSectionView`, `CreateDetailView` plus mark attributes | ✅ 2021 signatures; ⚠️ cut-point/depth/mark semantics unverified |
| View edit | `View.Modify/Delete`, width/height/origin/scale, `RotateViewOnAxis*`, `RotateViewOnDrawingPlane` | ✅ 2021 signatures; ⚠️ rotations and frame effects unverified |
| Object enumeration | placed `View.GetObjects/GetAllObjects`, sheet `ContainerView.GetObjects`, `DrawingObjectSelector.GetSelected/SelectObjects` | ✅ 2021 signatures; ⚠️ selection and recursive ordering unverified |
| Object identity | DrawingInternal identifier plus enumeration-index fallback | ✅ 2021 signature; ⚠️ ID may be zero and index is intentionally ephemeral |
| Object metadata | drawing `ModelObject.ModelIdentifier`, `IHideable`, type-specific geometry, optional database-object UDAs | ✅ 2021 signatures; ⚠️ geometry/visibility/UDA availability varies by type |
| Coordinate transform | `MatrixFactory.ToCoordinateSystem(view.DisplayCoordinateSystem).Transform(point)` | ✅ common signature; ⚠️ global-to-view orientation must be checked on rotated/mirrored views |
| Graphics/annotations | `Text`, `Line`, `Rectangle`, `Circle`, `Arc`, `Polyline`, `Polygon`, `Cloud`, `Symbol`, `LevelMark` | ✅ 2021 constructors/signatures; ⚠️ insertion/attributes/bulge behavior unverified |
| Dimensions | straight and curved dimension-set handlers, `AngleDimension`, `RadiusDimension` | ✅ 2021 signatures; ⚠️ point order, paper distance and attributes need live validation |
| Object marks | `View.GetModelObjects(Identifier)`, `new Mark(ModelObject)`, optional insertion/attributes | ✅ 2021 signatures; ⚠️ represented-object and duplicate-mark behavior unverified |
| Mark merge/split | `Drawing.Operations.Operation.MergeMarks` / `SplitMarks` | ✅ 2021 signatures; ⚠️ mark compatibility/result identity unverified |
| Object edit | `IMovableRelative.MoveObjectRelative`, `IHideable.Hideable`, `Attributes.LoadAttributes`, `Modify/Delete` | ✅ 2021 signatures; ⚠️ capability varies by concrete object type |
| Drawing origin tag | drawing `DatabaseObject.SetUserProperty("MCP_ORIGIN", ...)` + `Modify()` | ✅ common database signature; ⚠️ support/persistence varies by object/environment |

### Drawing addresses and coordinate spaces

The public Drawing API has no single durable ID property across drawings, views, and child
objects. v0.7 tries the DrawingInternal extension methods inside `try/catch`. If a drawing ID is
unavailable, its MCP key falls back to an escaped composite of type, associated model GUID,
sheet number, mark, and name. That fallback can change when public properties change. View and
object indices are only enumeration positions; re-list after inserts/deletes and prefer non-zero
ID/ID2 pairs.

Drawing content uses:

- `view`: target-view local/display-coordinate-system coordinates;
- `model`: global model coordinates transformed through `View.DisplayCoordinateSystem`;
- `sheet`: drawing-paper millimetres on `Drawing.GetSheet()`.

View placement/frame and dimension-line distances use paper millimetres; section/detail points
are source-view-local and section depths are model millimetres. Read-side object geometry is
returned in the coordinate system Tekla exposes for that object; callers should use the
coordinate systems returned by `tekla_list_drawing_views` instead of assuming global values.

### Drawing editor preconditions

- Drawing status/list/QA can run without an active drawing.
- View/object/content calls require an active drawing.
- Creating drawings and AutoDrawing require the drawing editor to be closed.
- A drawing cannot be deleted, updated, or printed while active; update also requires current
  numbering.
- `PlaceViews()` is intended for the matched active drawing.
- `CloseActiveDrawing(save: false)` discards unsaved changes and remains preview-by-default in
  the MCP tool.

### Useful report properties

`WEIGHT`, `WEIGHT_NET`, `LENGTH`, `HEIGHT`, `WIDTH`, `AREA`, `VOLUME`, `PROFILE`, `MATERIAL`,
`CLASS`, `NAME`, `ASSEMBLY_POS`, `PART_POS`, `PHASE`. Full list in Tekla documentation
(Template/Report properties section).

Profile facts, verified live on Tekla 2023 (model 3219, 2026-09-29): `PROFILE_TYPE` (`RO` round
tube, `RU` round bar, `I`, `B` plate/flat, …), `PROFILE.DIAMETER` (O51X3.5 → 51, D30 → 30),
`PROFILE.PLATE_THICKNESS` (tube wall: 3.5). ⚠️ `HEIGHT`/`WIDTH` are NOT profile dimensions on a bent
PolyBeam: a bent D30 anchor reports `HEIGHT = 2655.98` (and `WIDTH = 30`). Use the `PROFILE.*`
properties for section sizes.

## Windows build environment

- Windows x64; Tekla Structures installed and running with a model open.
- .NET SDK 8+ and .NET Framework 4.8 Developer Pack.
- `Tekla.Structures`, `.Model`, and `.Drawing` NuGet versions should match the installed Tekla
  version, or use local `<Reference>` entries with `<HintPath>` to Tekla installation DLLs.

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

**Native DLLs in the reference glob (issue #15).** Both backends glob `Tekla.Structures*.dll`
(and `Tekla.Dialog*.dll` / `Tekla.*.dll`), and Tekla 2025 ships a NATIVE
`Tekla.Structures.Native.DbvDatabase.dll` next to the managed API. `MetadataReference.CreateFromFile`
does not validate the image — it only prefetches the bytes — so a native (or garbage) file becomes a
reference without an exception and EVERY compile then fails with `CS0009`. `ScriptEngine.BuildReferences`
therefore admits a file only when `PEReader` finds managed metadata and an assembly manifest
(`IsReferenceableAssembly`). Checked against all 374 DLLs in a Tekla 2023 `bin` (95 native): identical
verdicts to `AssemblyName.GetAssemblyName`, and all 29 `Tekla.Structures*.dll` stay referenced.
Unit tests use a hand-built PE32 image without a CLI header (`ScriptReferenceTests`).

### Assembly loading history (constraints that must not be violated)

`TeklaAssemblyResolver` now simply `Assembly.LoadFrom`s the matching per-version install's
DLLs — plain and policy-free. `Assembly.Location` is real again, and script metadata
references start from the loaded Tekla assemblies plus the DLL files in
`TeklaAssemblyResolver.BinDir` (Drawing/Dialog/Datatype/Plugins included) — see
`TeklaModelService.SelectScriptReferences`.

**What the CLR caches when a bind fails** (probed on .NET Framework 4.8 with a fake strong
assembly, 2026-09-29):

- An exception THROWN by an `AssemblyResolve` handler is cached for the AppDomain: the handler
  is never called again for that assembly and every later bind rethrows it. A handler that
  returns null is asked again next time. So the resolver must never throw. On a version
  mismatch it returns null, and the "wrong build" message comes from `EnsureVersionMatch`
  instead.
- A failed static initializer (TypeInitializationException) is cached for good, even when the
  handler returned null. That is why `TeklaModelService`, whose static fields hold Tekla enum
  tables, is created through `TeklaBackendFactory` only after the version check passes. The DI
  container retries a singleton factory that threw (checked with
  Microsoft.Extensions.DependencyInjection 8).
- Compiling a method that merely mentions a Tekla type binds its assembly, even when that code
  path never runs. Decisions that must work without the Open API (e.g. skipping alignment for
  2024+ builds) belong in a method that mentions no Tekla type.

Two hard-won constraints from the universal-build era (the PR #10 failure matrix) still apply to
ANY future change here:

- **Never combine an `AssemblyResolve`-based resolver with anti-GAC bindingRedirects.** On
  .NET Framework `Assembly.Load(byte[])` (unlike `LoadFile`) APPLIES binding policy to the
  image's identity, so a `Tekla.* → 2999.9.9.9` redirect turns the resolver's own load into a
  bind for 2999.9.9.9 — a circular resolve ending in FileNotFound (with a re-entrancy guard)
  or StackOverflow/hangs (without). Verified live twice: cbe716b and the d167ff4 re-attempt
  (reverted).
- **Do not switch to `Assembly.LoadFile` either.** It binds in a separate load context whose
  dependency binds re-entered `AssemblyResolve` until StackOverflow on live Tekla (the
  original issue #7 crash).

The old issue #7 failure mode — a stale GAC copy at the compiled version binding silently
while a different Tekla runs — is now caught by `TeklaAssemblyResolver.EnsureVersionMatch`
(called per operation from `EnsureTeklaReady`): the tools fail fast with the "wrong build for
this Tekla version" message instead of failing cryptically on the remoting channel.

**Still TODO(windows):**

- Timeout abort uses `Thread.Abort` (supported on net48, no-op catch on net8) — verify an
  aborted script doesn't wedge the Tekla remoting channel.
- The mutation path (`allowMutations=true`) has not been run against a live model.
- v0.7 hardening adds the remoting-free `tekla_check_csharp` compile-only path, source SHA-256,
  conservative model+drawing mutation detection, explicit execution-attempt semantics and
  partial-mutation warnings. Verify the check works with Drawing/Dialog types on each supported
  Tekla version. `Tekla.Structures.Drawing` is intentionally not a global script import because
  `Part` and `View` collide with Model/UI names; scripts should alias it explicitly.
- v0.7 drawing tools: validate DrawingInternal IDs/fallback keys, drawing-list/editor selection,
  lifecycle/create/AutoDrawing/print, view coordinate conversion and all supported content
  object types across the 2021–2026 release builds.
- "Server started before Tekla" flow: `Align()` retries until Tekla publishes its pipes, and
  the resolver re-probes for the Tekla bin on demand — verify a connection succeeds without
  restarting the server. A tool call while Tekla is down no longer dials anything (the
  poisoning guard above) — verify the connection then succeeds once the model is open, on 2021
  and 2023.
- SESSIONNAME channel alignment (v0.7.0 field-report fix): on the live machine verify that
  (a) `tekla_create_beam` with `apply=true` creates the beam (stderr shows
  "write-path proxies initialized (ModuleManager configuration: …)"), and (b) the drawing
  tools connect. If several Tekla sessions run under different Windows sessions, verify the
  suffix choice or force `TEKLA_MCP_CHANNEL`.
- IFC placement fallback: ✅ verified live 2026-07-23 (Tekla 2023, `3219_Model_playground`)
  against the 3219-АР reference window `0VZkpIecn7$9mG$7iL8u45` — `placementSource:
  "ifc-file"` origin (-496, 5795.82, 9200) and the `ifc-placement-estimate` AABB match the
  exact `tekla-faces` AABB; resolved from the `DataStorage\ref\*.ifczip` cache (the original
  `.\INCOMING\*.ifc` did not exist on disk). Still TODO: a rotated (`Rotation ≠ 0`) and a
  scaled overlay, and a base-point model.
- Per-version fail-fast (issue #11): on a machine whose GAC holds a DIFFERENT Tekla version
  than the build (e.g. tekla2023 build, 2021 in the GAC), verify the dedicated tools work and
  that a deliberately wrong zip (e.g. tekla2021 on running Tekla 2023) produces the
  "wrong build for this Tekla version" message.
