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
       → (Windows) Tekla Open API → open model
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
