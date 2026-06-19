## Summary

<!-- What does this PR change and why? -->

## Type of change

- [ ] Bug fix
- [ ] New MCP tool or capability
- [ ] Documentation
- [ ] CI / release infrastructure
- [ ] Refactor (no behavior change)

## Checklist

- [ ] Builds locally (`dotnet build TeklaMcp.sln -c Release` on Windows, or `dotnet build src/TeklaMcp.Server` for mock-only changes)
- [ ] Mock backend updated if `ITeklaModelService` changed
- [ ] Tekla backend updated if `ITeklaModelService` changed
- [ ] No `Console.WriteLine` to stdout (MCP uses stdout for JSON-RPC)
- [ ] README tool table updated (if tools changed)
- [ ] CHANGELOG.md updated under `[Unreleased]` (if user-facing)

## Test plan

<!-- How did you verify this? Mock backend, live Tekla, MCP Inspector, etc. -->

```text

```

## Breaking changes

<!-- Tool renames, removed parameters, changed response shapes — or "None" -->

None
