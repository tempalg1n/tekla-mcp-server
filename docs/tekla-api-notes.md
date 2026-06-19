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
  etc., with versions like `2026.0.x` (project currently pins `2023.0.0` for tested environments).

## Connection model

The server is a **standalone process** that connects to an **already running** Tekla instance
with an open model. Connection is established via `new Tekla.Structures.Model.Model()` and
checked with `model.GetConnectionStatus()`. This is not an in-process Tekla plugin (that
would use `Tekla.Structures.Plugins`).

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
