# Contributing

Thank you for your interest in Tekla MCP Server. This project is in active development — contributions, bug reports, and feedback are welcome.

## Getting started

1. Fork the repository and clone your fork.
2. Install the [.NET SDK 8+](https://dotnet.microsoft.com/download).
3. For live Tekla integration: Windows x64, Tekla Structures with an open model, and the .NET Framework 4.8 Developer Pack.

```powershell
dotnet build TeklaMcp.sln -c Release
dotnet run --project src/TeklaMcp.Server -f net48 -c Release
```

Without Tekla, use the mock backend:

```bash
dotnet run --project src/TeklaMcp.Server
```

### Build for your Tekla version

The live build must match your installed Tekla version (the Open API DLLs are version-locked to
the running Tekla). Default is `2023.0.0`; override it:

```powershell
# from NuGet (versions 2021.0.0 .. 2026.0.x)
dotnet build TeklaMcp.sln -c Release -p:TeklaVersion=2021.0.0
# or from a local Tekla install 'bin'
dotnet build TeklaMcp.sln -c Release -p:TeklaBinDir="C:\Program Files\Tekla Structures\2021.0\bin"
```

See [docs/tekla-api-notes.md](docs/tekla-api-notes.md#tekla-version-compatibility) for details.

## Adding a new capability

Follow the pattern documented in [AGENTS.md](AGENTS.md):

1. Add a method to `ITeklaModelService` in `src/TeklaMcp.Core/`.
2. Implement it in both `MockTeklaModelService` and `TeklaModelService`.
3. Expose it as an MCP tool in `src/TeklaMcp.Server/Tools/`.
4. Update the tool table in [README.md](README.md).

Tool names: `tekla_<verb>_<noun>`, snake_case. Prefer read-only verbs (`get`, `list`, `find`, `analyze`) unless there is an explicit need for mutating operations with a safety gate.

## Pull requests

- Keep changes focused and match existing code style.
- Do not add Tekla API references outside `src/TeklaMcp.Tekla/`.
- Never write to stdout in server code — MCP uses stdout for JSON-RPC.
- Update documentation when behavior or architecture changes.
- Add user-facing changes to `[Unreleased]` in [CHANGELOG.md](CHANGELOG.md).

## Releases

Maintainers: see [docs/releasing.md](docs/releasing.md) for tagging, automated builds, and attaching release binaries.

## Reporting issues

Include:

- Tekla Structures version (if applicable)
- .NET SDK version (`dotnet --version`)
- Steps to reproduce
- Expected vs actual behavior

## Code of conduct

Be respectful and constructive. This is an open-source community project with no official affiliation to Trimble or Tekla.
