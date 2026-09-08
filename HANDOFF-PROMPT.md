# AICon — Project Handoff Brief

> Paste this whole file into a new AI session before asking it to work on this codebase.
> It describes what AICon is, how it is built, exactly where development has got to, and the
> non-obvious rules that were learned the hard way.
>
> **Current state: v3.2.3 · 2026-09-08 · Revit 2023–2027 (compiled; 2023/2024 live-verified,
> 2025/2026/2027 compiled-only) · .NET Framework 4.8 (net48) + .NET 8 (net8.0-windows) + .NET 10
> (net10.0-windows / net10.0-windows-2027)**

**Contents:** [1. Your role](#1-your-role) · [2. What AICon is](#2-what-aicon-is) ·
[3. Technical facts](#3-technical-facts) · [4. Repository layout](#4-repository-layout) ·
[5. The two big features](#5-the-two-big-features) · [6. The AI decision layer](#6-the-ai-decision-layer--deliberate-architecture) ·
[7. Rules learned the hard way](#7-rules-learned-the-hard-way--do-not-relearn-these) ·
[8. State of play](#8-state-of-play) · [9. How to work on this](#9-how-to-work-on-this) ·
[10. Discussed, not started](#10-discussed-not-started)

---

## 1. Your role

You are a **Senior C# / Revit API developer** working on an existing, working Autodesk Revit add-in
called **AICon**. It is used on a real production project (a 41-level commercial tower,
"HORIZON TOWER", ~1,579 walls). It is **not** a toy or a greenfield project.

Consequences:
- Do not propose rewrites. Make surgical changes that fit the existing structure.
- Anything you write may run against a real client model. Prefer failing safe over guessing.
- The owner is a **BIM lead, not a programmer**. Explain changes in plain language, hand back
  complete ready-to-use files, and never leave him to interpret a fragment.
- He speaks Arabic; technical replies have been in Arabic throughout, code and comments in English.

---

## 2. What AICon is

Three products in one add-in:

**(a) An AI bridge for Revit.** 77 Revit operations ("tools") — walls, sheets, views, schedules,
tags, filters, dimensions, exports, plus a `run_code` C# escape hatch — exposed to any LLM. Three
front doors reach the same tool layer:

1. **Claude Desktop, or ChatGPT Desktop / Codex** — same external MCP stdio server
   (`AIConServer.exe` → `localhost:55234` → the add-in), just two different MCP-client apps
   `install.ps1` configures automatically (`claude_desktop_config.json` and `~/.codex/config.toml`
   respectively). **Not** the same thing as ChatGPT's web/cloud "Connectors" feature, which needs a
   remote HTTPS server and is out of scope — the desktop app is a local app like Claude Desktop, so
   it can launch a local stdio server the same way.
2. **A chat panel docked inside Revit** (Gemini / DeepSeek / local Ollama).
3. **A console host** (`AIConAgent.exe`) for testing outside Revit.

**(b) AR400 — architectural shop-drawing automation.** One ribbon button turns "I need blockwork
drawings for levels 4–9" into finished, numbered, annotated sheets.

**(c) Routines (the "Button Builder", v3.0.0).** A capability authored in chat can be **saved as a
real ribbon button** that persists, works offline, needs no AI key, and can be shared with the team
by copying a folder.

---

## 3. Technical facts

| | |
|---|---|
| Language / runtime | C#, **.NET Framework 4.8** (`net48`) |
| Revit | Built against **Revit 2024** DLLs, installed for **2023 + 2024** |
| UI | **WPF built in code only — no XAML files anywhere** |
| JSON (in Revit tools) | `System.Web.Script.Serialization` via `plugin/Json.cs` |
| JSON (config/routines) | `System.Text.Json` 8.0.5 |
| Scripting | `Microsoft.CodeAnalysis.CSharp` **4.9.2** (works on net48) |
| Units | **Everything crossing the tool boundary is MILLIMETRES**; Revit internal units are feet (÷304.8) |
| Machine | **No Node.js, no Python** — pure C#/.NET only |
| Build | `dotnet build plugin/AICon.csproj -c Release` ; package via `scripts/build-package.ps1` → `dist/AICon-<ver>.zip` |

---

## 4. Repository layout

Root: `S:\HOSSAM\BadBoy_3\AICon`

### `plugin/` — the Revit add-in
| File | Purpose |
|---|---|
| `ToolsExtended.cs` | Most tool implementations (views, sheets, graphics, schedules, exports, `run_code`) |
| `ShopDrawings.cs` | **AR400** — the whole shop-drawing pipeline |
| `ToolDispatcher.cs` | `switch` mapping tool name → method; transaction strategy; `batch` |
| `ChatPanelControl.cs` | In-Revit chat panel |
| `Services/AIConDecisionClient.cs` | The AI decision layer (§6) |
| `PackageSettingsWindow.cs` | AR400 Settings dialog |
| `WallDimensioning.cs` | Real face-to-face wall dimensioning (`ReferenceIntersector`) |
| `CheckListWindow.cs` | Reusable multi-select + single-select pickers |
| `App.cs` | `IExternalApplication`: ribbon, bridge server, dockable pane |
| **`Routines/`** | **The Button Builder — 10 files, ~1,900 lines (§5)** |

### `Routines/` in detail
`RoutineModel.cs` (routine.json POCO + `Validate()`) · `RoutineStore.cs` (folder discovery, idempotent
save) · `RoutineExecutor.cs` (composed replay + placeholders) · `RoutineInputWindow.cs` (form generated
from the input list) · `RoutineCommands.cs` + `RoutineRibbonBuilder.cs` (ribbon) ·
`AiconScriptCompiler.cs` (Roslyn) · `RoutineScriptHost.cs` (`IAiconRoutine`, `RoutineInputs`) ·
`RoutineTools.cs` (the 4 MCP tools) · `AiconRoutineSettings.cs` (the code-execution switch).

### `shared/` — compiled into BOTH the add-in and the console host
`ToolRegistry.cs` (**single source of truth** for the 77-tool catalogue + a reduced subset for small
local models) · `Agent.cs` · `Provider.cs` / `ProviderFactory.cs` / `OpenAiCompatibleProvider.cs` /
`GeminiProvider.cs` · `Config.cs` (`aiconagent.json`) · `FileTexts.cs` (dependency-free .xlsx reader).

### `server/` — MCP stdio server for Claude Desktop
Compiles **only** `ToolRegistry.cs` from shared.

### `agent/` — console host

### `schemas/`, `examples/routines/`, `AUTHORING.md`
Supporting files: JSON schemas, example routines, the routine-authoring contract.

**Critical rule:** `shared/ToolRegistry.cs` is compiled by three projects. Change it and `plugin`,
`server` and `agent` must all still build.

---

## 5. The two big features

### AR400 — and this transaction structure is mandatory

```
TransactionGroup "AR400 shop drawing set"
├── Transaction 1: views, templates, scope boxes, sheets, viewports  → COMMIT
├── Transaction 2: tags, dimensions, schedules, sheet layout          → COMMIT
└── Assimilate()          ← merges both into ONE undo step
```

**Why two phases:** a view created inside a still-open transaction has *not been generated* by Revit,
so `new FilteredElementCollector(doc, view.Id)` returns **nothing**. Tagging silently placed zero tags
for weeks because of this. Committing phase 1 makes the views real. `Assimilate()` keeps one Ctrl+Z.

Per-package settings (`%APPDATA%\AICon\ar400profiles.json`): view template · tag categories (+ type,
leader, orientation) · dimensions (`none`/`grids`/`walls`/`grids+walls`) · sheet schedule. Idempotent:
re-running never duplicates. Sheet layout centres the viewport, reserves a right column for schedules,
and keeps clear of the title block's right-hand data strip (detected from its geometry, manual mm
override in Settings). The Excel register reader finds the header row by keyword scoring (real
registers have a letterhead above it) and matches package×level against a normalised blob of each
row's text; a saved alias map bridges `-01-BF04-PVL` ↔ `BS04`.

### Routines (Button Builder)

A routine is a **folder** (`routine.json` + optional `.cs` + icon) in `%APPDATA%\AICon\routines`, plus
any team folders listed in `routine-roots.txt`. Two kinds, one file format:

- **`composed`** — an ordered list of existing `aicon:` tool calls with `{{input.x}}` and
  `{{steps.alias.field}}` placeholders. No compilation, always available. **Prefer this.**
- **`script`** — C# in sibling `.cs` files, compiled in memory by Roslyn. Gated behind
  `%APPDATA%\AICon\routines.json` → `allowCodeExecution` (**currently TRUE on this machine**).

MCP tools: `get_authoring_guide` · `list_routines` · `run_routine` · `save_routine` (compiles BEFORE
saving; on failure nothing is written and structured diagnostics come back). Full contract in
`AUTHORING.md`, which is served verbatim by `get_authoring_guide` and shipped beside the DLL.

Ribbon: 8 fixed slot commands bound at startup **plus an always-current browser button** — Revit only
allows ribbon creation during `OnStartup`, so a routine saved mid-session is runnable immediately from
the browser but only gets its own button after a restart. This is stated to the user, not hidden.

---

## 6. The AI decision layer — deliberate architecture

AR400 is **deterministic for ~95% of its work**. An LLM is consulted only at genuinely ambiguous
points and **never as a dependency** — every call fails safe to a deterministic fallback.

Decision points (`Services/AIConDecisionClient.cs`): Excel column mapping · **level-name mapping**
(the one that earns its keep) · tag position · dimension face pair.

Rules that must be preserved:
- **No API key of its own.** It reuses whichever agent the user configured for the chat panel
  (`aiconagent.json`). `ar400ai.json` is optional, only to point decisions at a *different* (e.g.
  free) model. **Never ask the user for another key.**
- **Never applied silently** where it matters: the level mapping is pre-filled into a dialog for the
  user to confirm — a wrong mapping puts wrong drawing numbers on issued sheets.
- **Never inside an open transaction.** These calls block ~10 s against a local model.
- Everything is logged to `%APPDATA%\AICon\decisions.log`. **Read this first when a decision
  "did nothing".** It has solved two mysteries already.

### Tool-execution safety layers (Router / Preview / Log) — separate from the AI-decision layer above

Every `Mutating`/`Unsandboxed` tool call goes through a router → preview → log pipeline in
`ToolDispatcher.cs`, independent of and unrelated to the AI-decision calls above:
- **Router** (`ToolTier`: `Read` / `Mutating` / `Bulk` / `Unsandboxed`) — `run_code` is always
  `Unsandboxed`; a normally-`Mutating` call escalates to `Bulk` past a configurable element/op count
  (`AiconRoutineSettings.BulkElementThreshold` / `BulkBatchOpThreshold`, defaults 50/20).
- **Preview** (`BuildPreviewSummary`) — a best-effort, **non-blocking** "what this will do" summary
  attached to the result (e.g. "12 Walls, 3 Doors"). Informational only; never gates execution, so it
  never stalls an unattended Routine run.
- **Log** (`Services/AuditLog.cs`) — every resolved `Mutating`/`Bulk`/`Unsandboxed` operation writes
  one JSON-line to `%APPDATA%\AICon\audit.log` (tool, tier, `front_door` — `mcp`/`in_revit_panel`/
  `routine`, since AICon has no real user identity —, summary, element ids, success/error). Separate
  file from `bridge.log` and `decisions.log`. A `batch`'s per-op entries are only written after the
  whole transaction actually commits — an all-or-nothing batch that rolls back logs one honest
  "rolled back" line instead of a misleading trail of per-op "successes."
- **`run_code`** is the one tool with a mandatory, blocking gate: every call needs `"confirmed": true`
  or it only echoes the code back without compiling/running it. Composed routines may not name
  `run_code` as a step (`RoutineModel.Validate()` rejects it) — **script routines are entirely
  unaffected**, since `RoutineScriptHost.Run` never calls `run_code` at all; a saved routine still runs
  at full, unattended speed with no confirmation gate, exactly as before.

---

## 7. Rules learned the hard way — do not relearn these

1. **Regenerate before reading back anything created in the same transaction** — view contents, tag
   bounding boxes, category-visibility changes are all invisible until `doc.Regenerate()`.
2. **Room tags are `RoomTag` via `doc.Create.NewRoomTag`**, not `IndependentTag` (which refuses
   rooms). `RoomTag.TagOrientation` takes `SpatialElementTagOrientation`.
3. **Never edit the user's view templates.** Report and leave alone; only touch views AR400 created.
4. **Every `IExternalCommand.Execute` needs a top-level try/catch** that logs the stack trace and
   shows the real message — Revit's default is a useless generic dialog.
5. **Don't create/delete a `View3D` per operation**, and **don't `doc.Regenerate()` per element** —
   create all, regenerate once, then measure.
6. **Accept argument aliases** (`parameter` vs `parameter_name`). Never silently ignore a supplied
   argument — throw a teaching error naming the right tool.
7. **`FilteredElementCollector` is `IEnumerable`** — `.Take(n)` directly, never `.ToElements().Take(n)`.
8. **Roslyn: use `Location.GetMappedLineSpan()`, NOT `GetLineSpan()`** — only the mapped one honours
   the `#line` directive. With the wrong one a 6-line routine is told "error on line 34".
9. **PowerShell 5.1 traps:** `Compress-Archive` writes backslash zip entries (breaks .xlsx);
   `Out-File -Encoding utf8` adds a BOM that breaks JSON parsers — use
   `[IO.File]::WriteAllText(..., UTF8Encoding($false))`. Here-strings have **no trailing newline**.
10. **WPF/Revit name collisions:** `Autodesk.Revit.DB.Grid`/`Color`, `Autodesk.Revit.UI.ComboBox`/
    `TextBox` clash with WPF — alias them (`using Grid = System.Windows.Controls.Grid;`).
11. **Dialogs must set `Owner` to Revit's main window** (`WindowInteropHelper`), not `Topmost` —
    `Topmost` alone deadlocks input focus when a TextBox takes focus.
12. **When a small model behaves bizarrely, check our tool surface first.** Twice the root cause was
    ours (an arg name our own prompt taught it; a missing single-call tool), not the model.
13. **The `run_code` "angle brackets break" folklore is FALSE** — tested live; `List<T>`,
    `Dictionary<K,V>` and LINQ all work. Do not reintroduce `ArrayList` workarounds.
14. **An em-dash (—) inside a `Write-Host "..."` string literal breaks `install.ps1` on Windows
    PowerShell 5.1** — the script has no BOM, and without one the parser can misread the em-dash's
    3-byte UTF-8 sequence (`E2 80 94`) under a non-UTF-8 codepage; one of those misread bytes happens
    to look like a closing curly quote, silently truncating the string and cascading into a real
    "missing terminator" parse error a few lines later — not a false positive, genuinely breaks the
    script (caught before shipping, 2026-09-08). An em-dash inside a `#` comment is fine (comments
    aren't tokenized as strings); only inside an actual string literal does it bite. Use a plain
    hyphen (`-`) in any `Write-Host`/string-literal text instead — matches this file's own em-dash
    convention in prose, just not inside PowerShell string literals.

---

## 8. State of play

**Verified working in the real model:** the full AR400 chain (views → sheets → viewports → templates
→ tags → schedules → layout, one Ctrl+Z, safe re-runs); face-to-face wall dimensioning (two 200 mm CMU
walls 2648.3 mm apart centre-to-centre returned exactly **2448.3 mm**); the chat panel with
Gemini/DeepSeek/local; AI level-mapping; the Excel register reader; **the routine pipeline end-to-end**
(`save_routine` → `list_routines` → `run_routine` all exercised live).

**v3.2.0's Router/Preview/Log layers + running a SCRIPT routine inside Revit — now actually exercised**
(2026-09-07, against Snowdon Towers, a real non-trivial sample: curtain systems, cores, 55 sheets),
closing the "never yet exercised against a live model" gap this section used to flag. Two real bugs
found this way, both fixed — see CHANGELOG.md's v3.2.1/v3.2.2 entries for the full detail:
1. **Routine edits were silently invisible without a Revit restart** — `RoutineScriptHost`'s compiled-
   Type cache was keyed by routine id alone, so `save_routine` overwriting a script's `.cs` file never
   invalidated it; every `run_routine` after the first kept running whatever compiled the very first
   time that id was ever run. Fixed: cache key now also carries an MD5 hash of the routine's own
   source, so an edit is picked up on its very next run — proven live (edited a threshold from 100→120,
   reran with no restart, the reported count changed immediately). The one remaining restart-needed
   case is a brand-new routine's own ribbon button (Revit only builds ribbon panels at startup — a
   separate, genuine Revit API limit).
2. **`thin-wall-audit`'s blind `.Cast<Wall>()` crashes on any real project with an in-place
   FamilyInstance under the Walls category** (Snowdon Towers has several — curtain/solar-wall
   systems). Fixed with `.OfType<Wall>()`.

**Also found the SAME session, root-caused, fixed in source but deliberately NOT redeployed:**
`run_code`'s documented `"confirmed": true` gate never actually took effect through MCP —
`shared/ToolRegistry.cs`'s published schema never declared a `confirmed` property, so no strict MCP
client could ever pass it through even though `ToolsExtended.cs`'s handler has required it since
v3.2.0. Fixed in source (builds clean); needs `AIConServer.exe` rebuilt AND relaunched to take effect —
left alone this pass since that is the live process a session talks to over stdio, not something to
swap out as a side effect of an unrelated fix.

**Ribbon UX gap found and fixed the same session:** a script routine's `return` value (its whole point
for a report-style routine like `model-qa-report`) was silently discarded when run from the ribbon —
the popup only ever said "N step(s) ran", with no way to see what the routine actually found unless it
was run from chat instead. `RoutineCommands.cs` now renders the returned value (bulleted, indented,
camelCase keys humanized) directly in the popup, and no longer claims "Ctrl+Z undoes it" for a
`readOnly` routine that changed nothing.

**Still built but NOT yet exercised in Revit:** AR400's automatic **grid dimension strings** and the
**bulk wall-pair dimensioning** pass (the primitive *is* verified, the batch pass is not); title-block
strip auto-detection; the v2.16.0 performance fixes; the routine **ribbon buttons and input form** (the
engine is proven, the WPF layer has not been clicked yet). First things to actually click: a `Bulk`-tier
delete, `run_code` without then with `confirmed:true` (once `AIConServer.exe` picks up the fix above),
and confirming a `run_code` step is rejected from a composed routine at save time.

**Two new bugs found live, NOT yet root-caused:**
- `place_family_instance` for a door returned success (id + correct family/type) but the instance never
  actually persisted — door count unchanged before/after, confirmed via `list_elements`. An identical-
  pattern window placement in the same batch call worked correctly.
- `tag_elements` on a Room failed with "no loaded tag type" even though the project has hundreds of
  existing Room Tags — plausibly needs to `.Activate()` a `FamilySymbol` before first use in a session.

**v3.2.3 (2026-09-08):** `scripts/install.ps1` now seeds `allowCodeExecution: true` into a fresh
`%APPDATA%\AICon\routines.json` on install (only if that file doesn't already exist) — prompted by a
colleague hitting the exact "set it to true by hand, still doesn't work, no idea why" confusion
v3.1.3's error-message fix was meant to catch. New installs no longer need that manual edit at all;
existing installs are untouched either way. **Also this version:** `install.ps1` now connects ChatGPT
Desktop / Codex too (§2 explains why this is architecturally the same MCP server Claude Desktop
already uses, not a new remote-server project), and a real em-dash-in-a-string-literal parse bug got
caught and fixed before shipping (§7 item 14). **Confirmed live (2026-09-08):** after running the new
installer, `aicon` shows up and is toggled ON under ChatGPT Desktop's own **Settings → Plugins → MCPs**
tab, alongside Codex's built-in `node_repl` server — the registration genuinely works, not just in
theory. **Still open:** an actual tool call through it (e.g. "What's in my Revit model?" from a real
ChatGPT Desktop chat with Revit open) has not yet been confirmed end-to-end. Details: CHANGELOG.md's
v3.2.3 entry.

**Installed on this machine right now:** the 8 stock routines plus `model-qa-report` (script,
read-only — one-click warnings/thin-tall-walls/room/mark/empty-sheet audit, built and proven this
session). **Code execution is ENABLED.**

**Loose end:** a test dimension (id 1495387, view `00-GROUND`) left in the live model from verifying
`create_wall_dimension`. Harmless; delete when convenient.

**Reference material:** `S:\HOSSAM\BadBoy_3\reference\AnalyzeTool` — a cloned OSS Revit add-in
(**Apache 2.0**, not MIT) whose Roslyn scripting pattern informed the Button Builder. Read for
patterns; no code was copied.

**Full version history** (v3.1.0 audit fixes, Revit 2025/2026/2027 support, the two v3.1.3 field-bug
fixes): see [`CHANGELOG.md`](CHANGELOG.md).

---

## 9. How to work on this

1. Build after every change: `dotnet build plugin/AICon.csproj -c Release`. If you touched
   `shared/ToolRegistry.cs`, also build `server/` and `agent/`.
2. **Let the compiler validate Revit API guesses.** Several plausible members don't exist
   (`BuiltInCategory.OST_RampsTags`) or have a different type than expected. Build, don't assume.
3. **Verify against the live model rather than reasoning in the abstract** — the add-in exposes tools
   that query it directly, and Revit is usually running.
4. **Write a throwaway harness for anything non-obvious.** Compiling the REAL source files in a
   scratch console has caught two shipping bugs (`GetMappedLineSpan`, and a silent chat-attachment
   regression). Scratch projects live under the session scratchpad.
5. Adding a tool = implement in `ToolsExtended.cs` + `case` in `ToolDispatcher.cs` + add to `Mutating`
   if it writes + schema in `shared/ToolRegistry.cs` (+ the local subset if a small model should see it).
6. Keep comments explaining **why**, not what. The existing ones carry hard-won context.
7. Bump the version in `plugin/AICon.csproj`'s `<Version>` only, then run
   `scripts/build-package.ps1` — `App.cs` and `build-package.ps1` both read it from there at
   build time, not a second hand-typed copy (confirmed while doing the v3.2.0 bump).

## 10. Discussed, not started
Batch PDF export per package named from the register · a pre-issue QA check (missing templates,
unnumbered sheets, untagged rooms, views off-sheet) · revision/issue management · an L1 "record the
calls I just made" recorder (currently the agent authors routine JSON directly, which is simpler and
works from every front door).

**Autodesk Marketplace publishing — planned, not started (2026-09-07).** Full plan approved and
saved at `C:\Users\h.yousef\.claude\plans\robust-discovering-sundae.md` (a Claude Code plan file,
not part of this repo) — paste/read that file at the start of a session to resume this thread
without re-researching it. Goal: list AICon on the Autodesk Design and Make Marketplace
(apps.autodesk.com) as a **$10/month subscription**. Key facts already researched and confirmed
(sources in that chat session, not repeated here): the marketplace bills recurring subscriptions
natively (no need to build our own Stripe billing); current publisher commission is **0.0%**
(Autodesk can change this unilaterally); Autodesk provides a ready-made **Entitlement API**
(`apps.autodesk.com/webservices/checkentitlement`) to check a signed-in user's paid status at
runtime; Autodesk has a dedicated **MCP Publisher track** (Tool Manifest, `ai_llm_providers`
disclosure, a Publisher Declaration Form) that fits AICon better than the generic Revit-plugin
track, and Autodesk's own June 2026 blog post shipping their own (read-only) Revit MCP server
explicitly welcomes third-party MCP servers alongside it. **Four decisions already locked with the
owner:** (1) publish under the individual name Hossam Yousef, no company needed; (2) build a
separate, cleaned **public** distribution for the listing — this repo (with HORIZON TOWER
references etc.) stays the internal working copy, never goes public. **Reinforced 2026-09-07: the
Marketplace work must live in a genuinely separate folder/repo, not a subfolder or branch of this
one** — nothing for that effort is ever committed to or pushed from
`S:\HOSSAM\BadBoy_3\AICon`/`github.com/LOLOCATTY/AICon`; (3) `run_code` ships
**locked off by default** in the public build only (seed `routines.json` with
`"allowRunCode": false` in the public installer — no source fork needed, reuses
`AiconRoutineSettings` as-is); (4) pursue the MCP Publisher track specifically. **Status:** the
plan is approved but execution has not started — the owner was asked which section to begin with
(public-distribution split + Tool Manifest generation vs. drafting the Privacy Policy/Declaration
Form) and deferred the choice for a later session. Ask again before picking a starting point.
