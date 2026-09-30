## Summary

<!-- What does this PR change and why? -->

## Type of change

- [ ] Bug fix
- [ ] New MCP tool or capability
- [ ] Documentation
- [ ] CI / release infrastructure
- [ ] Refactor (no behavior change)

## Checklist

- [ ] Builds locally (`dotnet build src/TeklaMcp.Server -c Release`; `dotnet build TeklaMcp.sln -c Release -p:TeklaVersion=<v>` if `src/TeklaMcp.Tekla/` changed)
- [ ] Unit tests pass (`dotnet test tests/TeklaMcp.Tests -c Release`)
- [ ] MCP smoke passes (`dotnet run --project tests/TeklaMcp.Smoke -c Release -- src/TeklaMcp.Server/bin/Release/net8.0/TeklaMcp.Server.dll <tool-count>`)
- [ ] `src/TeklaMcp.Tekla/` changes compile for all six `TeklaVersion`s (2021.0.0, 2022.0.10715, 2023.0.1, 2024.0.4, 2025.0.0, 2026.0.3) — CI checks this too
- [ ] Mock backend updated if `ITeklaModelService` changed
- [ ] Tekla backend updated if `ITeklaModelService` changed
- [ ] No `Console.WriteLine` to stdout (MCP uses stdout for JSON-RPC)
- [ ] Tools added/removed: README tool table + count updated, and the smoke tool count bumped in BOTH `.github/workflows/ci.yml` and `.github/workflows/release.yml`
- [ ] Write tools: `apply=false` by default, batch `limit`, `expectedModelPath`, routed through `ToolHelpers.Write(...)`
- [ ] AGENTS.md / docs updated if conventions or architecture changed
- [ ] CHANGELOG.md updated under `[Unreleased]` (if user-facing)

## Test plan

<!-- How did you verify this? Mock backend, unit tests, MCP smoke, live Tekla (which version?), MCP Inspector, etc. -->

```text

```

## Breaking changes

<!-- Tool renames, removed parameters, changed response shapes — or "None" -->

None
