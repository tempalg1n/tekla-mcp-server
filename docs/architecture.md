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

The service surface stays at backend primitives: queries/geometry/analytics, batched part
create/modify/delete, batched connection create/modify, component creation, UDA writes (single,
by filter, from a file), model save/open, environment reads (advanced options, catalogs), raw
scene geometry and view capture for the visual tools, script execution, plus stateful Drawing
API query/write batches. High-level generators or future detail replication belong in the tool
layer and compose those primitives.

Both backends are split into one partial file per area (`MockTeklaModelService.<Area>.cs`,
`Tekla<Area>Service.cs`). Anything both backends must compute identically lives in `TeklaMcp.Core`
and is unit-tested there without Tekla: aggregation and catalog paging, grid/curve/solid math,
file-exchange formats and the path allow-list, the schematic renderer, write outcomes, and the
classification of lost-connection errors. A mock answer and a live answer therefore differ only in
where the raw data came from.

### Five projects around the interface

| Project | Target | Holds |
|---|---|---|
| `TeklaMcp.Core` | `netstandard2.0` | the interface, flat DTOs, shared Tekla-free logic |
| `TeklaMcp.Mock` | `netstandard2.0` | synthetic model + drawings |
| `TeklaMcp.Scripting` | `netstandard2.0` | the `tekla_run_csharp` pipeline (policy → compile → execute, approval hash, reference selection, capped JSON) and the offline API reference (generator + search). Tekla-free: the live backend hands it the Tekla assemblies at runtime |
| `TeklaMcp.Tekla` | `net48`, x64 | the live backend: Open API calls, assembly loading, the remoting channel |
| `TeklaMcp.Server` | `net8.0` (+ `net48` on Windows) | MCP host and the `tekla_*` tool classes |
Reference-model objects are an explicit exception to GUID-first addressing: Tekla commonly
reports an empty GUID for them, so reference geometry is addressed by the integer model-object
ID from the current session.

### Pictures for agents

Two tools return images (MCP `ImageContentBlock`) next to their JSON:

- `tekla_render_schematic` (**beta**): the backend's `GetSchematicScene` delivers only raw
  geometry; `TeklaMcp.Core.Rendering` projects and draws it (own rasterizer, bitmap font, PNG
  encoder — Core stays dependency-free), so mock and live pictures come from the same code.
- `tekla_capture_view`: the live backend copies the pixels Tekla rendered in its own view window
  (`TeklaWindowCapture`, Win32 `PrintWindow`), optionally after a restorable zoom/highlight through
  the Model.UI API. The mock answers with a schematic stand-in labelled as such.

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

### Why `netstandard2.0` for Core, Mock and Scripting

`netstandard2.0` is the common denominator understood by both modern `.NET 8` and
`.NET Framework 4.8`, so the same `Core`/`Mock`/`Scripting` assemblies plug into both server
builds. It is also why Core stays dependency-free (the schematic PNG rasterizer, font and encoder
are hand-rolled) and why Roslyn stays on the 4.9.x line, the last to target `netstandard2.0`.

### Why the server multi-targets `net8.0` and `net48`

| Component | Supported targets |
|---|---|
| MCP C# SDK (`ModelContextProtocol`) | `netstandard2.0`, `net8.0` |
| Tekla Open API | `.NET Framework 4.8`, `netstandard2.0` |

Both are compatible with `netstandard2.0`, so a single **`net48` process on Windows**
can host the MCP SDK and Tekla Open API together — no two-process split required. Hence:

- **`net8.0`** — cross-platform build with `Core` + `Mock` + `Scripting` only. Runs without Tekla.
- **`net48`** — Windows build that additionally references `TeklaMcp.Tekla` for the live
  backend.

Backend selection is via `#if NET48` in `Program.cs`. Set `TEKLA_MCP_USE_MOCK=1` to force
the mock backend even in the `net48` build.

### Per-Tekla-version builds and startup order

`TeklaMcp.Tekla` compiles against ONE Tekla version (`-p:TeklaVersion`, one release zip per
year 2021–2026) because the Open API remoting protocol is version-locked. The Tekla DLLs are not
shipped; in the live build `Program.cs` starts in a fixed order:

1. `TeklaAssemblyResolver.Register()` — finds the installed Tekla's Open API folder and refuses a
   Tekla of another year ("wrong build") instead of binding to it.
2. `TeklaRemotingChannel.Align()` — on 2021–2023 builds, points the Open API clients at the
   named-pipe channels the running Tekla actually publishes (2024+ name their channels
   themselves and are left alone).
3. `TeklaBackendFactory.Create()` — constructs `TeklaModelService` only after the version check,
   because a failed static initializer would be cached for the life of the process.

The history of why universal builds and assembly-loading tricks were abandoned is in
[tekla-api-notes.md](tekla-api-notes.md) ("Assembly loading history").

