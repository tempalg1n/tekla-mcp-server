# Backlog — discussed and deferred

Work that was analysed and consciously postponed, with enough of the findings to pick it up
later without redoing the investigation. Each entry says what is verified and what is not.

---

## DEV-005 field report (Tekla 2021/2023) — discussed 2026-09-29

An agent developing a dependent (plugin) component — the KXMp Handrails project — hit repeated
server problems over several weeks and wrote them up (26 cases; the report and its evidence stay
with that project, not in this repo). Component UI bugs, the project's own external
inspector/writer tools, Windows screen automation (capture, UI Automation, focus) and disk access
were classified as not server issues and are out of scope here.

Already done in the same round:

- Script references on Tekla 2021, Open API folder detection, tool error text reaching the
  agent (branch `claude/script-refs-fix`; see CHANGELOG).
- Connection-failure diagnostics, server identity in `tekla_get_connection_info`,
  `expectedSha256` for `tekla_run_csharp`, script return-value truncation flag, catalog
  `Import*` members in the script policy (branch `claude/feedback-kxmp-fixes`).

The rest is below, most valuable first.

### 1. Reconnect after a Tekla restart without restarting the MCP client

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
  `GenericDelegateProxy<I, T>.Delegate`. Verified on live 2023 against a SIMULATED stale object
  (repeated three times); not yet against a real Tekla restart.
- **A poisoned proxy is not recoverable in-process:** if the first touch happens while the
  channel is not published, the static constructor fails and the CLR caches the
  `TypeInitializationException` for the process lifetime. Prevent it instead: check that the
  channel exists before the first touch and fail with a plain "not published" message without
  touching Tekla types.
- The field error ("cannot find the file" = the dialed pipe does not exist) points at the
  channel NAME changing between Tekla generations (see §2), not only at a stale object (the
  simulation gives "Requested service not found"). Check live which one happens.
- `DelegateProxy.Initialize()` exists only in some builds (not for Model/Drawing in NuGet
  2021.0.0 / 2023.0.1) — do not rely on it for 2021–2023.
- Public reset hooks that avoid reading the private `proxy` field (decompilation, untested):
  2021–2023 `CDelegateSetter.SetInstanceForUnitTesting(ICDelegate)` sets
  `DelegateProxy.Delegate`; 2024+ `CDelegateSetter.ResetInstance()` calls
  `DelegateProxy.Initialize()` (Model, Drawing and base assemblies).

**Plan.**

1. ~~Poisoning guard~~ — done 2026-09-30 for 2021–2023 (`TeklaRemotingChannel.EnsurePublished`,
   see `docs/tekla-api-notes.md`). On 2021 a poisoned Model client is a NULL delegate, not an
   exception (verified) — so step 3's recreate could also revive it, which widens what "poisoned"
   can mean here. The guard's "connected before, now not published" branch is where the
   reconnect hooks in; its message still says "restart the server".
2. Detect staleness with a cheap real call (`new Model().GetInfo()`); classify by exception
   TYPE — `RemotingException` in the chain = stale, `TypeInitializationException` = poisoned
   (only a server restart helps; say so). Messages are localized; never match on text.
3. Re-derive the channel suffix, patch the loaded `*Internal.Remoter.ChannelName` fields and
   recreate the base, Model and (if loaded) Drawing proxies as above.
4. Retry reads once at most. Never retry a write after a failure: report the outcome as unknown
   and read back (§4).

**Acceptance** (from the report): three normal restarts each of Tekla 2021 and 2023 with the MCP
server alive; the same model is reachable again without restarting the client; no write is
repeated blindly.

### 2. Explicit Tekla instance selection

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

