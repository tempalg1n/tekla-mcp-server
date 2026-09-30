# Contributing

Thank you for your interest in Tekla MCP Server. This project is in active development — contributions, bug reports, and feedback are welcome.

## Getting started

1. Fork the repository and clone your fork.
2. Install the [.NET SDK 8+](https://dotnet.microsoft.com/download). A newer SDK/runtime alone
   (e.g. .NET 10) is fine: the executables and tests set `RollForward=Major`.
3. For live Tekla integration: Windows x64, Tekla Structures with an open model, and the .NET Framework 4.8 Developer Pack.

```powershell
dotnet build TeklaMcp.sln -c Release -p:TeklaVersion=2023.0.1   # your Tekla, see below
dotnet run --no-build --project src/TeklaMcp.Server -f net48 -c Release
```

(`--no-build` keeps the build you just made; a plain `dotnet run` would rebuild against the
default Tekla 2021 API.)

Without Tekla, use the mock backend (any OS):

```bash
dotnet run --project src/TeklaMcp.Server
```

### Checks to run before a pull request

```bash
dotnet build src/TeklaMcp.Server -c Release
dotnet test tests/TeklaMcp.Tests -c Release
# MCP stdio smoke — the second argument is the exact number of registered tools:
dotnet run --project tests/TeklaMcp.Smoke -c Release -- \
  src/TeklaMcp.Server/bin/Release/net8.0/TeklaMcp.Server.dll 116
```

If you changed anything under `src/TeklaMcp.Tekla/`, also compile the solution for every
supported Tekla version — this works on macOS/Linux too, and CI does the same:

```bash
for v in 2021.0.0 2022.0.10715 2023.0.1 2024.0.4 2025.0.0 2026.0.3; do
  dotnet build TeklaMcp.sln -c Release -p:TeklaVersion=$v || break
done
```

### Tekla version

Live builds are **per-Tekla-version**: the artifact is compiled against one Tekla API and only
talks to that Tekla (the Open API protocol is version-locked; a mismatch fails fast with a
clear message). Pass the `TeklaVersion` matching your installed Tekla — it is a NuGet package
version, year first (exact strings in [docs/releasing.md](docs/releasing.md)):

```powershell
dotnet build TeklaMcp.sln -c Release -p:TeklaVersion=2023.0.1
```

The Tekla DLLs are not bundled; they load at runtime from your installed Tekla. If the server
can't locate it automatically, set `TEKLA_BIN_DIR` to the Tekla `bin` folder. See
[docs/tekla-api-notes.md](docs/tekla-api-notes.md#tekla-version-compatibility) for details.

## Adding a new capability

Follow the pattern documented in [AGENTS.md](AGENTS.md):

1. Add a method to `ITeklaModelService` in `src/TeklaMcp.Core/`.
2. Implement it in both `MockTeklaModelService` and `TeklaModelService` (a new area gets its own
   partial file on each side; logic both backends share goes into Core with unit tests).
3. Expose it as an MCP tool in `src/TeklaMcp.Server/Tools/`.
4. Update the tool table and the tool count in [README.md](README.md).
5. **Adding or removing a tool:** change the expected tool count passed to `tests/TeklaMcp.Smoke`
   in BOTH `.github/workflows/ci.yml` and `.github/workflows/release.yml`. The smoke asserts the
   exact number, so CI fails otherwise.

Tool names: `tekla_<verb>_<noun>`, snake_case. Prefer read-only verbs (`get`, `list`, `find`, `analyze`) unless there is an explicit need for mutating operations with a safety gate. Mutating tools follow the write conventions in [AGENTS.md](AGENTS.md): `apply=false` preview by default, a batch `limit`, the optional `expectedModelPath`, and the `ToolHelpers.Write(...)` wrapper so every result carries `outcome` and `target`.

## Pull requests

- Keep changes focused and match existing code style.
- Do not add Tekla API references outside `src/TeklaMcp.Tekla/`.
- Never write to stdout in server code — MCP uses stdout for JSON-RPC.
- Update documentation when behavior or architecture changes (conventions live in [AGENTS.md](AGENTS.md)).
- Add user-facing changes to `[Unreleased]` in [CHANGELOG.md](CHANGELOG.md).

## Releases

Maintainers: see [docs/releasing.md](docs/releasing.md) for tagging, automated builds, and attaching release binaries.
A version bump touches three places that the smoke checks against each other: `<Version>` in
`Directory.Build.props`, the version string in `tests/TeklaMcp.Smoke/Program.cs`, and the fallback
version in `src/TeklaMcp.Server/Program.cs`.

## Reporting issues

Include:

- Tekla Structures version (if applicable) and which release zip / `TeklaVersion` you run
- The output of `tekla_get_connection_info` and the server's stderr log, for connection problems
  (`tekla_report_gap` drafts an issue for you)
- .NET SDK version (`dotnet --version`)
- Steps to reproduce
- Expected vs actual behavior

## Code of conduct

Be respectful and constructive. This is an open-source community project with no official affiliation to Trimble or Tekla.
