# AICon — Changelog

Detailed, dated history of fixes and platform-support work, newest first. For
the current state at a glance, see `HANDOFF-PROMPT.md` §8 "State of play";
for the technical audit this grew out of, see `docs/AUDIT.md`.

---

## v3.2.0 — 2026-09-07

**Router / Preview / Log safety layers, plus a `run_code` confirm gate.** Prompted by an architecture
discussion (docs/AUDIT.md findings F03/F06/F08/F16 had already flagged the underlying gaps): the tool
dispatch pipeline (`ToolDispatcher.cs`) treated every mutating call identically, showed no expected
effect before committing, and had no general audit trail (only `delete_elements` got a one-off log
line). `run_code` compounded this — full Revit API + unrestricted .NET access, no prior review, gated
by a switch that defaults on.

**Router:** `ToolTier` (`Read` / `Mutating` / `Bulk` / `Unsandboxed`) replaces the old single
`Mutating` boolean. `run_code` is always `Unsandboxed`. A normally-`Mutating` call escalates to `Bulk`
past a configurable element/op count (`AiconRoutineSettings.BulkElementThreshold` /
`BulkBatchOpThreshold`, default 50/20). Read tools are untouched — same fast path as before.

**Preview:** `BuildPreviewSummary` computes a plain-language "what this will do" line (e.g. "12 Walls,
3 Doors", or a move's translation vector) before the transaction opens, attached to the result as
`"summary"`. Deliberately non-blocking — informational only, so it can never stall an unattended
Routine run.

**Log:** new `Services/AuditLog.cs` writes one JSON-line per resolved mutating/`Unsandboxed` operation
to `%APPDATA%\AICon\audit.log` (tool, tier, `front_door` — `mcp` / `in_revit_panel` / `routine`, the
closest honest answer to "who" AICon has, since it has no real user identity — summary, element ids,
success/error). Separate file from `bridge.log` and `decisions.log`. Granularity is per-operation, not
per-transaction, so a partially-failed `batch` shows exactly which ops actually stuck — and for the
all-or-nothing default, per-op entries are buffered and only flushed once the transaction genuinely
commits; a rollback logs one honest "nothing committed" line instead of a misleading trail of
individually-"successful" entries that then got undone.

**`run_code`:** now requires `"confirmed": true` on every call — without it, the call returns the code
that would run and does not compile or execute anything. Works identically across all three front doors
(no native-dialog-vs-stdio split to design). Every call is logged unconditionally, including the
`no_transaction:true` path, which previously had no logging at all (closes F08). `AllowRunCode` stays
default-on (F16's inversion was judged already addressed by the new per-call gate, not worth the
day-to-day friction of flipping the machine default).

**Composed Routines may no longer name `run_code` as a step** (`RoutineModel.Validate()` rejects it at
save time) — closes the one path by which the new confirm gate could otherwise stall an unattended,
ribbon-button-triggered routine run. **Script Routines are entirely unaffected by any of this change**:
`RoutineScriptHost.Run` never calls `run_code` — verified by reading the code, not assumed — so a saved
script routine runs at exactly the same speed, with no summary/log/confirm step added, before and
after this version.

Drive-by fix: `run_code`'s "AllowRunCode is off" error message used to say "restart Revit to re-enable
it" — wrong, `AiconRoutineSettings.Load()` re-reads the file fresh on every call, same as it already
said for `AllowCodeExecution`'s equivalent message. Corrected.

Builds clean on all four targets (net48, net8.0-windows, net10.0-windows, net10.0-windows-2027).
**Not yet exercised against a live model** — see HANDOFF-PROMPT.md §8.

---

## v3.2.1 — 2026-09-07 (same day, first real live-model pass)

Prompted directly by a comprehensive tool-by-tool test against the Snowdon Towers sample project
(a real, non-trivial AEC model — curtain systems, cores, party walls, 55 sheets) plus building and
testing a new script routine (`model-qa-report`) end to end. Two real bugs found this way, both
root-caused in the actual code and fixed, not patched around:

1. **Editing a script routine silently kept running the OLD code — no error, no warning.**
   `RoutineScriptHost.cs`'s compiled-Type cache was keyed by routine id alone; `save_routine`
   overwriting `Routine.cs` on disk never invalidated it, so every `run_routine` call after the first
   kept reusing whatever Type got compiled the very first time that id was ever run — reproduced
   live: `model-qa-report` was saved once with a bug, fixed and resaved, and `run_routine` (both via
   MCP and the ribbon button) kept throwing the ORIGINAL bug's error until Revit restarted. Fixed:
   the cache key now also carries an MD5 hash of the routine's own `.cs` file contents
   (`RoutineScriptHost.ComputeSourceHash`); a hash mismatch forces a fresh compile into a fresh
   assembly, and the old one is simply never used again (still resident in memory for the rest of the
   session — .NET Framework genuinely can't unload it — but that is a memory-growth cost now, not a
   correctness bug). Saving *and* editing a routine both now take effect on the very next
   `run_routine` call, no restart. The ONE remaining restart-needed case is a brand-new routine's own
   ribbon button, because Revit only builds ribbon panels at startup — unrelated, unavoidable, and
   already stated honestly in AUTHORING.md and `save_routine`'s own response `note`.
   [plugin/Routines/RoutineScriptHost.cs](plugin/Routines/RoutineScriptHost.cs)

2. **`thin-wall-audit` (a shipped example routine) crashes on any real project with an in-place
   FamilyInstance under the Walls category** (Snowdon Towers has several — curtain/solar-wall
   systems) — `Unable to cast object of type 'FamilyInstance' to type 'Wall'`. Blind `.Cast<Wall>()`
   after `OfCategory(OST_Walls).WhereElementIsNotElementType()` is the classic FilteredElementCollector
   trap: that category is not guaranteed to contain only real `Wall` objects. Fixed by switching to
   `.OfType<Wall>()` (silently skips anything that isn't a `Wall`) in both places it collects walls
   (main model and, when `includeLinks` is on, each linked document). Fixed in the shipped example
   ([examples/routines/thin-wall-audit/Routine.cs](examples/routines/thin-wall-audit/Routine.cs)) and
   pushed live via `save_routine` to this machine's installed copy.

**Also found, fixed in source, NOT yet redeployed** (would mean rebuilding/relaunching
`AIConServer.exe` — the live MCP process a session is talking to over stdio; deliberately left for a
deployment done on purpose, not as a side effect of an unrelated fix):

3. **`run_code`'s documented `"confirmed": true` re-invocation never actually took effect** — called
   twice with identical arguments plus `confirmed: true`, both times the response was the unexecuted
   "confirmation_required" echo. Root cause: `shared/ToolRegistry.cs`'s published MCP schema for
   `run_code` never declared a `confirmed` property (only `code` and `no_transaction`) even though
   `ToolsExtended.cs`'s handler has required it since v3.2.0 — so the confirm-then-resend workflow
   this version's changelog entry describes was never actually reachable through the MCP surface,
   exactly the "not yet exercised against a live model" gap flagged above. Fixed: `confirmed` added to
   the declared schema. [shared/ToolRegistry.cs](shared/ToolRegistry.cs)

Builds clean (net48, and the server's net10.0). net48 rebuilt and deployed to this machine's live
Revit 2024 Addins folder (old locked DLL renamed `.old` aside, as `install.ps1` already does) —
takes effect on next Revit restart. `AIConServer.exe` was rebuilt to verify #3 compiles but was
**not** redeployed this pass.

**Still open, found live, not yet root-caused:**
- `place_family_instance` for a door returned success (id + correct family/type) but the instance
  never actually persisted — door count unchanged before/after, confirmed via `list_elements`. An
  identical-pattern window placement in the same batch call worked correctly.
- `tag_elements` on a Room failed with "no loaded tag type" even though the project has hundreds of
  existing Room Tags — plausibly needs to `.Activate()` a `FamilySymbol` before first use in a
  session.

---

## v3.2.2 — 2026-09-07 (ribbon result display)

Prompted directly by confusion over the v3.2.1 fixes above: `model-qa-report` finally ran without
error after the Revit restart, but clicking its ribbon button showed only a generic "1 step(s) ran"
dialog — none of the actual report (warnings, thin/tall walls, empty sheets, ...) was visible anywhere.

**Root cause:** `RoutineCommands.cs`'s ribbon success dialog always showed a fixed "N step(s) ran"
message and silently discarded `result.ScriptResult` — the value a script routine's `return` produces.
That is fine for a routine whose whole point is a model change (colour a wall, place a sheet — you see
the effect in the model), but a `readOnly` report-style routine's entire output IS that return value;
run from chat it reaches the AI as real JSON, but run from the ribbon there was no chat to hand it to,
so it was just thrown away. Not a new bug — true since script routines could return structured data at
all; only exposed once a routine was actually built around returning a report.

**Fixed:** the popup now renders `result.ScriptResult` when present — bulleted, indented up to a few
levels, camelCase keys humanized ("warningsByType" → "Warnings By Type"), long lists capped with an
"…and N more" line and the whole thing capped around 3500 characters (TaskDialog does not scroll) with
a "run from chat for the full result" note past that. A composed routine (no `ScriptResult`) keeps the
original "N step(s) ran" message unchanged. Also corrected the footer: it always said "One Ctrl+Z
undoes the whole routine", which is actively wrong for a `readOnly` routine that changed nothing — it
now says so instead. [plugin/Routines/RoutineCommands.cs](plugin/Routines/RoutineCommands.cs)

Builds clean (net48). Deployed to this machine's live Revit 2024 install; takes effect on next Revit
restart (it's a change to the add-in itself, same platform limit as always — see AUTHORING.md §5).

---

## v3.2.3 — 2026-09-08

**`scripts/install.ps1` now seeds `allowCodeExecution: true` on a fresh install** (step [3/5],
`%APPDATA%\AICon\routines.json`) — prompted by a colleague hitting exactly the class of confusion
v3.1.3's `Load(out parseError)` fix was meant to catch: script Routines silently staying off with no
clear reason why, needing a hand-edit of `routines.json` to fix. Rather than every new machine
needing that same manual edit, `install.ps1` now writes `{"allowCodeExecution": true}` there itself —
**only** when the file does not already exist, so a reinstall/update never overwrites a user's own
later choice to turn it back off. `allowRunCode` is left unset in the seed on purpose — it already
defaults to `true` in code, so there is nothing to seed for it.

**`install.ps1` now also connects ChatGPT Desktop / Codex CLI automatically** (step [5/5]) —
confusingly similar-sounding but architecturally unrelated to ChatGPT's web/cloud "Connectors"
feature (which requires a remote HTTPS MCP server and is out of scope for AICon's local-only design).
The **desktop app** shares one local config file with Codex CLI and its IDE extension,
`%USERPROFILE%\.codex\config.toml`, and — like Claude Desktop — can launch a local stdio MCP server
directly, so this needed no new server code, just an installer step that appends a
`[mcp_servers.aicon]` block (TOML, not JSON — a **literal** `'...'` string so a Windows path's
backslashes need no escaping) pointing at the same `AIConServer.exe` Claude Desktop already uses.
Skips gracefully if `%USERPROFILE%\.codex` doesn't exist yet (app not installed — same pattern as the
Claude Desktop step), and never touches an already-configured `[mcp_servers.aicon]` section or
anything else already in the file. Steps renumbered [0/5]–[5/5] throughout the script to make room
for both additions. [scripts/install.ps1](scripts/install.ps1)

**Real bug caught before shipping, not just a style note:** an em-dash (—) inside a `Write-Host "..."`
string literal breaks this script's parsing on Windows PowerShell 5.1 — the file has no BOM, and
under a non-UTF-8 codepage one byte of the em-dash's 3-byte UTF-8 sequence is misread as a closing
curly quote, silently truncating the string and cascading into a real syntax error a few lines later.
An em-dash inside a `#` comment is fine (never tokenized as a string) — only a literal inside an
actual string broke it. Fixed by using a plain hyphen in the three new `Write-Host` lines instead; see
HANDOFF-PROMPT.md §7 item 14 for the full mechanism.

Builds clean on all four targets. Packaged as `dist/AICon-3.2.3.zip`.

---

## v3.1.3 — 2026-09-06

### Script-routine bug batch (external field report)
Still the same version (user's explicit call — this round folded into the
already-built 3.1.3 rather than bumping again). A bug report
(`aicon-script-routine-bug-context.md`, a different user/company —
"ELBOSTAN") described two script-routine problems. Both root-caused in the
actual code, not guessed:

1. **`allowCodeExecution: true` in `routines.json` "still didn't work"** — `AiconRoutineSettings.Load()`
   ([plugin/Routines/AiconRoutineSettings.cs](plugin/Routines/AiconRoutineSettings.cs)) swallowed ANY
   JSON parse failure silently and fell back to OFF with no way to tell "the key is missing" from "the
   file exists but is broken" (a stray smart-quote from pasting is enough). Fixed: `Load(out string
   parseError)` overload surfaces the real reason; `RoutineScriptHost.cs`'s error message now includes
   it when present, and no longer tells the user to restart Revit (the flag is read fresh every run —
   confirmed by reading the call site, not assumed). Also added `PropertyNameCaseInsensitive = true` for
   consistency with `RoutineStore.cs`'s own JsonSerializerOptions.
2. **Arabic text in a routine's `description` rendered as mojibake in the Run dialog, English fields
   fine.** NOT a routine-JSON round-trip bug (that path is clean UTF-8 both ways, confirmed). Root cause:
   `server/Program.cs` (the actual MCP stdio server Claude Desktop talks to) set `Console.OutputEncoding
   = new UTF8Encoding(false)` but never set `Console.InputEncoding` — which then defaults to the OS's
   console input codepage, NOT UTF-8, on a non-English Windows locale. Every multi-byte UTF-8 byte
   sequence coming in over stdin (Claude Desktop always sends UTF-8 JSON-RPC) got misdecoded right there
   before JSON parsing ever saw it; ASCII bytes are identical across codepages, which is exactly why only
   non-ASCII text broke. Proof this was a real omission, not a design choice: `agent/Program.cs` (the
   console host) already sets BOTH lines. Fixed by adding the matching `Console.InputEncoding` line to
   `server/Program.cs`.
3. **Bonus finding while reading the script compiler for issue 1 — a real, separate limitation**: the
   user separately asked to be able to run "any code, even big and complex" in a routine.
   `routine.Script.Files` was already a list, but `AiconScriptCompiler.Compile()`
   ([plugin/Routines/AiconScriptCompiler.cs](plugin/Routines/AiconScriptCompiler.cs)) decided
   per-FILE whether to wrap it as a bare body or use it as-is (`IsFullRoutine` check) — so a second
   "helper" file that is a plain class (not itself an `IAiconRoutine`) got its whole class declaration
   stuffed inside a generated method body: invalid C#. Multi-file routines were effectively broken
   beyond file 1. Fixed: only `files[0]` (the entry point) gets the bare-body-or-IAiconRoutine
   treatment; every other file compiles as-is into the same assembly, free to be called from the entry
   file. Also fixed `RoutineTools.SaveRoutine`'s pre-save compile check, which was compiling each file
   in isolation via the now-removed `CompileSnippet` — replaced with `CompileForSave`, which checks all
   of a routine's files together in the same order/entry-point rule the real run uses, so save-time
   validation can never diverge from run-time behavior. **Verified with a dedicated harness**
   (`scratchpad/multifiletest`, compiles the real `AiconScriptCompiler.cs`/`RoutineModel.cs` unmodified,
   no Revit API needed by using a locally-declared decoy `IAiconRoutine` so `IsFullRoutine`'s string
   check fires without needing RevitAPIUI.dll loadable outside of actually being hosted in Revit.exe):
   4/4 checks pass — the old per-file-wrap bug reproduces, a 2-file and a 3-file routine both compile
   under the fix, and file order matters exactly as designed (entry must be `files[0]`).
   `AUTHORING.md` updated with the multi-file convention and an example.

All three fixed and build-verified (0 warnings/0 errors); packaged into the same `AICon-3.1.3.zip`
(rebuilt, not a new version number — the user's explicit choice this round).

### Stale-file install bug
A colleague running Revit 2026 hit `Could not switch AICon agent: Could not load file or assembly
'System.Text.Json, Version=8.0.0.5, ...'` plus every MCP tool call (`list_elements`, etc.) failing the
same session — looked like two bugs, was one. Diagnosed **without a live Revit 2026** by comparing
assembly identities directly: reflection on the shipped `net10.0-windows` `AICon.dll` shows it actually
references `System.Text.Json, Version=10.0.0.0` (matches this machine's .NET 10.0.9 SDK) — not
8.0.0.5. Version 8.0.0.5 is the AssemblyVersion of the NuGet `System.Text.Json` **8.0.5** package's
compat-shim build, confirmed via `plugin/obj/project.assets.json` to be exactly what the **net48**
target resolves (an early build once referenced it for net8.0-windows too, before the "redundant,
framework already has it" cleanup — see the `net8.0-windows`/`net10.0-windows` `PropertyGroup`
comment). Conclusion: a leftover `System.Text.Json.dll` from an older install was still sitting in the
colleague's `%APPDATA%\Autodesk\Revit\Addins\2026\AICon\` folder. .NET probes a plugin's own folder
for a same-named assembly **before** falling back to the shared framework, so that stale, older file
shadowed the correct in-box one — breaking every code path that touches JSON (agent switching AND the
whole tool-dispatch bridge alike, hence both symptoms from one cause). Root bug: `install.ps1` only
ever copied files over an existing install, never deleted what a previous version left behind that the
new one doesn't ship. Fixed in `scripts/install.ps1`'s per-year install loop — the whole `AICon`
destination folder is now wiped file-by-file before the fresh copy (still renaming aside, not
deleting, any file locked by a currently-running Revit, same as the pre-existing DLL-lock handling).
**Immediate workaround that does not need this fix:** delete
`%APPDATA%\Autodesk\Revit\Addins\2026\AICon\` by hand, then reinstall.

---

## Revit 2027 support — 2026-08-30
Same runtime as 2026, different binary, builds clean.

The user supplied `RevitAPI.dll`/`RevitAPIUI.dll` directly this time (from a colleague's licensed 2027
install — same provenance pattern as 2026's). Stored at `lib/revit-refs/2027/` (gitignored, same
treatment as 2026's).

Verified by compiler, not assumed, exactly like 2026 was: referencing 2027's RevitAPI.dll against
`net10.0-windows` compiled clean on the first try — **2027 stayed on .NET 10, did not jump again the
way 2026 jumped past 2025's .NET 8.** But 2026's and 2027's RevitAPI.dll are still two different
assemblies (`AssemblyVersion` 26.5.0.0 vs 27.2.0.0) — same TFM, not the same binary, and whether one
built DLL would actually load correctly against the OTHER year's live RevitAPI.dll inside Revit is
just as unverified for this net10/net10 pair as it always was for net48's 2023/2024 pair. So 2027 got
its own build rather than reusing 2026's, via a new `RevitApiYear` MSBuild property on
`net10.0-windows` (default `2026`; `2027` selected with
`dotnet build -f net10.0-windows -p:RevitApiYear=2027`, output redirected to its own
`bin\Release\net10.0-windows-2027\` folder via `AppendTargetFrameworkToOutputPath=false` — a plain
`OutputPath` override is NOT enough on its own, MSBuild still appends `$(TargetFramework)` on top of
it by default, which the first attempt at this got wrong before the fix landed).
`scripts/build-package.ps1` now builds this extra pass explicitly and ships a fourth folder
(`AICon\net10.0-windows-2027\`); `scripts/install.ps1` routes year 2027 to it, same one-year-per-entry
policy as before (2028+ still reported found-but-unsupported).

**What is still NOT verified for 2027:** identical gap to 2025 and 2026 — compiled clean, never run
inside a live Revit 2027 process (none installed on this machine). Whoever eventually gets a real 2027
launch is the first real test, same as the still-open 2025/2026 gaps above.

---

## Revit 2026 support — 2026-08-23
Builds clean too, NOT yet run live.

A colleague (whose machine hit the dockable-pane crash below) sent `RevitAPI.dll`/`RevitAPIUI.dll` from
their own licensed Revit 2026 install — legitimate provenance, copied from a real install, never
downloaded from the internet (see the conversation this session if that judgment call needs revisiting).
Stored at `lib/revit-refs/2026/` (gitignored — Autodesk's files, never redistributed, same
`Private=false` compile-only treatment every Revit reference gets here).

**Important correction to an assumption:** the plan going in was "2025 and 2026 both moved to .NET 8,
so one net8.0-windows build might cover both." That assumption was WRONG and was caught by the
compiler, not by reasoning about it: referencing 2026's RevitAPI.dll from the net8.0-windows target
failed with CS1705 — "'RevitAPI' ... uses 'System.Runtime, Version=10.0.0.0' ... higher version than
referenced assembly 'System.Runtime' ... Version=8.0.0.0". **Revit 2026 moved to .NET 10, not .NET 8.**
`plugin/AICon.csproj` now has a THIRD target, `net10.0-windows`, referencing the 2026 DLLs above.
**All three targets (net48, net8.0-windows, net10.0-windows) compile 0 errors / 0 warnings** against
their respective real Revit API — no other 2026 API break beyond what 2025 already required showed up.
`scripts/build-package.ps1`/`scripts/install.ps1` updated the same way F04/F05's pattern was: three
folders in the zip, installer picks per exact year (2022-2024→net48, 2025→net8.0-windows,
2026→net10.0-windows), anything else still reported found-but-unsupported rather than guessed at.

**What is still NOT verified for 2026:** exactly the same gap as 2025 — never run inside a live Revit
2026 process (no Revit 2026 installed on THIS machine to launch). The colleague who supplied the
reference DLLs does have a real 2026 install, so once the message-fix version reaches them (see below),
their next real launch is the first opportunity to find out. **Lesson for whoever picks this up next:**
do not assume Revit N+1 shares Revit N's exact .NET version just because both post-date the
net48→net8 jump — check by referencing the real DLL and reading what the compiler says, the same way
this was actually discovered.

A colleague separately hit Revit 2026's version and got a raw "dockable pane has not been created yet"
crash — root-caused to `OnStartup` silently swallowing a `RegisterDockablePane` failure (see
`ChatPaneRegistered` in App.cs, added 2026-08-23). That message fix does NOT by itself make 2026
supported (2026 wasn't compiled against at all yet when that fix shipped) — it only made an
unsupported-version failure legible instead of cryptic. With `net10.0-windows` now built, whether that
colleague's ORIGINAL crash is actually resolved (as opposed to just producing a clearer message) is
still unverified — needs their next real test to confirm.

---

## Revit 2025 support — 2026-08-23
Builds clean, NOT yet run live.

Revit 2025 got installed on this machine (full install, `C:\Program Files\Autodesk\Revit 2025\`).
`plugin/AICon.csproj` now multi-targets `net48;net8.0-windows` — one source tree, two DLLs, MSBuild
builds both every time (`bin\Release\net48\AICon.dll` for Revit 2023/2024, `bin\Release\net8.0-windows\AICon.dll`
for Revit 2025). **Both compile with 0 errors, 0 warnings against the real Revit 2025 RevitAPI.dll/RevitAPIUI.dll**
— no other Revit 2025 API break beyond ElementId turned up at compile time. One real bug already caught
this way: `Microsoft.CodeAnalysis.CSharp` (Roslyn — `run_code`/script Routines) was NOT being copied to
the net8.0-windows output folder by default, which would have made `run_code` fail at runtime on 2025
with a missing-assembly error; fixed with `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`
(net8.0-windows only — net48 never needed it). `scripts/build-package.ps1` and `scripts/install.ps1`
updated: the packaged zip now ships both builds side by side (`AICon\net48\`, `AICon\net8.0-windows\`),
and the installer picks the right one per detected Revit year — **2025 only, not "2025+"**: a Revit
year past 2025 is reported as found-but-unsupported rather than silently installed with unverified code.

**What is still NOT verified:** this build has never actually RUN inside a live Revit 2025 process —
Revit 2025 has never been launched on this machine (`%APPDATA%\Autodesk\Revit\Addins\2025\` does not
exist yet, meaning Revit creates it on first launch and hasn't been opened here even once). Compiling
clean rules out a huge class of problems but not runtime-only ones (WPF hosting differences, dockable
pane registration — see the Revit 2026 dockable-pane report above, a DIFFERENT unsupported version —,
behavioral API changes the compiler can't catch). Do not describe 2025 as "done" until it has actually
opened a model and run a real tool call.

---

## v3.1.0 — 2026-08-18
A full technical audit, see `docs/AUDIT.md`, plus fixes.

`run_code` migrated off the legacy CodeDom compiler onto the same Roslyn pipeline script Routines use
(`AiconScriptCompiler.CompileRunCodeBody`) — modern C# now works there (`$"..."`, `?.`, `var`, LINQ),
verified live in Revit against the Snowdon Towers sample model. A shared-secret token
(`%APPDATA%\AICon\bridge.token`) is now required on every localhost bridge call. `run_code` got its
own on/off switch (`AiconRoutineSettings.AllowRunCode`, defaults on). `delete_elements` now logs
unconditionally. AR400's view-template/scope-box failures are no longer silently swallowed. Full
findings + fix status: `docs/AUDIT.md`.
