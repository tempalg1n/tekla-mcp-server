# Backlog — discussed and deferred

Work that was analysed and consciously postponed, with enough of the findings to pick it up
later without redoing the investigation. Each entry says what is verified and what is not.

State as of **v0.8.0 (released 2026-09-30)**. Most of the DEV-005 items below shipped in v0.8.0;
their findings stay here because the open remainder builds on them.

| § | Item | State after v0.8.0 |
|---|---|---|
| 1 | Reconnect after a Tekla restart | Shipped for 2021–2023. Accepted on Tekla 2023; **Tekla 2021 acceptance open** |
| 2 | Explicit Tekla instance selection | **Open** (not started) |
| 3 | Tekla 2024+ transport | **Open** — needs testers with a 2024+ install |
| 4 | Write outcomes | Shipped. **Open:** live run of `unknown`, exact PID with several instances, finer drawing/script outcomes, the UDA paths that cannot report `unknown` |
| 5 | Read tools for component development | Shipped. **Open:** the live run of each tool (see §5) |
| 6 | Smaller follow-ups | Shipped. **Open:** why orphaned servers lived |
| 7 | Found after the first write-up | Fixes shipped. **Open:** AutoFetch snapshot cost, `GetAllObjectsWithType(Type[])` timing, partial version-mismatch recovery |
| 8 | `tekla_analyze_by_material` is an unpaged whole-model scan | **Open** (found in the v0.8.0 release review) |
| 9 | `tekla_export_drawings_pdf` writes outside the file allow-list | **By design** (user's responsibility, warned); opt-in restriction possible later |

The per-API verification ledger and the full list of live runs still pending are in
[docs/tekla-api-notes.md](tekla-api-notes.md) ("Live validation still pending").

---

## DEV-005 field report (Tekla 2021/2023) — discussed 2026-09-29

An agent developing a dependent (plugin) component — the KXMp Handrails project — hit repeated
server problems over several weeks and wrote them up (26 cases; the report and its evidence stay
with that project, not in this repo). Component UI bugs, the project's own external
inspector/writer tools, Windows screen automation (capture, UI Automation, focus) and disk access
were classified as not server issues and are out of scope here.

Done in the same round, shipped in v0.8.0 (see CHANGELOG):

- Script references on Tekla 2021, Open API folder detection, tool error text reaching the
  agent.
- Connection-failure diagnostics, server identity in `tekla_get_connection_info`,
  `expectedSha256` for `tekla_run_csharp`, script return-value truncation flag, catalog
  `Import*` members in the script policy.

The rest is below, most valuable first.

### 1. Reconnect after a Tekla restart without restarting the MCP client

**Status: shipped in v0.8.0 for Tekla 2021–2023; accepted on 2023, 2021 acceptance open; 2024+
not covered (§3).**

**Symptom.** After Tekla is closed and reopened on the same model, every tool fails with
`RemotingException: Failed to connect to an IPC port: The system cannot find the file
specified` until the MCP server process is restarted (seen on 2021 and 2023, several times).

**Findings** (decompiled 2021–2026 NuGet assemblies; probed read-only on a live Tekla 2023):

- `Model.GetConnectionStatus()` only checks `DelegateProxy.Delegate != null` — no round trip,
  so it keeps answering `true` after Tekla restarts. `DrawingHandler.GetConnectionStatus()`
  does a real round trip.
- The Open API client state is process-static: one
  `GenericDelegateProxy<ICDelegate, CDelegate>` per assembly
  (`TeklaStructuresInternal` / `ModelInternal` / `DrawingInternal` `.DelegateProxy`), holding a
  client-activated object created from `"ipc://" + Remoter.ChannelName`.
  `TeklaRemotingChannel.Align()` also runs once per server process.
- **A stale proxy is recoverable in-process:** create a fresh object with the public
  `Tekla.Structures.Internal.RemotingProxyHelper.CreateInstance<T>("ipc://" + channel)`
  (`T` = that proxy's `CDelegate`) and assign it through the public setter
  `GenericDelegateProxy<I, T>.Delegate` (the shipped code sets the static
  `{Asm}Internal.DelegateProxy.Delegate` property by reflection). Verified on live 2023 against a
  SIMULATED stale object (repeated three times), then against real Tekla restarts (acceptance
  below).
- **A poisoned proxy is not recoverable in-process:** if the first touch happens while the
  channel is not published, the static constructor fails and the CLR caches the
  `TypeInitializationException` for the process lifetime. Prevent it instead: check that the
  channel exists before the first touch and fail with a plain "not published" message without
  touching Tekla types.
- The field error ("cannot find the file" = the dialed pipe does not exist) points at the
  channel NAME changing between Tekla generations (see §2), not only at a stale object (the
  simulation gives "Requested service not found"). Check live which one happens. Partly answered
  by the 2023 acceptance: with a single instance, the restarted Tekla published the SAME channel
  name every time (the same-name reconnect worked in all three cycles), so the field error fits a
  call made while Tekla was down or still starting. A name change with several instances is still
  possible (§2).
- `DelegateProxy.Initialize()` exists only in some builds (not for Model/Drawing in NuGet
  2021.0.0 / 2023.0.1) — do not rely on it for 2021–2023.
- Public reset hooks that avoid reading the private `proxy` field (decompilation, untested):
  2021–2023 `CDelegateSetter.SetInstanceForUnitTesting(ICDelegate)` sets
  `DelegateProxy.Delegate`; 2024+ `CDelegateSetter.ResetInstance()` calls
  `DelegateProxy.Initialize()` (Model, Drawing and base assemblies).

**Plan.**

1. ~~Poisoning guard~~ — shipped in v0.8.0 for 2021–2023 (`TeklaRemotingChannel.EnsurePublished`,
   see `docs/tekla-api-notes.md`). On 2021 a poisoned Model client is a NULL delegate, not an
   exception (verified) — so step 3's recreate could also revive it, which widens what "poisoned"
   can mean here. Open: the positive half (server started with Tekla closed, first call after the
   model opens connects without a restart) is not verified live on 2021 or 2023.
2.–4. ~~Detect, recreate, never retry a write~~ — shipped in v0.8.0 (`ConnectModel` +
   `TeklaRemotingChannel.TryReconnect`, see `docs/tekla-api-notes.md` "Two different dead
   connections"). Deviations from the plan: the channel suffix is NOT re-derived — a reconnect
   goes to the same channel names only, because another name may be another Tekla (that stays
   §2); the Catalogs client is recreated too; the swap goes through `DelegateProxy.Delegate`, not
   `CDelegateSetter.SetInstanceForUnitTesting` (breaks catalogs, verified). Reads are not retried
   either: the check runs at the start of each call, before anything is read or written.
   Verified live on Tekla 2023 WITHOUT a restart (the swap, three rounds), then with real restarts
   (acceptance below). 2024+: not reconnected.

**Acceptance** (from the report): three normal restarts each of Tekla 2021 and 2023 with the MCP
server alive; the same model is reachable again without restarting the client; no write is
repeated blindly.

- **Tekla 2023 — passed 2026-09-30** (model 3219, one net48 server kept alive by a watcher that
  called `tekla_get_connection_info` + a read every 3 s): three close/reopen cycles 11:49–11:56,
  each "not running … call again" while closed (the guard refused, nothing dialed), then
  "reconnected (Tekla was restarted)" the moment the channel reappeared and `connected=True` on
  the same model. Known behaviour: the call that reconnects while Tekla is still opening the
  model waits for it (22–70 s measured) — a real MCP client may time out on that one call; the
  next one works. (Documented for users in the v0.8.0 CHANGELOG entry and release notes.)
- **Tekla 2021 — still open** (a v0.8.0 known limitation). Same procedure; also log whether the
  first `CreateInstance` after a restart needs the second attempt. The `TryReconnect` code comment
  still names both versions as its acceptance TODO.

### 2. Explicit Tekla instance selection

**Status: open (not started). v0.8.0 known limitation: with several instances of one Tekla
version the server binds to one and never switches.** What v0.8.0 already does around it: before
the first connection the guard re-aligns to whichever instance of the build's version publishes;
once connected, the reconnect (§1) and the guard refuse to adopt a channel under another
session/instance name ("possibly a DIFFERENT Tekla — restart the server"); write results carry the
Tekla PID when it can be told apart (§4), and `expectedModelPath` refuses a write aimed at another
model.

- Channel names are `{Assembly}-{SESSIONNAME}:{AssemblyVersion}`. A second or later Tekla of the
  same version appends its PID: `…-Console-<PID>:…` — the base `Remoter` static constructor does
  it when the plain name is already taken. From code reading; not yet verified with two live
  instances.
- `TeklaRemotingChannel.DeriveSessionSuffix` always takes the first suffix (the current
  `SESSIONNAME`, else ordinal order). With two Tekla 2023 open the server bound to the user's
  working model instead of the test model, and after that Tekla closed it kept dialing the
  closed channel.

**Plan.** List the published channels, map each suffix to a PID (`Console-<PID>`; the plain one
by elimination) and to its model path, and show the list in the connection info. Select an
instance explicitly (tool parameter or a pinned target) — switching reuses the §1 recreate.
Report `ambiguous` / `unreachable` instead of guessing. Before any write, re-check model path,
PID and process start time.

### 3. Tekla 2024+ transport

**Status: open — waits for testers.** v0.8.0 builds and ships 2024/2025/2026 zips, but nothing
connection-related has run on them: no connection guard, no in-process reconnect (a lost connection
needs a server restart), channel names from decompiled assemblies only.

2024–2026 use Trimble.Remoting (memory-mapped files and named kernel objects), not named pipes.
The channel is `{Assembly}-{ProductName}-{SESSIONNAME|Console}:{FileVersion}` and exists when
`EventWaitHandle.TryOpenExisting(channel + "$S")` succeeds (the guard's `TODO(windows)` in
`TeklaRemotingChannel.CheckChannel`). `Describe()` lists `\\.\pipe\` and therefore sees nothing on
these versions; `Align()` no longer touches 2024+ builds at all (fix shipped in v0.8.0, see
CHANGELOG). `DelegateProxy.Initialize()` plus `RemotingProxyHelper.DisposeService()` look like the
recreate path; the behaviour of a stale proxy after a restart is unknown — test on a live 2024+
before claiming support.

**Needs testers.** The maintainers have no 2024+ install, so this waits for field reports: README,
the v0.8.0 known limitations and the server's own 2024+ connection messages ask users of those
versions for issues (`tekla_report_gap` drafts one). The one 2024+ field source so far is issue
#15 (Tekla 2025, script references and curve geometry); nothing from v0.8.0 is confirmed there yet. What to ask a reporter for: the
`tekla_get_connection_info` output with Tekla running and after a Tekla restart, the server's
stderr, and whether `EventWaitHandle.TryOpenExisting("<model channel>$S")` sees the channel.

### 4. Write outcomes

Shipped in v0.8.0 (see CHANGELOG and the write-result outcome/target rules in AGENTS.md).
Differences from the plan:
`rejected` is not an outcome — a mismatching `expectedModelPath` refuses the call as a tool error
before the backend runs; `failed_before_write` became `not_written` (it also covers "Tekla
refused every item"), and `partial` was added (committed, some items refused). Still open:

- Live verification of the `unknown` path (break the connection mid-batch on a scratch model) —
  a v0.8.0 known limitation — and of the `Modify()` return value in `ModifyParts`: a refused
  edit must return false, and a no-op `Modify()` of an unchanged part must still return true
  (the `TODO(windows)` there), otherwise an unchanged part would be reported as refused.
- The UDA write paths are coarser than the rest (from code reading, v0.8.0):
  `ApplyUdaUpdates` counts a field once `SetUserProperty` accepted it and ignores/swallows the
  following `Modify()` (its own `TODO(windows)`: is `Modify()` needed for every object type);
  `tekla_set_object_udas` / `tekla_set_udas_by_filter` never call `CommitChanges()`. Decide once
  `Modify()`'s role for UDAs is known live. (The bulk import used to swallow an exception from its
  final `CommitChanges()` and report `committed`; fixed before the v0.8.0 release — it is now
  `unknown`, like the other write paths.) Listed as a known limitation in the v0.8.0 CHANGELOG.
  **Plan** (live, on a scratch model): (1) `SetUserProperty` alone, no `Modify()`/commit — read the
  value back from a freshly started server: does it persist? (2) the same with `Modify()` per
  object type (`Beam`, `ContourPlate`, `PolyBeam`, `Assembly`, `BoltGroup`, weld) and its return
  value; (3) whether a missing `CommitChanges()` loses anything on save. Then treat a `false`
  from a required `Modify()` as a refusal (as `ModifyParts` does) and give
  `tekla_set_object_udas` / `tekla_set_udas_by_filter` one `CommitChanges()` whose throw is
  `unknown` — or document why neither is needed.
- The Tekla PID is exact only when the channel names it or one instance of the version runs;
  with several instances on a plain channel it stays null (§2 makes it exact).
- Drawing writes are tracked coarsely (connection loss / failed commit → unknown).
- `tekla_run_csharp` gets the target and `expectedModelPath`, but no outcome: a script's own
  writes are not observable (`executed`/`executionAttempted` stay the signal).

### 5. Read tools for component development

**Status: all shipped in v0.8.0; open: the live run of each tool (four of them are v0.8.0 known
limitations).**

The APIs exist in the Tekla 2021 assemblies (checked by metadata):

- ~~Advanced options~~ and ~~catalogs~~ — shipped in v0.8.0: `tekla_get_advanced_options`,
  `tekla_list_catalog` (profiles, parametric profiles, materials, components, UDA definitions).
  Compiled against all six NuGet versions. `tekla_get_advanced_options` has run live on Tekla 2023:
  an unknown option answers `true` with an empty value, so an empty value is now `found=false`
  (fixed before the release); `asPaths` has no live record. `tekla_list_catalog` has not run live
  as a tool — only `CatalogHandler.GetMaterialItems()` did, in the reconnect probes on 2023. Open
  (known limitation): UDA definitions with `details=true` — do the enumerated items need
  `UserPropertyItem.Select()` (TODO(windows) in `TeklaEnvironmentService.cs`).
- ~~Plugin components with input objects and ordered points~~ — shipped in v0.8.0:
  `tekla_create_component` / `CreateComponents`, read back with children. Open (known
  limitation): run a real plugin (does Run happen inside `Insert()` or at `CommitChanges()`;
  attribute types).
- ~~Save and open without the UI dialog~~ — shipped in v0.8.0: `tekla_save_model`,
  `tekla_open_model` (refuses on unsaved changes unless discarded explicitly). Open (known
  limitation): whether Tekla dialogs (locked model, upgrade) block `Open()`.
- ~~Part solids~~ — shipped in v0.8.0: `tekla_get_part_solid` (compiled 2021–2026). Open (known
  limitation): check loop winding and whether `OriginPartId` names cutting parts as documented.
- ~~API reference without manual generation~~ — shipped in v0.8.0: metadata-only generation from
  the installed Tekla, cached per build (verified in a net48 server against Tekla 2021: 1 740 types,
  ~7 s, dependencies resolved from `nt\bin` too). Not yet seen on 2024+ installs.

### 6. Smaller follow-ups

Shipped in v0.8.0 (see CHANGELOG): connection failures from any tool go through
`DiagnoseConnectionFailure`, the server instructions spell out `expectedSha256`, and
`ShutdownGuard` ends the server with its parent process (Windows only; verified with the parent
killed while the pipes stay open). Still open:

- **Why the orphans lived is unconfirmed.** Closing stdin stops the server (0.2 s, both TFMs,
  mock and live backend), so they either never got EOF or hung in shutdown; the parent watch and
  the 10 s stop deadline cover both. If orphans still appear, capture one's parent chain
  (`Get-CimInstance Win32_Process`, `ParentProcessId`) before killing it — a parent that is still
  alive means the client kept its pipes open, which no server-side guard can see.

### 7. Found after the first write-up (same day)

Two bugs and a doc error from this list were fixed the same day and shipped in v0.8.0 (channel
alignment of 2024+ builds, a mismatched Open API folder sticking for the process, the "no
multi-type overload" sentence) — see CHANGELOG. Still open:

- **Performance — the AutoFetch snapshot.** With `ModelObjectEnumerator.AutoFetch = true`,
  constructing an enumerator runs one `ExportGetSnapshotFromDatabase` over the whole source
  (decompilation). If that holds, `GetSize()` is not a cheap count and every cursor page pays the
  full snapshot again — the AGENTS.md scan rules assume otherwise. Measure live (count and
  page timings on a 400k-object model) before changing them. Calls over ~60 s are a second
  plausible cause of the "MCP lost access" reports. **First data point (2026-09-30, Tekla 2023,
  model 3219, net48 server over MCP):** an UNFILTERED `tekla_count_objects` — documented as
  "instant" (`GetSize()`) — took ~20 s, while connection info and `tekla_list_grids` answered in
  about a second. Supports the snapshot hypothesis; still needs a proper timing breakdown. The
  Open API's own XML doc for `AutoFetch` points the same way: objects are fetched when the
  enumerator is created rather than on `Current`, and the setting applies to every enumerator in
  the process. The v0.8.0 release notes list the ~20 s count as a known limitation.
- **Measure `GetAllObjectsWithType(System.Type[])`** against the per-type chain (~5 s on 470k
  objects) before recommending it.
- **Recovery from a version mismatch without any GAC copy is only partial.** When neither the
  GAC nor the located folder holds this build's Open API, every bind fails. The resolver no longer
  makes that permanent, and the backend is not constructed until the version matches. But any
  class whose static initializer ran during the mismatch stays broken until the server restarts
  (CLR behaviour, verified in isolation). The common case — the right Tekla installed, so its
  assemblies are in the GAC — never gets there.

---

## Found in the v0.8.0 release review (2026-09-30)

### 8. `tekla_analyze_by_material` is an unpaged whole-model scan

`ModelAnalysisTools.AnalyzeByMaterial` calls `GetModelSummary()` with its defaults: every object
through `GetAllObjects()` (~50 s on a 400k-object model, see the `tekla_run_csharp` notes), a
`WEIGHT` read per part, plus class/profile counts it throws away — with no `maxObjects`, no cursor
and no `partsOnly`. On a large model it can outlive the MCP client's ~60 s timeout, and the reply
is then lost. `tekla_group_weight_by` with `groupBy=material` gives the same breakdown streamed,
restricted to physical parts and paged; the README already points there.

**Plan.** Either re-implement the tool on `ITeklaModelService.AggregateBy` (key = material) with
the usual `partsOnly` / `maxObjects` / `cursor` / `truncated` + `nextCursor`, keeping its output
shape, or deprecate it in favour of `tekla_group_weight_by` (description first, removal in a
later minor with a CHANGELOG note — tool removals break clients that hard-code schemas). Both
backends already implement `AggregateBy`, so either option is tool-layer only.

### 9. `tekla_export_drawings_pdf` writes outside the file allow-list

The three file-exchange tools send every path through `FilePathPolicy` (allow-listed roots from
`TEKLA_MCP_FILE_ROOT`, data extensions only, no UNC/device paths). The PDF export does not:
`TeklaDrawingService` checks only that `outputFile` is absolute, that the resolved names are
unique, and that an existing file is kept unless `overwrite=true`; then `PrintDrawing` writes
there. So with `apply=true` + `overwrite=true` an agent can replace any file the user's account
can write — a `.docx` included — or write to a network share. `{mark}`/`{name}` are safe
(`SafeFileToken` replaces path separators).

**Decision (2026-09-30):** keep it unrestricted — where drawings are printed is the user's call.
Mitigated by preview-by-default, `overwrite=false` by default, a warning in the tool description
(show the resolved `outputFiles`, `overwrite=true` only with explicit consent) and in both READMEs.

**If it ever needs tightening:** an opt-in variable (e.g. `TEKLA_MCP_PDF_ROOT`) that routes the
resolved paths through `FilePathPolicy` with `.pdf` allowed, and/or refusing `overwrite=true` for
an existing file whose extension is not `.pdf`. Both are tool/backend-local; no interface change.
