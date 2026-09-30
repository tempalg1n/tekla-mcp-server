# TeklaApiDoc — Tekla Open API reference generator (CLI)

A small cross-platform utility that emits a **grep-friendly Markdown reference** of the Tekla
Open API (signatures + XML-doc summaries) so contributors and AI agents can verify API calls
offline instead of clicking through the web docs page by page.

> **The server does this by itself (since v0.8.0).** The generator lives in `TeklaMcp.Scripting`
> (`ApiReferenceGenerator`); this CLI is a thin wrapper over the same code and produces the same
> page format. Use the CLI for a repository copy (`reference/tekla-api`) or to prepare a folder for
> `TEKLA_MCP_API_REF_DIR`. See [How the server finds a reference](#how-the-server-finds-a-reference).

It reads assemblies **metadata-only** via `System.Reflection.MetadataLoadContext` (no Tekla code
runs, nothing is loaded into the process), so it runs on any OS and can read the `net40`/`net48`/
`netstandard2.0` Tekla DLLs from this `net8.0` tool — no Tekla install or Windows required.

> ⚠️ **Output is not committed.** The generated `reference/` folder is Trimble's documentation
> content — it is git-ignored. Each developer regenerates it locally. Only this generator is
> committed.

## Get the Tekla assemblies

They ship as public NuGet packages (the same ones `src/TeklaMcp.Tekla` references) and include
the XML docs used for descriptions. Either download + extract them:

```bash
ver=2023.0.1   # exact NuGet version per Tekla year: see docs/releasing.md
for id in tekla.structures tekla.structures.model tekla.structures.drawing tekla.structures.catalogs; do
  curl -sSL -o "/tmp/$id.nupkg" \
    "https://api.nuget.org/v3-flatcontainer/$id/$ver/$id.$ver.nupkg"
  unzip -oq "/tmp/$id.nupkg" -d "/tmp/$id"
done
# DLLs + XML are under /tmp/<id>/lib/<tfm>/
```

…or, after any build of the net48 backend has restored them, use the NuGet cache directly:
`~/.nuget/packages/<id>/<ver>/lib/<tfm>/`.

The `<tfm>` folder depends on the version: `net40` for 2021–2023, `net48` and/or
`netstandard2.0` for 2024+ (the 2026 Model/Drawing/Catalogs packages ship `netstandard2.0` only).

The server documents seven assemblies (`ApiReference.CoreAssemblyNames`): `Tekla.Structures`,
`.Model`, `.Drawing`, `.Catalogs`, `.Datatype`, `.Dialog`, `.Plugins`. For the same coverage, add
`tekla.structures.datatype`, `tekla.structures.dialog` and `tekla.structures.plugins` to the loop.

On Windows you can instead point `--dll-dir` at an installed Tekla's Open API folder — `bin` on
2023+, **`nt\bin\plugins` on 2021** (the server resolves some 2021 dependencies from `nt\bin`
as well; the CLI has no dependency-only option, so a type whose dependencies are missing is
skipped with a log line).

## Generate

```bash
dotnet run --project tools/TeklaApiDoc -c Release -- \
  --dll-dir /tmp/tekla.structures.model/lib/net40 \
  --dll-dir /tmp/tekla.structures.drawing/lib/net40 \
  --dll-dir /tmp/tekla.structures.catalogs/lib/net40 \
  --dll-dir /tmp/tekla.structures/lib/net40 \
  --namespace Tekla.Structures \
  --out reference/tekla-api
```

Output: one `*.md` per public type (full constructor/property/method/field signatures,
`ref`/`out` params, enum values, XML-doc summaries) plus `INDEX.md` (every type → file). Files are
written into `--out` as they are generated and old pages are not removed — regenerate into an empty
folder when switching Tekla versions.

The project is not part of `TeklaMcp.sln`; `dotnet run --project` builds it together with
`TeklaMcp.Scripting`, which it references.

## Navigate

```bash
grep -rl "GetReportProperty" reference/tekla-api      # which types declare it
grep -i "AddContourPoint" reference/tekla-api/Tekla.Structures.Model.ContourPlate.md
grep -rl "CreateSectionView" reference/tekla-api      # Drawing API declaration
```

Agents get the same through the MCP tools `tekla_search_api` and `tekla_get_api_doc`.

## Options

| Flag | Meaning |
|---|---|
| `--dll <path>` | add one assembly (repeatable) |
| `--dll-dir <dir>` | add all `*.dll` in a directory (repeatable) |
| `--xml-dir <dir>` | extra XML-doc directory (repeatable); only files named like a documented DLL are read. `*.xml` next to each DLL is always read |
| `--namespace <ns>` | document only this namespace prefix (repeatable; default `Tekla.Structures`) |
| `--out <dir>` | output directory (default `reference/tekla-api`) |
| `-h`, `--help` | usage |

## How the server finds a reference

`tekla_search_api`, `tekla_get_api_doc` and `tekla_get_api_reference_status` look in this order
(`ApiReference.Resolve`):

1. **`TEKLA_MCP_API_REF_DIR`** — a folder you generated (with this CLI, for example); origin
   `configured`.
2. **The cache for the exact Tekla build** the backend reports — origin `generated`:
   `%LOCALAPPDATA%\TeklaMcp\api-reference\<key>-r<FormatVersion>`, where the key is
   `tekla-<file version of Tekla.Structures.Model.dll>` on the live backend. The live backend takes
   the seven assemblies above from the installed Tekla's Open API folder, with the XML docs Tekla
   ships next to them. The mock backend does the same from `TEKLA_MCP_SCRIPT_REF_DIR` (a folder
   holding `Tekla.Structures*.dll`).
3. **Background generation** into that cache when it does not exist yet (temp folder + rename, so a
   half-written reference is never served). A tool call waits up to 40 s for it — or not at all
   when a repository copy can be served meanwhile. A failed generation is reported, and retried
   only after a server restart. Verified in a net48 server against Tekla 2021: 1 740 types in ~7 s.
4. **A repository copy**: `reference/tekla-api` found by walking up from the server binary's folder
   or the current directory — origin `repository`.

`tekla_get_api_reference_status` reports which one is in use (`origin`) and whether a generation is
running (`generating`). Nothing from Trimble is bundled with the server; the generated cache stays on
the machine it was built on.

## Notes

- Verifies the API **surface** (signatures/overloads/enums). Runtime behavior (units, coordinate
  effects, grid string format) still needs a live model — see [../../docs/tekla-api-notes.md](../../docs/tekla-api-notes.md).
- Include `tekla.structures.drawing` whenever working on drawing tools. The generator will then
  cover `Tekla.Structures.Drawing`, `.Drawing.UI`, `.Drawing.Automation`, and
  `Tekla.Structures.DrawingInternal` types available in that version.
- A few deep-internal types are skipped when their private dependencies aren't in the package;
  that's expected and does not affect the public API.
- Targets `net8.0` with `RollForward=Major`, so it also runs on a newer installed runtime (.NET 10).