2024–2026 use Trimble.Remoting (memory-mapped files and named kernel objects), not named pipes.
The channel is `{Assembly}-{ProductName}-{SESSIONNAME|Console}:{FileVersion}` and exists when
`EventWaitHandle.TryOpenExisting(channel + "$S")` succeeds. `Describe()` lists `\\.\pipe\` and
therefore sees nothing on these versions; `Align()` no longer touches 2024+ builds at all (fixed
2026-09-29, see CHANGELOG). `DelegateProxy.Initialize()` plus
`RemotingProxyHelper.DisposeService()` look like the recreate path; the behaviour of a stale
proxy after a restart is unknown — test on a live 2024+ before claiming support.

### 4. Write outcomes

Done 2026-09-30 (see CHANGELOG and AGENTS.md "Outcome + target"). Differences from the plan:
`rejected` is not an outcome — a mismatching `expectedModelPath` refuses the call as a tool error
before the backend runs; `failed_before_write` became `not_written` (it also covers "Tekla
refused every item"), and `partial` was added (committed, some items refused). Still open:

- Live verification of the `unknown` path (break the connection mid-batch on a scratch model)
  and of `Modify()` returning false for a refused part edit (TODO(windows) in `ModifyParts`).
- The Tekla PID is exact only when the channel names it or one instance of the version runs;
  with several instances on a plain channel it stays null (§2 makes it exact).
- Drawing writes are tracked coarsely (connection loss / failed commit → unknown).
- `tekla_run_csharp` gets the target and `expectedModelPath`, but no outcome: a script's own
  writes are not observable (`executed`/`executionAttempted` stay the signal).

### 5. Read tools for component development

The APIs exist in the Tekla 2021 assemblies (checked by metadata):

- ~~Advanced options~~ and ~~catalogs~~ — done 2026-09-30: `tekla_get_advanced_options`,
  `tekla_list_catalog` (profiles, parametric profiles, materials, components, UDA definitions).
  Compiled against all six NuGet versions; not yet run live (TODO(windows) in
  `TeklaEnvironmentService.cs`: whether UDA-definition items need `Select()` for details).
- ~~Plugin components with input objects and ordered points~~ — done 2026-09-30:
  `tekla_create_component` / `CreateComponents`, read back with children. Not yet live: run a
  real plugin (does Run happen inside `Insert()` or at `CommitChanges()`; attribute types).
- ~~Save and open without the UI dialog~~ — done 2026-09-30: `tekla_save_model`,
  `tekla_open_model` (refuses on unsaved changes unless discarded explicitly). Not yet live:
  whether Tekla dialogs (locked model, upgrade) block `Open()`.
- ~~Part solids~~ — done 2026-09-30: `tekla_get_part_solid` (compiled 2021–2026, not yet live;
  check loop winding and whether `OriginPartId` names cutting parts as documented).
- ~~API reference without manual generation~~ — done 2026-09-30: metadata-only generation from the
  installed Tekla, cached per build (verified in a net48 server against Tekla 2021: 1 740 types,
  ~7 s, dependencies resolved from `nt\bin` too). Not yet seen on 2024+ installs.

All of §5 is in `release/0.8.0`; what remains is the live run of each tool listed above.

### 6. Smaller follow-ups

Done 2026-09-30 (see CHANGELOG): connection failures from any tool go through
`DiagnoseConnectionFailure`, the server instructions spell out `expectedSha256`, and
`ShutdownGuard` ends the server with its parent process. Still open:

- **Why the orphans lived is unconfirmed.** Closing stdin stops the server (0.2 s, both TFMs,
  mock and live backend), so they either never got EOF or hung in shutdown; the parent watch and
  the 10 s stop deadline cover both. If orphans still appear, capture one's parent chain
  (`Get-CimInstance Win32_Process`, `ParentProcessId`) before killing it — a parent that is still
  alive means the client kept its pipes open, which no server-side guard can see.

### 7. Found after the first write-up (same day)

Two bugs and a doc error from this list were fixed the same day (channel alignment of 2024+
builds, a mismatched Open API folder sticking for the process, the "no multi-type overload"
sentence) — see CHANGELOG. Still open:

- **Performance — the AutoFetch snapshot.** With `ModelObjectEnumerator.AutoFetch = true`,
  constructing an enumerator runs one `ExportGetSnapshotFromDatabase` over the whole source
  (decompilation). If that holds, `GetSize()` is not a cheap count and every cursor page pays the
  full snapshot again — the AGENTS.md scan rules assume otherwise. Measure live (count and
  page timings on a 400k-object model) before changing them. Calls over ~60 s are a second
  plausible cause of the "MCP lost access" reports.
- **Measure `GetAllObjectsWithType(System.Type[])`** against the per-type chain (~5 s on 470k
  objects) before recommending it.
- **Recovery from a version mismatch without any GAC copy is only partial.** When neither the
  GAC nor the located folder holds this build's Open API, every bind fails. The resolver no longer
  makes that permanent, and the backend is not constructed until the version matches. But any
  class whose static initializer ran during the mismatch stays broken until the server restarts
  (CLR behaviour, verified in isolation). The common case — the right Tekla installed, so its
  assemblies are in the GAC — never gets there.
