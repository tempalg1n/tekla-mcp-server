# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Universal multi-version Tekla support.** One live build now works with any installed Tekla (2021+): it compiles against a baseline API and loads the Tekla DLLs from the running Tekla at runtime (`TeklaAssemblyResolver`), so no per-version artifact or download is needed. The server auto-detects the running Tekla (override with the `TEKLA_BIN_DIR` env var). Build overrides: `-p:TeklaVersion=` (compile baseline) and `-p:TeklaBinDir=` (local install).
- `tools/TeklaApiDoc` — cross-platform generator (metadata-only via `MetadataLoadContext`) that emits a grep-friendly Markdown reference of the Tekla Open API for offline signature verification. Output (`reference/`) is git-ignored. Developer/agent aid; no change to the server.

### Changed

- `docs/tekla-api-notes.md` now points to the local API reference and marks the write-path API calls as signature-verified (runtime behavior still to confirm on a live model)

## [0.3.0] - 2026-06-22

First release with **write** capability: agents can now create, edit and delete model objects — preview-by-default with `apply=true` to commit, and `MCP_ORIGIN` tagging for traceability. Adds geometry/grid resolution, parametric generators, and "find & fix" tools for columns.

### Added

- **Write operations (create / edit / delete), preview-by-default with `apply=true` to commit; created/modified objects tagged with the `MCP_ORIGIN` UDA:**
  - Geometry/grids: `tekla_list_grids`, `tekla_resolve_point` (translate axis labels like `1`/`Д` + elevation into coordinates)
  - Primitives: `tekla_create_beam`, `tekla_create_column`, `tekla_create_plate`, `tekla_modify_part`, `tekla_swap_handles`, `tekla_delete_objects`
  - Generators: `tekla_generate_frame`, `tekla_create_column_grid`, `tekla_create_beam_between_grids`
  - Fixers: `tekla_straighten_columns` (re-plumb crooked columns), `tekla_fix_column_handles` (re-orient flipped columns)
  - Core: `ITeklaModelService.GetGrids/ResolvePoint/CreateParts/ModifyParts/DeleteObjects` and new DTOs (`PartSpec`, `PartModification`, `WriteResult`, `Point3D`, `GridLineInfo`, `PointResult`)
- Grid coordinate parsing supports repeat syntax (e.g. `4*6000`) for more robust axis resolution

## [0.2.2] - 2026-06-22

Capability expansion: assembly analytics, a model-quality (QA) check battery, a generic property reader, table export, and a `useSelection` scope switch across the query/analytics tools.

### Added

- New tool `tekla_get_properties` — read any named properties (report properties, UDAs, or built-ins like `VOLUME`/`AREA`/`WEIGHT`) for an object by GUID, without a dedicated tool per field
- New tool `tekla_export_objects` — export filtered objects as a CSV or Markdown table (bill-of-materials handoff)
- New tool `tekla_list_assemblies` — assembly marks (`ASSEMBLY_POS`) with part count and total weight per mark
- New tool `tekla_count_assemblies` — count distinct assembly marks (unique assembly types)
- New tool `tekla_get_assembly_parts` — list all parts sharing a given assembly mark
- New tool `tekla_find_modeling_issues` — QA battery (missing material/profile/class, zero weight, not-numbered) grouped with sample GUIDs
- `useSelection` scope switch on query/analytics tools to operate on the current Tekla UI selection instead of the whole model
- `assembly` group key for `tekla_group_weight_by` and `tekla_list_distinct_values`
- Core: `ITeklaModelService.GetProperties`, `ObjectQuery.UseSelection`, and a new `QaReport` DTO

### Changed

- Tekla and Mock backends route filter-based scans (`FindObjects`, `SelectObjects`, `SetUdas`) through a shared source selector so every filter tool honors `useSelection`

## [0.2.1] - 2026-06-19

Patch release focused on improving `tekla_select_objects` usability for agent workflows.

### Added

- `tekla_select_objects` now accepts `guidIn` (comma/semicolon/newline separated list of GUIDs) for explicit object allow-list selection

