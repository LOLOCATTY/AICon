# Authoring AICon Routines

A **routine** is a saved capability that becomes a real button in Revit. Where a chat answer scrolls
away, a routine is still there next week — and a colleague can click it without knowing an AI exists.

You are reading this because you are about to author one. Read it once; it is short.

---

## 1. What a routine is on disk

A **folder**, nothing more:

```
level-sheet-set/
├── routine.json      ← required
├── Routine.cs        ← only for kind = "script"
└── icon32.png        ← optional
```

Folders live in `%APPDATA%\AICon\routines`, plus any team folders listed one-per-line in
`%APPDATA%\AICon\routine-roots.txt`. **Copy the folder, share the routine** — that is the whole
distribution story. If two folders define the same `id`, the user's own copy wins so nobody's button
changes under them.

---

## 2. The two kinds — pick the right one

### `composed` — a list of existing AICon tool calls. **Prefer this.**

No compilation, no code-execution setting, works on every machine immediately. If the ~70 `aicon:`
tools can already do it, use this kind.

### `script` — C# in sibling `.cs` files.

For what no tool covers. Compiled in memory when first run. Requires the user to have enabled code
execution (`%APPDATA%\AICon\routines.json` → `"allowCodeExecution": true`); until then the routine is
saved but refuses to run, with a message saying exactly that.

> **Never put C# inside `routine.json`.** Source always goes in its own `.cs` file, passed through
> `save_routine`'s `files` argument.

**More than one file is fine — there is no size or complexity limit.** `script.files` is a list; every
file is compiled together into ONE assembly, so they can freely call each other. The only rule:
**`files[0]` is the entry point** (a bare body or a full `IAiconRoutine` class, same as a single-file
routine — see §5); every other file must be ordinary, complete C# (a helper class, a data model, extra
static methods) with nothing special about it. Put the entry file first:

```json
"script": { "files": ["Routine.cs", "Helpers.cs", "Models.cs"] }
```

A routine that has genuinely outgrown one file is a sign it should have been `Helpers.cs` all along,
not a reason to avoid `script` — split it.

---

## 3. `routine.json`

```json
{
  "schemaVersion": 1,
  "id": "level-sheet-set",
  "name": "Level Sheet Set",
  "description": "Plan + sheet + room tags for one level.",
  "kind": "composed",
  "ribbon": { "panel": "Routines", "buttonText": "Level\nSheet" },
  "inputs": [
    { "name": "level", "label": "Level", "type": "string", "required": true }
  ],
  "readOnly": false,
  "destructive": false,
  "steps": [ /* … */ ]
}
```

| Field | Notes |
|---|---|
| `schemaVersion` | Always `1`. AICon refuses a version it does not know rather than guessing. |
| `id` | lowercase-kebab, 2–64 chars. **This is the identity** — saving the same id again updates in place. |
| `kind` | `composed` or `script`. |
| `ribbon` | Omit entirely if it should only appear in the Routines list. |
| `readOnly` | `true` if it never changes the model. Tells the AI it is safe. |
| `destructive` | `true` if it deletes/overwrites. The user gets a warning in the dialog before it runs. |

### Input types

`string` · `number` · `integer` · `boolean` · `enum` (needs `options`, or `source` — see below) ·
`stringArray` (one per line) · `elementId` (renders a **Pick…** button so the user clicks the element
in the model).

**`enum` choices from the live model, not a fixed list.** A plain `enum` needs `options` written once
at author time — fine for choices that never change, wrong for anything like "pick a level": the
routine would keep offering whatever levels existed the day it was saved. Set `source` instead of (or
as a fallback alongside) `options` and AICon reads the real choices from the open model when the form
appears:

```json
{ "name": "level", "label": "Level", "type": "enum", "source": "levels" }
```

Recognised sources: `levels`, `views` (printable views only), `sheets` (`"<number> - <name>"`),
`categories` (every category actually present in the model). `source` always wins over `options` when
both are given.

**Picking more than one.** Add `"multi": true` to any `enum` (with `options` or `source`) to get a
checkbox list instead of a combo box. The bound input becomes an **array** of the checked strings
instead of one string — reference it in a step exactly like `stringArray`:

```json
{ "name": "levels", "label": "Levels", "type": "enum", "source": "levels", "multi": true }
```

Both are additive — an existing routine using plain `enum` + `options` needs no changes.

Declare inputs once and you get **both** the user's form and this tool's MCP schema. There is no
second place to edit — never hand-write a dialog.

Keep inputs **lean**. Every input is a field the user must fill in and a property in the schema the AI
must read. Ask for what changes; hard-code what doesn't.

---

## 4. Composed routines: steps and placeholders

```json
"steps": [
  { "tool": "create_floor_plan",
    "args": { "level": "{{input.level}}", "name": "PLAN - {{input.level}}" },
    "saveResultAs": "plan" },

  { "tool": "create_sheet",
    "args": { "number": "{{input.sheetNumber}}" },
    "saveResultAs": "sheet" },

  { "tool": "place_view_on_sheet",
    "args": { "view_id": "{{steps.plan.id}}", "sheet_id": "{{steps.sheet.id}}" } },

  { "tool": "tag_elements",
    "args": { "view_id": "{{steps.plan.id}}", "category": "Rooms" },
    "continueOnError": true }
]
```