### Why `net48` is disabled on non-Windows

In `TeklaMcp.Server.csproj`:

```xml
<TargetFrameworks Condition="'$(OS)' == 'Windows_NT'">net8.0;net48</TargetFrameworks>
<TargetFrameworks Condition="'$(OS)' != 'Windows_NT'">net8.0</TargetFrameworks>
```

Non-Windows builds of the server target only `net8.0` and never reference the
`TeklaMcp.Tekla` project. The project itself still compiles on any OS as part of `TeklaMcp.sln`
(the .NET SDK supplies the .NET Framework reference assemblies), which is how every
`TeklaVersion` can be compile-checked without Windows; it just cannot run there.

## MCP transport: stdio

The server communicates over **stdio** (JSON-RPC on stdin/stdout). Therefore:

- stdout is reserved for the protocol — logs go **only to stderr**
  (`LogToStandardErrorThreshold` in `Program.cs`), and `Console.Out` itself is redirected to
  stderr at startup because the Tekla Open API writes lines such as "Connection failed" to the
  console, which would corrupt the JSON-RPC framing;
- the MCP client launches the server process (see README for configuration).

## Process lifetime

Closing stdin stops the host. On Windows `ShutdownGuard` adds two guards against orphaned
servers that keep holding a Tekla connection: the process exits when the process that launched
it exits (opt out with `TEKLA_MCP_EXIT_WITH_PARENT=0`), and a shutdown that hangs for 10 s is cut
short — an in-flight Open API call cannot be cancelled.

## Connection lifecycle (2021–2023)

The Open API talks to Tekla over .NET remoting. A client created while Tekla's channel is not
published is dead for the whole process, and after a Tekla restart the old proxies still report
"connected" until the first real call fails. So the live backend:

- creates no Model/Drawing client until the channel is published (`EnsureTeklaReady`), answering
  "not reachable, nothing was sent" instead;
- starts every tool call with one real `GetInfo()` (`GetConnectedModel()`), and when that fails
  swaps fresh remoting objects into the existing clients on the SAME channel
  (`TeklaRemotingChannel.TryReconnect`) before the tool reads or writes anything — nothing is
  retried, so a write is never repeated;
- never adopts a Tekla under another channel name (it may be a different Tekla instance).

Tekla 2024+ uses Trimble.Remoting instead of named pipes; the guard and the reconnect are not
active there yet. Details and field history: [tekla-api-notes.md](tekla-api-notes.md) and
[backlog.md](backlog.md).

## Errors and write results

- **Tool errors carry their cause.** The MCP SDK replaces the message of every exception except
  `McpException` with a generic text. `ToolErrorFilter` (a call-tool filter registered in
  `Program.cs`) returns the flattened exception instead (`ErrorText.Flatten`), and in the live
  build replaces a lost connection (`ConnectionErrors`: a `RemotingException` or failed Tekla type
  initializer anywhere in the chain) with `TeklaRemotingChannel.DiagnoseConnectionFailure` —
  cause, action and channel state.
- **Writes say what happened and where.** The Open API has no transactions. Every mutating tool
  goes through a `ToolHelpers.Write(...)` wrapper that resolves the write target (model path +
  Tekla PID), refuses a mismatching `expectedModelPath` before the backend is called, and stamps
  `target` + `outcome` (`planned` / `not_written` / `committed` / `partial` / `unknown`) on the
  result. The live backend tracks progress with `WriteProgress`, so a failure after the first
  mutating call is `unknown` (an MCP error carrying the full result), never "not written".

## Files in, files out

Bulk data does not pass through the tool response (an LLM context). `tekla_export_parts_file`,
`tekla_export_reference_objects_file` and `tekla_set_udas_from_file` stream to/from jsonl/csv
files; format, paging, the `<path>.status.json` sidecar and the result DTO are shared in
`TeklaMcp.Core.FileExchange` (`ExportRunner`, `UdaImportRunner`), and every path passes
`FilePathPolicy` (absolute, allow-listed roots from `TEKLA_MCP_FILE_ROOT` or the defaults,
data-file extensions only). Backends supply only the row source or the UDA target.

HTTP/SSE transport could be added later (`ModelContextProtocol.AspNetCore`), but stdio is
the simplest choice for a local tool running beside Tekla.

## Request flow

```
Client → JSON-RPC (stdin) → MCP SDK → ToolErrorFilter → tool method [tekla_*]
       → (writes) ToolHelpers.Write: target check, outcome stamping
       → ITeklaModelService (Mock | Tekla)
       → (Windows) GetConnectedModel(): connection check / reconnect
                   Tekla Model API → open model
                   Drawing API → drawing list / active editor
                   Model.UI / Win32 PrintWindow → view capture
       → DTO (TeklaMcp.Core.Models.*) → JSON text block (+ PNG image block for the visual tools)
       → (stdout) → client
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
