# Architecture

This document explains **why** the project is structured the way it is. Conventions for
contributors and AI agents are in [../AGENTS.md](../AGENTS.md); overview in
[../README.md](../README.md).

## Core constraint

Tekla Open API is a set of **Windows .NET assemblies** (`.NET Framework 4.8`, x64 from
Tekla 2026 onward). The MCP server must therefore run on Windows to talk to a live model,
while still allowing development and testing **without Tekla installed**.

## Solution: interface + two backends + multi-targeting

### Abstraction layer

`ITeklaModelService` (in `TeklaMcp.Core`) describes all model operations. MCP tools
depend only on this interface. Two implementations:

- `MockTeklaModelService` — synthetic data, cross-platform (`netstandard2.0`).
- `TeklaModelService` — real Tekla Open API (`net48`, Windows-only).

The service surface stays at backend primitives: queries/geometry, batched part mutations and
batched connection creation, plus stateful Drawing API query/write batches. High-level
generators or future detail replication belong in the tool layer and compose those primitives.
Reference-model objects are an explicit exception to GUID-first addressing: Tekla commonly
reports an empty GUID for them, so reference geometry is addressed by the integer model-object
ID from the current session.

### Drawing subsystem

The drawing layer is **experimental** (first shipped in v0.7.0): it has had less live-model
exposure than the model tools, and Tekla's Drawing API carries version-specific
remoting/serialization quirks (e.g. some drawing objects cannot be materialized over the
remoting channel and must be skipped during enumeration). Expect bugs to surface in the field.

The Drawing API is a separate version-matched assembly (`Tekla.Structures.Drawing.dll`) but
runs in the same live `net48` backend and behind the same `ITeklaModelService` boundary. The
implementation is split across partial-class files to keep concerns reviewable:

| Layer | Files | Responsibility |
|---|---|---|
| DTO/service contract | `DrawingInfo.cs`, `DrawingWriteModels.cs`, `ITeklaModelService.cs` | Flat serializable drawing/view/object queries, specs, and preview results |
| Mock | `MockTeklaModelService.cs` | Stateful synthetic drawing list, active editor, views, and objects |
| Live backend | `TeklaDrawingService.cs` | Drawing list, editor lifecycle, create/update/issue/delete/print |
| Live backend | `TeklaDrawingObjectService.cs` | View/object enumeration, identity, selection, view create/modify |
| Live backend | `TeklaDrawingContentService.cs` | Graphics, annotations, dimensions, marks, object edit/delete |
| MCP tools | `DrawingQueryTools.cs`, `DrawingWriteTools.cs`, `DrawingContentTools.cs` | Agent-facing workflows and preview/apply safety |

Drawing state has explicit preconditions:

- list/status/QA queries do not require an open drawing;
- active-drawing view/object queries and content writes require the drawing editor to be open;
- drawing creation and AutoDrawing require the editor to be closed;
- deleting, updating, or printing a drawing requires that target not to be active; update also
  depends on Tekla numbering;
- opening never silently replaces an already active drawing, and closing with `save=false`
  explicitly discards unsaved editor changes.

Persistent drawing mutations use the same `apply=false` preview convention as model writes.
The live backend batches and caps targets, reports per-item errors, and commits active-drawing
content through `Drawing.CommitChanges`. `MCP_ORIGIN` is attempted on created/modified drawing
database objects, but drawing-side UDA availability is environment/object dependent.

#### Drawing identity

Public Drawing API objects do not provide a uniform identifier property. The live backend reads
an object's own identifier with
`DrawingInternal.DatabaseObjectExtensions.GetIdentifier` on a best-effort basis.
`GetViewIdentifier` identifies a drawing object's containing view and is not a placed View's own
identity:

- a drawing key is `drawing:<ID>:<ID2>` when available;
- otherwise it is an opaque composite of public type, associated model GUID, sheet number,
  mark, and name;
- view/object `ID:ID2` pairs are preferred when non-zero;
- enumeration indices are deliberately ephemeral and must be refreshed after structural edits.

The internal identifier surface is version-sensitive and not treated as a guaranteed persistent
external ID. Exact mark lookup is only accepted when unambiguous.

#### Drawing coordinates

Model write inputs remain global model millimetres. Drawing content has three explicit spaces:

| Space | Meaning |
|---|---|
| `view` | Target view/display-coordinate-system coordinates (model millimetres before drawing scale) |
| `model` | Global model coordinates transformed with the target view's `DisplayCoordinateSystem` |
| `sheet` | Drawing-paper millimetres; target is the sheet (`viewIndex=-1`, no view ID) |

View placement/origin/frame dimensions and dimension-line distances are paper millimetres.
Section/detail definition points are source-view-local; section depths are model millimetres.
Read-side object geometry stays in the coordinate system Tekla exposes for that drawing object;
the API does not silently label it as global. `DrawingViewInfo` therefore returns view/display
coordinate systems so callers can transform deliberately.

### Why `netstandard2.0` for Core and Mock

`netstandard2.0` is the common denominator understood by both modern `.NET 8` and
`.NET Framework 4.8`, so the same `Core`/`Mock` assemblies plug into both server builds.

### Why the server multi-targets `net8.0` and `net48`

| Component | Supported targets |
|---|---|
| MCP C# SDK (`ModelContextProtocol`) | `netstandard2.0`, `net8.0` |
| Tekla Open API | `.NET Framework 4.8`, `netstandard2.0` |

Both are compatible with `netstandard2.0`, so a single **`net48` process on Windows**
can host the MCP SDK and Tekla Open API together — no two-process split required for the
prototype. Hence:

- **`net8.0`** — cross-platform build with `Core` + `Mock` only. Runs without Tekla.
- **`net48`** — Windows build that additionally references `TeklaMcp.Tekla` for the live
  backend.

Backend selection is via `#if NET48` in `Program.cs`. Set `TEKLA_MCP_USE_MOCK=1` to force
the mock backend even in the `net48` build.

### Why `net48` is disabled on non-Windows

In `TeklaMcp.Server.csproj`:

```xml
<TargetFrameworks Condition="'$(OS)' == 'Windows_NT'">net8.0;net48</TargetFrameworks>
<TargetFrameworks Condition="'$(OS)' != 'Windows_NT'">net8.0</TargetFrameworks>
```

Non-Windows builds target only `net8.0` and never reference the Windows-only
`TeklaMcp.Tekla` project.

## MCP transport: stdio

The server communicates over **stdio** (JSON-RPC on stdin/stdout). Therefore:

- stdout is reserved for the protocol — logs go **only to stderr**
  (`LogToStandardErrorThreshold` in `Program.cs`);
- the MCP client launches the server process (see README for configuration).

HTTP/SSE transport could be added later (`ModelContextProtocol.AspNetCore`), but stdio is
the simplest choice for a local tool running beside Tekla.

## Request flow

```
Client → JSON-RPC (stdin) → MCP SDK → tool method [tekla_*]
       → ITeklaModelService (Mock | Tekla)
       → (Windows) Tekla Model API → open model
                         Drawing API → drawing list / active editor
       → DTO (TeklaMcp.Core.Models.*) → JSON → (stdout) → client
```

## Fallback: two-process design

If the `net48` build hits dependency conflicts (common on .NET Framework: `System.Text.Json`,
binding redirects), a fallback architecture is:

- MCP server stays on `net8.0` (cross-platform);
- a small separate `net48` worker references Tekla and talks to the server over stdio or
  named pipes.

The single-process design is preferred for simplicity; the two-process option is documented
here so it does not need to be rediscovered. See [tekla-api-notes.md](tekla-api-notes.md)
for dependency notes.
