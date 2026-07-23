# TeklaApiDoc — Tekla Open API reference generator

A small cross-platform utility that emits a **grep-friendly Markdown reference** of the Tekla
Open API (signatures + XML-doc summaries) so contributors and AI agents can verify API calls
offline instead of clicking through the web docs page by page.

It reads assemblies **metadata-only** via `System.Reflection.MetadataLoadContext`, so it runs on
any OS and can read the `net40`/`net48` Tekla DLLs from this `net8.0` tool (no Tekla install or
Windows required).

> ⚠️ **Output is not committed.** The generated `reference/` folder is Trimble's documentation
> content — it is git-ignored. Each developer regenerates it locally. Only this generator is
> committed.

## Get the Tekla assemblies

They ship as public NuGet packages (the same ones `src/TeklaMcp.Tekla` references). Easiest is
to download + extract them (they also include the XML docs used for descriptions):

```bash
ver=2023.0.1
for id in tekla.structures tekla.structures.model tekla.structures.drawing; do
  curl -sSL -o "/tmp/$id.nupkg" \
    "https://api.nuget.org/v3-flatcontainer/$id/$ver/$id.$ver.nupkg"
  unzip -oq "/tmp/$id.nupkg" -d "/tmp/$id"
done
# DLLs + XML are under /tmp/<id>/lib/<tfm>/  (e.g. lib/net40)
```

(Or point `--dll-dir` at a Tekla install's `bin` folder on Windows.)

## Generate

```bash
dotnet run --project tools/TeklaApiDoc -c Release -- \
  --dll-dir /tmp/tekla.structures.model/lib/net40 \
  --dll-dir /tmp/tekla.structures.drawing/lib/net40 \
  --dll-dir /tmp/tekla.structures/lib/net40 \
  --namespace Tekla.Structures \
  --out reference/tekla-api
```

Output: one `*.md` per type (full constructor/property/method signatures, `ref`/`out` params,
enum values, summaries) plus `INDEX.md` (every type → file).

## Navigate

```bash
grep -rl "GetReportProperty" reference/tekla-api      # which types declare it
grep -i "AddContourPoint" reference/tekla-api/Tekla.Structures.Model.ContourPlate.md
grep -rl "CreateSectionView" reference/tekla-api      # Drawing API declaration
```

## Options

| Flag | Meaning |
|---|---|
| `--dll <path>` | add one assembly (repeatable) |
| `--dll-dir <dir>` | add all `*.dll` in a directory (repeatable) |
| `--xml-dir <dir>` | XML docs directory (also auto-reads `*.xml` next to DLLs) |
| `--namespace <ns>` | document only this namespace prefix (repeatable; default `Tekla.Structures`) |
| `--out <dir>` | output directory (default `reference/tekla-api`) |

## Notes

- Verifies the API **surface** (signatures/overloads/enums). Runtime behavior (units, coordinate
  effects, grid string format) still needs a live model — see [../../docs/tekla-api-notes.md](../../docs/tekla-api-notes.md).
- Include `tekla.structures.drawing` whenever working on drawing tools. The generator will then
  cover `Tekla.Structures.Drawing`, `.Drawing.UI`, `.Drawing.Automation`, and
  `Tekla.Structures.DrawingInternal` types available in that version.
- A few deep-internal types are skipped when their private dependencies aren't in the package;
  that's expected and does not affect the public API.
- Targets `net8.0` with `RollForward=Major`, so it also runs on a newer installed runtime (.NET 10).
