# AICon — Project Handoff Brief

> Paste this whole file into a new AI session before asking it to work on this codebase.
> It describes what AICon is, how it is built, exactly where development has got to, and the
> non-obvious rules that were learned the hard way.
>
> **Current state: v3.1.3 · 2026-09-06 · Revit 2023–2027 (compiled; 2023/2024 live-verified,
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

1. **Claude Desktop** over MCP (external stdio server → `localhost:55234` → the add-in).
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

---

## 8. State of play

**Verified working in the real model:** the full AR400 chain (views → sheets → viewports → templates
→ tags → schedules → layout, one Ctrl+Z, safe re-runs); face-to-face wall dimensioning (two 200 mm CMU
walls 2648.3 mm apart centre-to-centre returned exactly **2448.3 mm**); the chat panel with
Gemini/DeepSeek/local; AI level-mapping; the Excel register reader; **the routine pipeline end-to-end**
(`save_routine` → `list_routines` → `run_routine` all exercised live).

**Built but NOT yet exercised in Revit:** AR400's automatic **grid dimension strings** and the **bulk
wall-pair dimensioning** pass (the primitive *is* verified, the batch pass is not); title-block strip
auto-detection; the v2.16.0 performance fixes; the routine **ribbon buttons and input form** (the
engine is proven, the WPF layer has not been clicked yet); running a **script** routine inside Revit
(its C# was compile-verified offline against the real Revit API + AICon.dll, but never executed).

**Installed on this machine right now:** 3 routines — `level-sheet-set`, `tall-wall-check`
(composed), `thin-wall-audit` (script). **Code execution is ENABLED.**

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
7. Bump the version in `plugin/App.cs`, `plugin/AICon.csproj` and `scripts/build-package.ps1`
   together, then run `scripts/build-package.ps1`.

## 10. Discussed, not started
Batch PDF export per package named from the register · a pre-issue QA check (missing templates,
unnumbered sheets, untagged rooms, views off-sheet) · revision/issue management · an L1 "record the
calls I just made" recorder (currently the agent authors routine JSON directly, which is simpler and
works from every front door).
