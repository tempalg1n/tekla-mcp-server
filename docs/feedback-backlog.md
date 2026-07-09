# Agent-feedback backlog

Work accumulated on the `dev` branch in response to a session-report from an agent that
used the MCP server (release v0.6.0) for a run of plate/bolt geometry tasks. The agent's
headline: the server is strong at search, analytics and creating primitives, but weak at
**geometric operations over existing parts** and **domain filters** (bolts, intersections,
contours) — which is why `tekla_run_csharp` became the workhorse instead of the escape hatch
it was meant to be.

Each step below is sized so it can be built and tested against the mock on macOS; geometry
mutations (Step 3) additionally need a live-Tekla pass on Windows before release.

## Step 1 — cheap discoverability + filter fixes ✅ done

Closes the two `run_csharp` detours that were really missing filters, not missing geometry.

- [x] Friendly type aliases (`Bolt` → `BoltArray`/`BoltGroup`, `Plate` → `ContourPlate`) via a
      shared `TeklaTypeAliases`, applied by both backends. Fixes `type="Bolt"` → 0 results.
- [x] `attributeNotEquals` filter across the filter tools — e.g. bolts where `BOLT_GRADE != 88`
      in one `select_objects` call.
- [x] `group_weight_by` / `list_distinct_values` accept any attribute/UDA name as the field —
      e.g. `list_distinct_values(field="BOLT_GRADE", type="Bolt")` as a bolt-grade reference.
- [x] Tool descriptions steer agents to `BOLT_GRADE`/`BOLT_STANDARD` (not material/class) and
      document that `attributeName` alone already means "attribute is set" (so no separate
      `attributeExists` flag was needed).
- [x] Mock bolts made faithful to the live backend (type `BoltArray`, grade in `BOLT_GRADE`);
      covered by `tests/TeklaMcp.Tests/ObjectQueryFilterTests.cs`.

## Step 2 — geometric reading (read-only, mockable)

- [ ] `tekla_get_contour` — contour points + key points of a `ContourPlate` (and bbox, already
      on `ModelObjectInfo`, surfaced explicitly). Removes the repeated `GetSolid()` +
      `Contour.ContourPoints` scripting.
- [ ] `tekla_find_intersections(filterA, filterB, gap, useSelection)` — bbox (and later solid)
      overlap between two filtered sets, returned as pairs. Replaces the O(n×m) hand-rolled
      scan. Needs a spatial index so it isn't quadratic on large models.

## Step 3 — contour mutations (needs live-Tekla verification)

- [ ] `tekla_modify_plate_contour` — replace a `ContourPlate` contour with new points
      (preview-by-default, same write contract as the other mutations). `modify_part` only does
      start/end, which is useless for plates.
- [ ] `tekla_trim_plate_at_obstacle(plate, obstacleFilter, gapMm, minPartWidthMm, apply)` —
      composite tool over Steps 2–3: the "trim rib at gusset" task the agent hit 6+ times.
      Build last; it's the highest-value but the only one where the domain logic (clipping,
      min-width rule) can be subtly wrong, so it must run on a real model.

## Explicitly declined / already covered

- **`attributeExists` / `udaExists`** — passing `attributeName` with no value already requires
  the attribute to be present on both backends. Fixed by documentation, not a new flag.
- **Dedicated batch-preview tool** — the service layer already takes lists (`CreateParts`,
  `ModifyParts`) with preview-by-default. When a batch tool is needed, give the existing
  create/modify tools a list input; the preview contract already covers the "plan" step.
- **`bolt` enum in the live `TypeEnumMap`** — left out on purpose. The alias resolves in
  `Matches` over a full scan, which is correct without an unverifiable enum fast path.