- `{{input.name}}` — a routine input.
- `{{steps.alias.field}}` — a value an earlier step returned (`saveResultAs` names it).
- When the **whole** string is one placeholder the value keeps its real type (an id stays a number).
  Inside a longer string it is substituted as text.
- `continueOnError: true` for a step that is genuinely optional. Default is **all-or-nothing**: any
  failure rolls the entire routine back.
- `run_code` may **not** appear as a step's `tool`. It is the one tool with a mandatory per-call
  confirmation gate (see §6), which a composed routine — meant to run unattended from a ribbon button —
  cannot answer. `save_routine` rejects a step naming it. Need arbitrary C#? Use a `script` routine
  instead; it is reviewed once, at save time, and then runs at full speed with no per-run gate.

**Atomicity:** every step runs inside one transaction group. The routine either fully happens or
fully doesn't, and the user needs exactly one Ctrl+Z.

---

## 5. Script routines: write the body, not the boilerplate

Write **just the statements**. AICon wraps them and puts these in scope:

| In scope | What it is |
|---|---|
| `uiapp` | `UIApplication` |
| `uidoc` | `UIDocument` |
| `doc` | `Document` |
| `input` | the routine's inputs — `input.Text("x")`, `.Number("x", 150)`, `.Int`, `.Bool`, `.TextList`, `.Element("x")` |

`return` any object; it is shown to the user and returned to the AI. A body with no `return` is fine.

```csharp
double maxMm = input.Number("maxThicknessMm", 150);
var hits = new List<int>();

foreach (Wall w in new FilteredElementCollector(doc)
             .OfCategory(BuiltInCategory.OST_Walls)
             .WhereElementIsNotElementType().Cast<Wall>())
    if (w.Width * 304.8 < maxMm) hits.Add(w.Id.IntegerValue);

return new Dictionary<string, object> { { "count", hits.Count }, { "ids", hits.Take(50).ToList() } };
```

Already `using`-ed for you: `System`, `System.Collections(.Generic)`, `System.Linq`,
`Autodesk.Revit.DB` (+ `.Architecture`, `.Structure`), `Autodesk.Revit.UI`, `AICon.Routines`.

**Transactions:** don't open one. AICon wraps a non-`readOnly` routine in a transaction already. A
`readOnly: true` routine gets none — so don't write in it.

If you need full control, write a complete class implementing `IAiconRoutine` instead; AICon detects
that and compiles it as-is.

### Compile errors come back with YOUR line numbers

`save_routine` compiles before it saves. If it fails, **nothing is written** and you get
`{id, message, line, column}` per error — and line 1 is line 1 of the source *you* sent, not of the
generated wrapper. Fix and resend.

### Generics are fine here

`List<T>`, `Dictionary<K,V>`, LINQ — all normal. (There is folklore that angle brackets break in
AICon; it was tested and is **not true**, in `run_code` either. Write ordinary C#.)

### `run_code` (not a routine — the separate escape-hatch tool) now requires confirmation

Unlike a script routine (reviewed once, at save time), `run_code` compiles and runs whatever C# it is
given immediately, with no prior review. Every call now needs `"confirmed": true` in its arguments; a
call without it does not compile or execute anything — it just echoes the code back so it can be
reviewed first. Call `run_code` again with the same arguments plus `"confirmed": true` to actually run
it. This does not affect routines of either kind at all — see the step restriction above.

### One platform limit, stated plainly

On Revit 2023/2024 (.NET Framework 4.8) a compiled assembly **cannot be unloaded** — collectible
AssemblyLoadContext, which would allow that, is .NET Core only. Saving a routine and editing an
existing one **both** take effect on the very next `run_routine` call, no restart needed: the host
caches a routine's compiled Type keyed by its own source hash, so editing the `.cs` file produces a
new hash, a fresh compile, and a fresh Type; the old assembly simply stops being used (it stays
resident in memory for the rest of the session — a real but small cost, not a correctness problem).
The one thing that still needs a Revit restart is a **brand-new** routine's own ribbon button, because
Revit only builds ribbon panels at startup — a separate, genuine Revit API limit unrelated to
compilation.

---

## 6. The tools

| Tool | Use |
|---|---|
| `get_authoring_guide` | This document. Read before authoring. |
| `list_routines` | What already exists — check before building something twice. |
| `run_routine` | `{ id, inputs }`. One undoable transaction. |
| `save_routine` | `{ routine_json, files?, target_root? }`. Same `id` updates in place. |

`save_routine` example for a script routine:

```json
{
  "routine_json": "{\"schemaVersion\":1,\"id\":\"thin-wall-audit\", … ,\"script\":{\"files\":[\"Routine.cs\"]}}",
  "files": { "Routine.cs": "double maxMm = input.Number(\"maxThicknessMm\", 150); …" },
  "target_root": "\\\\server\\bim\\aicon-routines"
}
```

`target_root` is how a routine goes straight to the team share instead of one machine.

---

## 7. Rules worth following

1. **Composed unless you must script.** It works everywhere, for everyone, with no setting to enable.
2. **Check `list_routines` first.** Extending an existing routine beats a near-duplicate.
3. **Be honest with `readOnly` / `destructive`.** They drive real warnings the user relies on.
4. **Say what it does in `description`.** It is the tooltip, and it is what tells you later whether
   this is the routine you want.
5. **Test before saving a button.** Run the steps once as ordinary tool calls; save when they work.
6. **Do not ask the user about routine.json.** They should never have to know the manifest format —
   that is what this guide is for.
