> **Status update — fixes applied.** Everything marked ✅ below has been implemented and all three
> projects (`plugin`, `server`, `agent`) build clean (0 warnings, 0 errors) with the changes in place.
> **F04/F05 is now also done** (see its row) — `run_code` was migrated onto the same Roslyn compiler
> `AiconScriptCompiler` already uses for script Routines. **F13** (code signing) and **F17** (test
> project) are process/infrastructure items, not code changes, and are unchanged. **F01** and **F10**
> were informational only and needed no code change. See each row below for what actually happened.
>
> **2026-08-23 — the table right below this is now itself partially historical.** `plugin/AICon.csproj`
> multi-targets `net48;net8.0-windows` as of this date (Revit 2025 got installed on this machine); it is
> no longer net48-only. Left as originally written since F01 already exists to record exactly this kind
> of drift — see `HANDOFF-PROMPT.md`'s "Revit 2025 support" section for the current, maintained state.

# AICon — Technical Audit

Read-only audit. No source files were modified, created, or reformatted. All line numbers verified
against the working tree at audit time.

**Correction to the audit brief before anything else:** the brief describes this repo as ".NET 8,
Revit 2025". That is not what is on disk. Verified facts:

| Project | TargetFramework | Revit reference | Version |
|---|---|---|---|
| `plugin/AICon.csproj` | `net48` | `RevitAPI.dll`/`RevitAPIUI.dll` from `C:\Program Files\Autodesk\Revit 2024\` (plugin/AICon.csproj:40,44) | 3.0.0 (plugin/AICon.csproj:12) |
| `server/AIConServer.csproj` | `net10.0` | none (pure MCP stdio process) | 2.1.2 (server/AIConServer.csproj:10) |
| `agent/AIConAgent.csproj` | `net10.0` | none (console test host) | 0.1.0 (agent/AIConAgent.csproj:10) |

The rest of this audit is written against the real stack: **.NET Framework 4.8 + Revit 2023/2024 for
the add-in, .NET 10 for the two out-of-process hosts.** Everything below follows from that, not from
the brief's assumption.

---

## 1. Executive summary

1. Transport is genuinely well separated: three front doors (MCP stdio, localhost HTTP, in-process)
   converge on one dispatch function, `ToolDispatcher.Dispatch`. That part scales fine.
2. Orchestration and domain are **not** separated — all ~70 tool implementations are `static` methods
   inside the same dispatcher partial class/file set, with Revit API calls written directly inline.
   Works today at this size; will not survive much more growth without a real service layer.
3. Transaction handling is a genuine strength: one `InTransaction` helper, consistently applied, plus
   correct two-phase `TransactionGroup`/`Assimilate` patterns in both AR400 and Routines. No leaked or
   orphaned transactions were found.
4. One concrete, verified defect breaks that discipline: `create_wall_dimension` can call the AI
   decision layer **synchronously, from inside an already-open Revit transaction**, with an effective
   timeout of up to 300 seconds — freezing Revit's UI thread with a transaction open. This directly
   contradicts the project's own documented rule. See Finding F02.
5. The localhost bridge has no authentication, and `run_code` has no sandbox and no on/off switch
   (unlike the otherwise-riskier-sounding script Routines, which do). Combined, any local process can
   drive Revit and execute arbitrary C# with filesystem/process access, gated only by an LLM's prompted
   good behavior. See F03/F17.
6. Tool registration is manual, not reflection-based: adding tool #78 costs edits in 2 files (switch
   case + optional `Mutating` entry in `ToolDispatcher.cs`, schema in `ToolRegistry.cs`). Boilerplate,
   not dangerous — a missed step fails loudly (Revit itself rejects a write outside a transaction).
7. Input validation is consistently good where it exists — a small set of shared helpers
   (`RequireElement`, `RequireIds`, `PointFromArg`, …) used by nearly every tool. `run_code` sits
   entirely outside this discipline by design.
8. There is no preview/dry-run and no audit log for destructive tool calls (Ctrl+Z is the only
   rollback). There is a real, working decision-audit log, but it's for the AI *decision* layer only
   (`%APPDATA%\AICon\decisions.log`), not for tool calls in general.
9. Product-readiness gaps are real but boring: no tests anywhere in the repo, `README.md` is stale
   (says "57 tools" against an actual 76, never mentions AR400 or Routines at all), version number is
   hand-duplicated across 5 files, no code signing.
10. Nothing found here is an emergency for a single-firm internal tool used by one BIM lead. Several
    items (F02, F03, F17) matter a lot before this is handed to a second firm or a second user who
    isn't also the person who wrote the prompts.

---

## 2. Current architecture

### 2.1 Projects

```
plugin/AICon.csproj    net48   — Revit add-in (IExternalApplication), WPF-in-code UI, ~70 tool impls
shared/*.cs             (no own .csproj — compiled directly into all three of the below)
server/AIConServer.csproj  net10.0 — MCP stdio process, compiles ONLY shared/ToolRegistry.cs
agent/AIConAgent.csproj    net10.0 — console test host, compiles shared/*.cs
```

`shared/*.cs` (`Agent.cs`, `Config.cs`, `FileTexts.cs`, `GeminiProvider.cs`, `HttpToolExecutor.cs`,
`OpenAiCompatibleProvider.cs`, `Provider.cs`, `ProviderFactory.cs`, `ToolRegistry.cs`) is source-included
(not a referenced assembly) into `plugin`, `agent`, and — for `ToolRegistry.cs` only — `server`. This
is the "single source of truth" for the tool catalogue described in the project's own docs, and it
holds up: `grep` confirms 76 `Tool(...)` calls in `shared/ToolRegistry.cs:1-650` (one more match is the
`Tool(...)` helper method's own declaration, not a tool), one definition, three consumers.

### 2.2 Entry points

- `IExternalApplication`: `plugin/App.cs:10` (`AICon.App`) — starts the bridge server, registers the
  dockable chat pane, builds 4 ribbon panels (AI Connection, AI Agent, Shop Drawings, Routines).
- `IExternalCommand`s: `plugin/Commands.cs`, `plugin/Routines/RoutineCommands.cs` (dynamic per-slot
  classes `RoutineSlot0Command`…`RoutineSlot7Command` generated at ribbon-build time, `plugin/Routines/RoutineRibbonBuilder.cs:81`).
- MCP server host: `server/Program.cs` — top-level statements, JSON-RPC 2.0 over stdin/stdout.
- Tool registry: `shared/ToolRegistry.cs` — `Tools.BuildToolList()`.
- Tool dispatch: `plugin/ToolDispatcher.cs:23` (`Dispatch`) — the one place all three front doors converge.

### 2.3 Call path — MCP (Claude Desktop)

```
Claude Desktop
   │  JSON-RPC 2.0 over stdio ("tools/call")
   ▼
server/Program.cs (net10 process)              — no Revit reference at all
   │  HTTP POST {"tool":..,"args":..} → http://localhost:55234/
   ▼
plugin/BridgeServer.cs:101 Handle()             — runs on a background HttpListener thread
   │  enqueue BridgeJob, ExternalEvent.Raise(), block on ManualResetEventSlim (≤110s)
   ▼
plugin/BridgeServer.cs:22 RevitEventHandler.Execute(UIApplication app)   ◄── REVIT API THREAD BOUNDARY
   │  runs only when Revit is idle (Revit's own ExternalEvent contract)
   ▼
plugin/ToolDispatcher.cs:23 Dispatch()
   │  if Mutating: open Transaction ─┐
   │  else: run directly             │
   ▼                                 │
ExecuteCore() switch(tool) → one of ~70 private static methods in
   ToolDispatcher.cs / ToolsExtended.cs / ShopDrawings.cs / WallDimensioning.cs
   │                                 │
   └── Transaction.Commit() ─────────┘
   ▼
result object → Json.Serialize → BridgeJob.ResultJson → HTTP response → MCP → Claude
```

### 2.4 Call path — in-Revit chat panel (Gemini/DeepSeek/local)

```
plugin/ChatPanelControl.cs (WPF, in-process)
   │  agent loop (shared/Agent.cs) picks a tool call
   ▼
plugin/InProcessToolExecutor.cs:54 ExecuteBlocking()
   │  read-only / delete-confirm gates checked HERE (panel-only — see F06)
   │  enqueue BridgeJob onto the SAME queue as the HTTP bridge
   ▼
plugin/App.cs:21 Handler (RevitEventHandler) — same ExternalEvent, same thread boundary
   ▼
ToolDispatcher.Dispatch()  (identical from here on)
```

Both paths cross the Revit API thread boundary in exactly one place — `RevitEventHandler.Execute`,
driven by one `ExternalEvent` (`plugin/App.cs:21-22`). That part is done correctly and consistently:
there is no code anywhere in the plugin that calls into `Autodesk.Revit.DB`/`UI` from a raw
`Task.Run`/`Thread`/`async` continuation without going through this queue. Confirmed by grep for
`Dispatcher.Invoke`/`.Result`/`async void` across the plugin — the two `Dispatcher.Invoke` call sites
(`ChatPanelControl.cs:356,618`) are WPF UI-thread marshalling, not Revit API calls, and are correct
uses (`Dispatcher.CheckAccess()` guard at :617, task continuation via
`TaskScheduler.FromCurrentSynchronizationContext()` at :343).

### 2.5 Routines and AR400 execution paths

Both reuse `ToolDispatcher.Dispatch` (Routines, `plugin/Routines/RoutineExecutor.cs:77`) or
duplicate its Revit-domain code directly (AR400, `plugin/ShopDrawings.cs`, which calls
`ToolDispatcher.CreateSheet`/`PlaceViewOnSheet` directly as internal statics — `ShopDrawings.cs:293,296`).
Script Routines run through a **separate** compiler (`plugin/Routines/AiconScriptCompiler.cs`, Roslyn)
and a **separate** transaction wrapper (`plugin/Routines/RoutineScriptHost.cs:142`), independent of
`ToolDispatcher.InTransaction`.

---

## 3. Findings

| ID | File:Line | Severity | Issue | Impact | Proposed fix |
|---|---|---|---|---|---|
| F01 | `plugin/AICon.csproj:4,40`; `server/AIConServer.csproj:5` | P2 | Repo is `net48`/Revit 2024 + `net10.0`, not ".NET 8/Revit 2025" as briefed | Any plan made against the wrong stack (e.g. assuming nullable refs or C# 12 in the plugin) will not compile | None needed in code — just don't plan future work against the wrong TFM |
| F02 ✅ | `plugin/ToolDispatcher.cs:30-33,44`; `plugin/WallDimensioning.cs:58-59,124-146`; `plugin/Services/AIConDecisionClient.cs:318-320`; `shared/OpenAiCompatibleProvider.cs:35`; `shared/GeminiProvider.cs:45` | **P0** | `create_wall_dimension` is in the `Mutating` set, so `Dispatch` opens a `Transaction` (ToolDispatcher.cs:32) before calling `CreateWallDimension` → `DimensionWallPair(..., allowAiEscalation: true, ...)`, which can call `AIConDecisionClient.DecideDimensionFaces` **synchronously** (`.GetAwaiter().GetResult()` at AIConDecisionClient.cs:320) from inside that open transaction. When no dedicated `ar400ai.json` key is configured (the documented common case), the call goes through `ViaChatPanelProvider`, whose underlying `HttpClient.Timeout` is **300 seconds** (OpenAiCompatibleProvider.cs:35 / GeminiProvider.cs:45) — not the 8s `CallTimeout` constant (AIConDecisionClient.cs:24), which only bounds the *other*, less-common direct-key path | Revit's UI thread is blocked and a transaction sits open for up to 5 minutes waiting on a network call (or a hung local Ollama) whenever `create_wall_dimension` hits >2 candidate faces or non-parallel walls. Directly contradicts the project's own documented invariant ("never inside an open transaction"). Also reachable from any composed Routine step that calls `create_wall_dimension` (`RoutineExecutor.cs:77`) | **Fixed:** `AIConDecisionClient.cs`'s `ViaChatPanelProvider` now passes a `CancellationTokenSource(CallTimeout)`-bound token into `provider.CompleteAsync(...)` instead of `CancellationToken.None` — confirmed the token reaches the real `HttpClient.SendAsync` call (`OpenAiCompatibleProvider.cs:63`), so it actually aborts the request at 8s regardless of the provider's own 300s `HttpClient.Timeout`. A timeout now surfaces as `TaskCanceledException`, already handled cleanly by the existing `FriendlyFailure` catch. Left the call itself inside the transaction (matching the original author's explicit "worth its latency" judgment call) since the real, measured harm — an unbounded 5-minute freeze — is what's fixed; splitting the transaction further would have required assumptions about `Reference` validity across a deleted scratch view that couldn't be verified without a live Revit session |
| F03 ✅ (auth) / ⬜ (confirm gate) | `plugin/BridgeServer.cs:75-76,101`; `plugin/ToolsExtended.cs:1655-1719` | **P0** | The localhost bridge (`BridgeServer.cs:76`, `http://localhost:55234/`) accepts any POST with no auth token/origin check and dispatches it straight to `ToolDispatcher`. `run_code` (ToolsExtended.cs:1655) compiles and runs arbitrary C# referencing `System.dll`/`System.Core.dll`/`System.Xml.dll` plus the full Revit API, with no namespace restriction — fully-qualified calls to `System.IO.File`, `System.Diagnostics.Process`, etc. compile and run | Any local process (not just Claude Desktop) can drive Revit and execute arbitrary code — file I/O, process spawn — with zero Revit-level confirmation. The only stated protection is the MCP `initialize` instructions text asking the model to "confirm before deleting/destructive code" (server/Program.cs:132-133), which is advisory, not enforced | **Fixed (token):** new `shared/BridgeToken.cs`; `BridgeServer` now generates a 256-bit random token on first run (`RandomNumberGenerator`), persists it to `%APPDATA%\AICon\bridge.token`, and rejects any POST whose `X-AICon-Token` header doesn't match (GET status-ping stays open — no model access). `server/Program.cs` and `shared/HttpToolExecutor.cs` (agent host) now read the token fresh on every call and send it. `InProcessToolExecutor.cs` (in-Revit panel) needed no change — it never goes over HTTP. **Not done:** the code-level confirm gate for `delete_elements`/`run_code` on this path — see F06, which adds logging but not a blocking confirmation; that's a product decision (what UX for a stdio-based MCP client), not a pure code fix, left for the owner to decide |
| F04 ✅ | `plugin/ToolsExtended.cs:1680-1697` vs `plugin/Routines/AiconScriptCompiler.cs:94-153` | P1 | Two independent "compile arbitrary C# against the live Revit API" implementations exist in the same codebase: `run_code` uses the legacy `System.CodeDom.Compiler`/`CSharpCodeProvider` ("C# 5" — no string interpolation, no `?.`, no `out var`, per the error text at ToolsExtended.cs:1692), recompiling from scratch on every call; Routines' script kind uses Roslyn 4.9.2 with modern C#, cached per routine id, and correct `#line`-mapped error locations | The AI's most-used escape hatch (`run_code`) is handicapped to a decade-old C# dialect while the less-used, curated feature (script Routines) got the better compiler. Confusing for whoever maintains both, and doubles the surface for compiler-related bugs | **Fixed:** `AiconScriptCompiler.cs` gained `CompileRunCodeBody(body)` — same `Compile()`/`GetReferences()` core as routines (exact loaded RevitAPI.dll, no version drift), its own wrapper matching `run_code`'s existing contract (`Run(UIApplication, UIDocument, Document)`, no `IAiconRoutine`), and the same `#line`-mapped error locations. `Compile()` gained an `alreadyWrapped` parameter so it doesn't re-wrap an already-fully-formed source (caught this exact bug in a throwaway harness before it shipped — see F05's row). `ToolsExtended.cs`'s `RunCode` now calls it instead of `CSharpCodeProvider`; `System.CodeDom.Compiler`/`Microsoft.CSharp` usings removed. Updated every user-facing "C# 5" mention (`ToolRegistry.cs`, `server/Program.cs`, `shared/Agent.cs`) to describe Roslyn/modern C# instead |
| F05 ✅ | `plugin/ToolsExtended.cs:1695` | P2 | Compiler-error line remap uses a bare magic number: `err.Line - 13`, tied to an exact count of boilerplate lines built at ToolsExtended.cs:1661-1678 | If the boilerplate template ever gains/loses a line, reported error line numbers silently go wrong — the AI gets told the wrong line to fix | Fold into F04's fix (Roslyn + `#line` directives already solves this correctly, as proven by `AiconScriptCompiler.cs:176-204`) | **Fixed as part of F04:** the magic-number remap is gone entirely — `WrapRunCodeBody`'s `#line 1 "run_code.cs"` directive makes Roslyn report the author's real line number directly, verified in a throwaway harness (a deliberately broken 3-line body correctly reported its error at line 2, not some offset into the wrapper) |
| F06 ✅ (logging only) | `plugin/InProcessToolExecutor.cs:75-82` vs `plugin/BridgeServer.cs:22-48` | P1 | `delete_elements` gets an actual UI Yes/No confirmation dialog only on the in-Revit chat-panel path (`InProcessToolExecutor.cs:79`). The MCP/Claude-Desktop path (`BridgeServer.cs` → `RevitEventHandler.Execute` → `ToolDispatcher.Dispatch`) has no equivalent — deletion runs immediately | A user driving AICon from Claude Desktop gets no in-app confirmation before element deletion; only Ctrl+Z after the fact. Inconsistent safety guarantee depending on which of the three front doors is used | **Fixed (partial):** `ToolDispatcher.DeleteElements` now unconditionally logs to `bridge.log` (element count + up to 20 ids) before deleting, regardless of which front door called it — so there is at least a record. **Not done:** a real blocking confirmation on the MCP path — the stdio JSON-RPC protocol has no clean built-in way to pause mid-call for a human yes/no the way the in-process panel's `MessageBox.Show` does; that needs a product decision on UX, not a mechanical fix |
| F07 ✅ | `plugin/ShopDrawings.cs:274,279` | P2 | `catch { }` swallows failures to apply the configured view template (`:274`) and to set the scope box parameter (`:279`) with **no logging and no surfaced warning** — contrast the well-handled sibling pattern at `ShopDrawings.cs:346-358` (tagging failures ARE logged via `App.Log` and added to `tagWarnings`) | A misconfigured/incompatible view template silently fails to apply on some or all AR400 sheets; `templated` just doesn't increment and nothing in the final summary tells the user why. On a 41-level tower this could mean dozens of sheets missing their template with zero indication | **Fixed:** both `catch { }` blocks now match the existing sibling pattern exactly — `App.Log(...)` plus a `tagWarnings.Add(viewName + ": ...")` entry naming the view and the real exception message, so a failed template/scope-box application now shows up in AR400's own end-of-run summary |
| F08 | `plugin/ToolsExtended.cs:1659,1711-1713` | P2 | `run_code`'s `no_transaction: true` argument runs AI-authored code fully outside any Revit transaction, with no logging of when/why it was used | Revit's own API will reject most writes attempted this way (fails loud, not silently corrupting), but there is no audit trail of how often/why the option is invoked, and the failure the AI sees is a raw Revit exception rather than the codebase's usual teaching-error style | Log every `no_transaction: true` call (tool args truncated, timestamp) to `bridge.log`; consider requiring a `reason` string alongside the flag |
| F09 ✅ | `plugin/Json.cs:15-18` | P2 | `JavaScriptSerializer.MaxJsonLength = int.MaxValue` removes the serializer's one built-in payload-size guard | Low risk alone (bridge is localhost-only, `BridgeServer.cs:76`), but combined with F03 (no auth), an arbitrarily large local POST is fully deserialized in memory with no cap | **Fixed:** capped at 64 MB (`Json.cs`'s new `MaxJsonLength` constant) instead of `int.MaxValue` — now also defense-in-depth alongside F03's token check |
| F10 | `plugin/Json.cs` vs `plugin/Routines/RoutineModel.cs:1-3` / `shared/*.cs` | P2 (informational) | Two JSON stacks coexist in the same `AICon.dll`: `System.Web.Script.Serialization.JavaScriptSerializer` (tool wire format, `plugin/Json.cs`) and `System.Text.Json` (Routines, config, shared agent code) | Not a bug — each is self-contained in its own area — but it's duplicated capability and two mental models for a contributor to learn | No urgent action. If the tool wire format is ever revisited, standardize on `System.Text.Json` (already referenced, `AICon.csproj:23`) and retire `Json.cs` |
| F11 ✅ | `plugin/AICon.csproj:12`; `plugin/App.cs:13`; `server/AIConServer.csproj:10`; `server/Program.cs:120`; `scripts/build-package.ps1:5` | P2 | Product version is hand-typed in 5 independent places with no compiler-enforced link | A missed bump ships a DLL whose `About`/log output or MCP `serverInfo.version` disagrees with the actual assembly — already an acknowledged manual ritual per the project's own maintenance notes | **Fixed:** `App.cs`'s `Version` and `Program.cs`'s `serverInfo.version` now both read `AssemblyInformationalVersionAttribute` off their own assembly (stamped by each `.csproj`'s `<Version>` via `GenerateAssemblyInfo`) instead of a hand-typed literal. `scripts/build-package.ps1` now parses `<Version>` straight out of `plugin/AICon.csproj` instead of its own hardcoded `$version`. Down from 5 independent literals to 2 (each project's own `.csproj`, which is the correct single source per project) |
| F12 ✅ | `README.md:4,36,57,66` | P2 | README says "57 tools" in four places; `shared/ToolRegistry.cs` currently defines **76** (`Tool(...)` call count — 77 `grep` hits include the `Tool(...)` helper method's own declaration). The "Developing" section's example output still names `dist\AICon-2.0.0.zip` (`README.md:66`) while `scripts/build-package.ps1:5` builds `3.0.0`. More significantly: README never mentions **AR400** or **Routines** anywhere — two of the product's three described features are undocumented for an end user | Anyone reading the shipped README gets a materially incomplete and numerically wrong picture of the product | Regenerate the tool table/count from `ToolRegistry.cs` rather than hard-coding it; add AR400 and Routines sections | **Fixed:** README's intro, tool table, and comparison table now say 76 (matching `ToolRegistry.cs`'s own corrected header comment); the "Developing" example no longer hardcodes a stale zip version; added full **AR400** and **Routines** sections; troubleshooting now mentions the new bridge token from F03 |
| F13 | `scripts/install.ps1:19-24` | P2 (informational) | No code signing — the installer works around Windows' Mark-of-the-Web via `Unblock-File` rather than an Authenticode signature | Fine for a single-firm internal tool; will hit friction (SmartScreen, AppLocker/WDAC) the moment this leaves one firm's machines | Out of scope for now; flag before wider distribution |
| F14 ✅ | `plugin/Json.cs:80-88` | P2 | `Json.ToInt`/`Json.ToDouble` throw raw `FormatException`/`InvalidCastException` on malformed input, unlike the rest of the validation layer which throws consistent `InvalidOperationException("'x' must be…")` messages | Never corrupts the model (every mutating call is inside `InTransaction`'s catch-all rollback, `ToolDispatcher.cs:227-244`) — but the AI/user sees a confusing raw .NET message instead of a teaching one | Wrap the `Convert.To*` calls and throw a consistent `InvalidOperationException` naming the offending argument key | **Fixed:** `Json.ToDouble` now catches `FormatException`/`InvalidCastException`/`OverflowException` and rethrows as `InvalidOperationException("Expected a number, got '...'")`; `ToInt` now calls `ToDouble` so it inherits the same clean error for free. Doesn't thread the specific argument *key* through (many call sites only have a raw list item, no key name available) — the value itself is included instead, which is still a real improvement over a bare `FormatException` |
| F15 ✅ | `plugin/ToolDispatcher.cs:340-367` | P2 (perf, minor) | `ListElements` calls `.ToElements()` (materializes the full matching `ICollection<Element>`) then `.Take(limit)` — the project's own documented rule is "never `.ToElements().Take(n)`". Here it's partly justified since `total_in_model` (line 363) needs a full count regardless | On a category with thousands of elements (this model has ~1,579 walls), builds one wrapper `Element` object per match before truncating, for a call that only ever returns `limit` (default 100) of them | Use `collector.GetElementCount()` for the total (no `Element` wrappers) and a fresh `FilteredElementCollector(...).Take(limit)` for the sample | **Fixed:** exactly as proposed — `GetElementCount()` for `total_in_model`, a fresh capped collector for the returned sample |
| F16 ✅ | `plugin/ToolDispatcher.cs:153` vs `plugin/Routines/RoutineScriptHost.cs:102-107` / `AiconRoutineSettings.cs:19` | P1 | Script Routines are gated behind an explicit, off-by-default opt-in (`AiconRoutineSettings.AllowCodeExecution`, checked at `RoutineScriptHost.cs:102`). `run_code` — reachable by any connected AI on every front door, with no save/review step first — has **no equivalent switch anywhere** in the codebase (confirmed: `AllowCodeExecution` only appears in the three `Routines/*.cs` files) | The higher-risk, less-reviewed capability (arbitrary code an AI decided to run *this turn*) is always on; the lower-risk, reviewed-before-becoming-a-button capability is gated. Inverted relative to actual risk | Add the same settings-file gate to `run_code`'s dispatch (`ToolDispatcher.cs:153`), reusing or extending `AiconRoutineSettings` | **Fixed:** new `AiconRoutineSettings.AllowRunCode` (separate flag, same file/class) — **defaults to `true`**, deliberately unlike `AllowCodeExecution`'s safe-by-default off, because `run_code` has always been unconditionally on; defaulting the new switch to off would have silently broken the owner's daily workflow on next rebuild. This only adds the *ability* to turn it off later (e.g. before handing AICon to a second, less-trusted user) — today's behavior is unchanged until someone edits `routines.json` |
| F17 | *(no single line — process gap)* | P2 | No test project exists anywhere in the repo (confirmed: no `*test*` path outside `bin`/`obj`) | Every "verified working" claim in the project's own status notes is verified by hand against the live Revit model, not by anything that runs in CI or before a commit. Regressions in the ~70-tool dispatch table are only caught by manually exercising them again | Not a quick fix given the Revit-API dependency, but `RoutineModel.Validate()`/`RoutineExecutor`'s placeholder resolution (`ResolveValue`/`Lookup`, `RoutineExecutor.cs:169-254`) and `AiconScriptCompiler.WrapBody`/`IsFullRoutine` are pure C# with no live Revit document needed — those are unit-testable today with zero new infrastructure |

**Not flagged, deliberately** — checked and found sound, listed here so the next reviewer doesn't
re-derive them: the single `InTransaction` helper and its consistent use (`ToolDispatcher.cs:227-244`);
AR400's two-phase `TransactionGroup` (`ShopDrawings.cs:237-398`) and Routines' matching pattern
(`RoutineExecutor.cs:63-109`, `RoutineScriptHost.cs:142-177`) — both correctly `Assimilate()` on
success and `RollBack()` on failure, verified line-by-line; the shared input-validation helpers
(`RequireElement`, `RequireIds`, `PointFromArg`, `VectorFromArg`, `ResolveLevel`, `ResolveTypeId` —
`ToolDispatcher.cs:828-915`); the one Revit-API-thread crossing point (`RevitEventHandler.Execute`,
`BridgeServer.cs:22`) and its consistent use from both the HTTP and in-process paths; zero
`TODO`/`FIXME`/`HACK` comments anywhere in the codebase; no orphaned `Transaction`/`TransactionGroup`
found (all are inside `using` blocks with explicit commit/rollback on every path checked).

---

## 4. Risk register — top 5 most likely to break in production

*(Items 1, 3 and 4 below are now fixed — see the ✅ outcomes in section 3. Left as originally written
so this stays an accurate record of what was found; the fix summary is in each row, not rewritten here.)*

1. **F02 — AI decision call inside an open transaction, up to 300s.** ✅ Fixed. The single most
   concrete, reproducible defect found. Trigger: run `create_wall_dimension` on two walls that aren't
   parallel, or where a third wall/return sits in the ray path, with the chat panel pointed at a local
   model (Ollama not running, or slow) and no dedicated `ar400ai.json` key configured. Revit froze with
   a transaction open until the HTTP call timed out or returned — now bounded to 8s.
2. **F03 — Unauthenticated localhost bridge + unsandboxed `run_code`.** ✅ Bridge auth fixed. `run_code`
   itself is still unsandboxed by design (F04 upgraded its *compiler*, not its trust model — a real
   Revit-API sandbox was never in scope, same reasoning `AiconRoutineSettings.cs`'s own doc comment
   gives for script Routines: "a routine that can touch the model can already delete it"). Requires
   local code execution
   already (another process on the same machine), so it is not remotely exploitable — but on a
   workstation that also runs other software, a compromised or malicious local process has a direct,
   silent path to arbitrary file/process access via `run_code`, with no Revit-level prompt at all.
3. **F16 — `run_code` has no kill switch.** ✅ Fixed. If AICon is ever handed to a second user whose
   prompting discipline the owner doesn't fully trust, there was no configuration knob to turn off code
   execution for that user short of removing the tool from `ToolRegistry.cs` and rebuilding — now
   `AiconRoutineSettings.AllowRunCode` (defaults on, unchanged behavior until someone flips it).
4. **F06 — Inconsistent delete confirmation.** ✅ Logging added; the confirmation gap itself remains (see
   F06's row in section 3 — needs a UX decision, not a mechanical fix). A user who is used to the
   in-Revit panel's Yes/No gate and then switches to driving AICon from Claude Desktop for a session
   still will not get the same protection, but every deletion is now at least logged either way.
5. **F07 — Silent AR400 template/scope-box failures.** ✅ Fixed. On a large package run (this project
   routinely creates dozens of sheets per AR400 pass), a template that failed to apply produced no warning
   anywhere — the sheet just looks wrong, discoverable only by visual review.

---

## 5. Recommended target architecture, and the delta from today

**Today:** transport (3 front doors) → one dispatcher function → ~70 domain methods living directly
in the dispatcher's files, using a shared but informal validation-helper convention. This works and is
not over-engineered for a ~70-tool, single-maintainer add-in.

**Target, in priority order (not a rewrite — additive layering on top of what exists):**

1. **A trust-tier concept for tools.** Today every tool is either "read" or "Mutating" (one boolean).
   Introduce a third tier — "unsandboxed" (`run_code`) — with its own settings gate (F16), its own
   logging (F08), and ideally its own confirmation contract (F06), rather than treating it as one more
   entry in the `Mutating` hash set.
2. **Pull the AI-decision escalation out of the transaction boundary as a hard rule**, not just fixed
   at the one call site found (F02). Add a debug assertion or lightweight check in
   `AIConDecisionClient`'s call path that throws if invoked while `doc.IsModifiable`/a transaction is
   open, so a future call site can't reintroduce the same bug silently.
3. **A minimal bridge auth token** (F03) — this is a small, additive change (one generated file, one
   header check) that closes the biggest gap without touching the dispatch architecture at all.
4. **Consolidate the two C# compilers** (F04) onto Roslyn — removes a whole category of future bugs
   (line-number remap, C# version confusion) for one deletion + one redirect.
5. **A single version source** (F11) and a **regenerated tool table in README** (F12) — cheap,
   mechanical, and removes an entire class of "the docs lied" support questions.

None of this requires splitting `ToolDispatcher`/`ToolsExtended`/`ShopDrawings` into a formal
transport/orchestration/domain project split. At ~70 tools and one primary maintainer, that split
would add indirection without buying much; revisit only if the tool count roughly doubles again or a
second full-time contributor joins.

---

## 6. Ordered refactor plan (P0 first, each item = one commit)

1. **P0 — Fix F02.** Move `AIConDecisionClient.DecideDimensionFaces` out of the open transaction in
   `WallDimensioning.cs`: split `CreateWallDimension`/`DimensionWallPair` so the `ReferenceIntersector`
   ray-cast + AI escalation happen before `InTransaction` is entered, and only the final
   `doc.Create.NewDimension` call runs inside it. Bound `ViaChatPanelProvider`'s call with a real
   timeout instead of inheriting the provider's 300s default.
2. **P0 — Fix F03.** Add a shared-secret token to `BridgeServer`: generate on `OnStartup`, write to
   `%APPDATA%\AICon\bridge.token` (user-only ACL), require it as a header in `BridgeServer.Handle`
   before enqueueing any job; update `server/Program.cs` and `HttpToolExecutor.cs` to read and send it.
3. **P1 — Fix F16.** Add an `allowRunCode` (or reuse `AiconRoutineSettings.AllowCodeExecution`) gate
   checked at the top of `RunCode` in `ToolsExtended.cs`, defaulting to whatever the current de facto
   behavior is today (on), so this ships as a no-op until the owner chooses to turn it off.
4. **P1 — Fix F06.** Add a destructive-action log line (tool, element count/ids, timestamp, which front
   door) written unconditionally from `ToolDispatcher.DeleteElements`, independent of which executor
   called it. Decide separately (owner's call, not a code decision) whether the MCP path also needs an
   interactive confirm round-trip.
5. **P2 — Fix F07.** Add `App.Log` + `tagWarnings` entries to the two silent `catch { }` blocks in
   `ShopDrawings.cs:274,279`, matching the existing pattern at `:346-358`.
6. **P2 — Fix F11.** Replace the literal `Version` strings in `App.cs`/`Program.cs` with
   `Assembly.GetExecutingAssembly().GetName().Version`.
7. **P2 — Fix F12.** Regenerate the tool count/table in `README.md` from `ToolRegistry.cs`; add AR400
   and Routines sections.
8. **P2 — Fix F04/F05.** ✅ Done — migrated `run_code` onto `AiconScriptCompiler`; deleted the
   `CSharpCodeProvider` path and the `err.Line - 13` remap entirely.
9. **P2 — Fix F09/F14/F15.** Three small, independent, low-risk cleanups; safe to batch into one commit
   if preferred, or ship separately — no ordering dependency between them or on anything above.

---

*End of audit. No files other than this one were created or modified.*
