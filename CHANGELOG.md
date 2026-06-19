# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- GitHub Actions CI and automated release workflow
- Issue and pull request templates

## [0.1.0] - 2026-06-19

First public release. Verified on Windows with Tekla Structures 2023.

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

**Documentation:**

- README (English) and README.ru.md (Russian)
- Architecture and Tekla API notes

### Notes

- Tekla NuGet packages pinned to `2023.0.0` for tested environments
- UDA write tools require explicit `apply=true` to modify the model
- Not affiliated with Trimble or Tekla Structures

[Unreleased]: https://github.com/tempalg1n/tekla-mcp-server/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/tempalg1n/tekla-mcp-server/releases/tag/v0.1.0
