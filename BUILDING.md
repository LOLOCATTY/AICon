# Building AICon — for a developer receiving this source

Read `HANDOFF-PROMPT.md` first: it explains what AICon is, how it is structured, and the Revit API
rules this codebase learned the hard way. This file is only about getting it to compile and deploy.

---

## 1. Prerequisites

| Need | Why |
|---|---|
| **Windows 10/11** | Revit is Windows-only. |
| **Autodesk Revit 2024**, installed at `C:\Program Files\Autodesk\Revit 2024\` | `plugin/AICon.csproj` references `RevitAPI.dll` and `RevitAPIUI.dll` from that exact path. See §2 if yours is elsewhere. |
| **.NET SDK 8 or newer** | Builds the net48 add-in and publishes the net10 MCP server. |
| Visual Studio 2022 (optional) | `AICon.sln` opens all three projects. The command line is enough. |

The add-in itself targets **.NET Framework 4.8** — that is what Revit 2023/2024 host. Do **not**
retarget it without reading §8 of `HANDOFF-PROMPT.md` first.

---

## 2. If your Revit is installed somewhere else

Edit the two `HintPath`s in `plugin/AICon.csproj`:

```xml
<Reference Include="RevitAPI">
  <HintPath>C:\Program Files\Autodesk\Revit 2024\RevitAPI.dll</HintPath>
  <Private>false</Private>
</Reference>
```

`<Private>false</Private>` matters — those DLLs must **not** be copied to the output; Revit supplies
them at run time and shipping a copy causes assembly conflicts.

Revit 2023 also works (the code deliberately avoids 2024-only APIs), but 2025+ does **not** — see
the deferred `ElementId` / .NET 8 note in `HANDOFF-PROMPT.md` §8.

---

## 3. Build

```powershell
# the Revit add-in
dotnet build plugin\AICon.csproj -c Release

# the MCP server (Claude Desktop talks to this) and the console test host
dotnet build server\AIConServer.csproj -c Release
dotnet build agent\AIConAgent.csproj  -c Release
```

**`shared\ToolRegistry.cs` is compiled by all three projects.** If you touch it, build all three or
you will only find out later that the server no longer compiles.

---

## 4. Package a release

```powershell
scripts\build-package.ps1
```

Builds the plugin, publishes the server self-contained (so end users need no .NET install),
regenerates the icons, and produces `dist\AICon-<version>.zip` containing a one-click `Setup.bat`.

The version lives in **one** place: `<Version>` in `plugin\AICon.csproj`. `App.cs` reads it back off
the built assembly and the packaging scripts parse the same csproj, so bumping it there is enough.

---

## 5. Deploy for debugging

`scripts\install.ps1` (run by `Setup.bat`) installs to:

```
%APPDATA%\Autodesk\Revit\Addins\2024\AICon.addin      ← manifest
%APPDATA%\Autodesk\Revit\Addins\2024\AICon\           ← AICon.dll + deps + icons + AUTHORING.md
%LOCALAPPDATA%\AICon\server\AIConServer.exe           ← MCP server
```

To iterate quickly: build, close Revit, copy `plugin\bin\Release\*.dll` over the installed folder,
reopen Revit. Revit locks the DLL while running, which is why it must be closed first.

**Attach a debugger** to `Revit.exe` after it starts; the add-in loads at Revit startup.

---

## 6. Run-time configuration (never in the repo)

All of it lives in `%APPDATA%\AICon\` and is deliberately **not** version-controlled — it can contain
API keys:

| File | What |
|---|---|
| `aiconagent.json` | Chat panel provider + API keys (Gemini / DeepSeek / local Ollama) |
| `ar400ai.json` | Optional override for the AI decision layer |
| `ar400profiles.json` | AR400 per-package automation settings |
| `routines.json` | `allowCodeExecution` — the routine scripting switch |
| `routines\` | Saved routines (one folder each) |
| `bridge.log`, `decisions.log` | **Read these first when something misbehaves.** |

---

## 7. Gotchas that will cost you a day if you skip them

1. Revit locks `AICon.dll` while running — close Revit before copying a new build.
2. Newly transferred DLLs are blocked by Windows (Mark of the Web) and Revit silently refuses to load
   them — `Unblock-File` them (the installer already does).
3. `shared\ToolRegistry.cs` feeds three projects (see §3).
4. Anything created inside an open Revit transaction is invisible to a `FilteredElementCollector`
   until `doc.Regenerate()`.
5. WPF is written **in code, no XAML**, and Revit's API has types that collide with WPF names
   (`Grid`, `Color`, `ComboBox`, `TextBox`) — alias them.

The full list, with the reasoning behind each, is §7 of `HANDOFF-PROMPT.md`. It is worth ten minutes.