### Changed

- `tekla_select_objects` description now explicitly documents UDA/attribute-capable filtering
- Object query pipeline in both Mock and Tekla backends now honors GUID allow-list filtering, enabling deterministic selection passes after discovery

## [0.2.0] - 2026-06-19

Major MCP capability expansion focused on attribute discoverability, spatial context, and profile connection analytics.

### Added

- New tool `tekla_find_attributes_by_value` to discover likely attribute names from a known value (for cases like `BK1`)
- New tool `tekla_analyze_profile_connections` to estimate unique connection/node types for a profile using beam-end proximity
- Generic attribute filters in query/workflow tools (`attributeName`, `attributeEquals`, `attributeContains`)
- Generic UDA + report property lookup support in `ITeklaModelService` backends
- New core DTOs for attribute match results and profile connection summaries

### Changed

- `ModelObjectInfo` now includes spatial data (`Center*`, `Start*`/`End*`, `Min*`/`Max*`) so agents can reason about coordinates
- `tekla_find_objects`, `tekla_count_objects`, `tekla_sum_weight`, `tekla_group_weight_by`, `tekla_list_distinct_values`, and `tekla_select_objects` now support UDA + generic attribute filtering
- Mock model now provides deterministic geometric coordinates and sample UDA values (including `RU_FN1_MRK=BK1` on columns)
- Tekla backend mapping now reads beam endpoints and solid bounding boxes (best effort, with graceful fallback)

## [0.1.1] - 2026-06-19

Recommended public release. Adds open-source documentation and project polish that were not included in v0.1.0.

### Added

- MIT [LICENSE](LICENSE)
- [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/releasing.md](docs/releasing.md)
- English [README.md](README.md) as the primary documentation
- Russian [README.ru.md](README.ru.md) as a secondary translation
- CI status badge in README

### Changed

- Rewrote project documentation for GitHub open-source audience
- [AGENTS.md](AGENTS.md), [docs/architecture.md](docs/architecture.md), and [docs/tekla-api-notes.md](docs/tekla-api-notes.md) cleaned up and aligned with public release
- Updated `.csproj` comments to remove internal dev-machine references

### Removed

- `TeklaMcp.Mac.slnf` (platform-specific solution filter no longer needed in public docs)

## [0.1.0] - 2026-06-19

Initial tagged release. Core MCP server and release automation.

### Added

**MCP tools (15):**

- `tekla_get_connection_info` — connection status and active backend
- `tekla_get_model_summary` — model-wide object count, weight, and breakdowns
- `tekla_list_objects` / `tekla_find_objects` — list and search with filters
- `tekla_get_object_by_guid` — single object lookup
- `tekla_get_selected_objects` — read current Tekla UI selection
- `tekla_analyze_by_material` — material breakdown (count + weight)
- `tekla_count_objects` / `tekla_sum_weight` — filtered count and weight sum
- `tekla_group_weight_by` / `tekla_list_distinct_values` — grouped analytics
- `tekla_select_objects` — programmatic UI selection by filter
- `tekla_get_object_udas` — read user-defined attributes
- `tekla_set_object_udas` / `tekla_set_udas_by_filter` — UDA writes with preview-by-default (`apply=false`)

**Architecture:**

- `ITeklaModelService` abstraction with Mock and Tekla backends
- Multi-target server: `net8.0` (mock, no Tekla) and `net48` (live Tekla on Windows)
- MCP stdio transport via the official C# SDK

**Release infrastructure:**

- GitHub Actions CI and automated release workflow
- Issue and pull request templates
- `CHANGELOG.md` and release notes template

### Notes

- Tekla NuGet packages pinned to `2023.0.0` for tested environments
- UDA write tools require explicit `apply=true` to modify the model
- Not affiliated with Trimble or Tekla Structures

[Unreleased]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.2.2...v0.3.0
[0.2.2]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/tempalg1n/tekla-mcp-server/releases/tag/v0.1.0
