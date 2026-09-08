# ============================================================
#  AICon setup — AI connection for Autodesk Revit
#  Double-click Setup.bat to run this.
# ============================================================
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host ""
Write-Host "  =========================================" -ForegroundColor Cyan
Write-Host "        AICon Setup - Revit + Claude AI    " -ForegroundColor Cyan
Write-Host "  =========================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path "$root\AICon.addin") -or -not (Test-Path "$root\server\AIConServer.exe")) {
    Write-Host "  ERROR: files missing. Extract the FULL AICon zip first, then run Setup.bat from the extracted folder." -ForegroundColor Red
    exit 1
}

# --- 0) Unblock everything (files downloaded/copied from another PC carry a 'blocked'
#        mark that makes Revit silently refuse to load the add-in DLL) ---
try {
    Get-ChildItem $root -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    Write-Host "  [0/5] Files unblocked (Windows download protection removed)." -ForegroundColor Green
} catch { }

# --- If Claude Desktop is running, it will overwrite our config on exit. Close it first. ---
$claude = Get-Process "Claude" -ErrorAction SilentlyContinue
if ($claude) {
    Write-Host "  Claude Desktop is running and must be closed to complete setup." -ForegroundColor Yellow
    $answer = Read-Host "  Close Claude Desktop now? (Y/N)"
    if ($answer -match '^[Yy]') {
        $claude | Stop-Process -Force
        Start-Sleep -Seconds 2
        Write-Host "  Claude Desktop closed." -ForegroundColor Green
    } else {
        Write-Host "  WARNING: setup will continue, but Claude Desktop may undo the configuration when it exits." -ForegroundColor Yellow
        Write-Host "  If AICon does not appear later, close Claude Desktop fully and run Setup.bat again."
    }
}

$revitRunning = [bool](Get-Process "Revit" -ErrorAction SilentlyContinue)

# --- 1) Revit add-in: install into every supported Revit year folder found ---
# Four builds ship in this package (see plugin\AICon.csproj's <TargetFrameworks> and RevitApiYear):
#   net48                -> Revit 2022-2024 (.NET Framework)
#   net8.0-windows       -> Revit 2025      (.NET 8)
#   net10.0-windows      -> Revit 2026      (.NET 10 — Revit did NOT stay on .NET 8 past 2025; verified
#                                             by a compiler error when 2026's RevitAPI.dll was first
#                                             referenced against net8.0-windows, not assumed from 2025)
#   net10.0-windows-2027 -> Revit 2027      (same .NET 10 runtime as 2026, but a DIFFERENT RevitAPI.dll
#                                             — 2026 and 2027 share a TFM, never a binary)
# One year per entry, not open-ended ranges: each of these was compiled AND (net48, net8.0-windows)
# runtime-verified against that SPECIFIC year's own RevitAPI.dll. A newer Revit year is never assumed
# compatible just because it is close by — it is reported as "found but not yet supported" below rather
# than silently installed with unverified code.
$addinsRoot = "$env:APPDATA\Autodesk\Revit\Addins"
$installedYears = @()
$lockedYears = @()
$foundYears = @()
$unsupportedYears = @()
if (Test-Path $addinsRoot) {
    foreach ($dir in Get-ChildItem $addinsRoot -Directory) {
        if ($dir.Name -match '^\d{4}$') {
            $foundYears += $dir.Name
            $year = [int]$dir.Name
            # 2021 lacks the APIs AICon uses for floors/ceilings/PDF; years outside this list have not
            # been built/verified against yet.
            $sourceBuild = $null
            if ($year -ge 2022 -and $year -le 2024) { $sourceBuild = "net48" }
            elseif ($year -eq 2025) { $sourceBuild = "net8.0-windows" }
            elseif ($year -eq 2026) { $sourceBuild = "net10.0-windows" }
            elseif ($year -eq 2027) { $sourceBuild = "net10.0-windows-2027" }

            if ($null -eq $sourceBuild) {
                $unsupportedYears += $dir.Name
                continue
            }

            try {
                $destPlugin = "$($dir.FullName)\AICon"
                # Wipe out whatever a PREVIOUS install left here before copying this version's files —
                # do not merge on top of it. A real bug hit by a colleague on Revit 2026: an old build
                # once copied a System.Text.Json.dll next to AICon.dll for this target (later removed
                # as redundant, see AICon.csproj's net8.0-windows/net10.0-windows PropertyGroup
                # comment); .NET probes a plugin's OWN folder for a same-named assembly BEFORE falling
                # back to the shared framework, so that leftover DLL (an older, incompatible version)
                # shadowed the correct in-box one and crashed every JSON-touching code path with a
                # FileLoadException — "Could not switch AICon agent" and every MCP tool call alike.
                # Reinstalling never cleared it because nothing ever deleted files the new package
                # doesn't ship. Now it does, every time.
                if (Test-Path $destPlugin) {
                    Get-ChildItem $destPlugin -Recurse -File | ForEach-Object {
                        try { Remove-Item $_.FullName -Force -ErrorAction Stop }
                        catch {
                            # Locked by a running Revit (the currently-loaded DLL): rename aside so the
                            # fresh copy below can still land under the real name. Cleared next install
                            # once Revit is closed.
                            try { Move-Item $_.FullName "$($_.FullName).old" -Force -ErrorAction SilentlyContinue } catch { }
                        }
                    }
                }
                New-Item -ItemType Directory -Force "$destPlugin\icons" | Out-Null
                # Copy AICon.dll AND its runtime dependencies for THIS year's build specifically.
                foreach ($dll in Get-ChildItem "$root\AICon\$sourceBuild\*.dll") {
                    $dest = Join-Path $destPlugin $dll.Name
                    try {
                        Copy-Item $dll.FullName $dest -Force
                    } catch {
                        # DLL locked by a running Revit: rename it aside, then copy the new one
                        if (Test-Path $dest) { Move-Item $dest "$dest.old" -Force }
                        Copy-Item $dll.FullName $dest -Force
                        if ($lockedYears -notcontains $dir.Name) { $lockedYears += $dir.Name }
                    }
                }
                Copy-Item "$root\AICon\icons\*.png" "$($dir.FullName)\AICon\icons\" -Force
                # The routine authoring guide is read from beside the DLL by get_authoring_guide.
                if (Test-Path "$root\AICon\AUTHORING.md") {
                    Copy-Item "$root\AICon\AUTHORING.md" "$($dir.FullName)\AICon\" -Force
                }
                Copy-Item "$root\AICon.addin" "$($dir.FullName)\" -Force
                # unblock the installed files too, and clear the old pre-AICon prototype
                Get-ChildItem "$($dir.FullName)\AICon" -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
                Unblock-File "$($dir.FullName)\AICon.addin" -ErrorAction SilentlyContinue
                Remove-Item "$($dir.FullName)\RevitClaudePlugin.addin" -Force -ErrorAction SilentlyContinue
                $installedYears += "$($dir.Name) ($sourceBuild)"
            } catch {
                Write-Host "  WARNING: could not install for Revit $($dir.Name): $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    }
}
if ($installedYears.Count -eq 0) {
    if ($foundYears.Count -gt 0) {
        Write-Host "  ERROR: found Revit $($foundYears -join ', ') but none of them are supported by this AICon build yet." -ForegroundColor Red
    } else {
        Write-Host "  ERROR: no Revit installation found (no folders under $addinsRoot)." -ForegroundColor Red
        Write-Host "  Start Revit once, close it, then run Setup.bat again."
    }
    exit 1
}
Write-Host "  [1/5] Revit add-in installed for: $($installedYears -join ', ')" -ForegroundColor Green
if ($unsupportedYears.Count -gt 0) {
    Write-Host "        Found Revit $($unsupportedYears -join ', ') too, but this AICon build does not support $(if ($unsupportedYears.Count -eq 1) {'it'} else {'them'}) yet." -ForegroundColor Yellow
}

# --- 2) AICon server ---
$serverDir = "$env:LOCALAPPDATA\AICon\server"
New-Item -ItemType Directory -Force $serverDir | Out-Null
try {
    Copy-Item "$root\server\AIConServer.exe" $serverDir -Force
} catch {
    # exe locked by a running server: rename it aside, then copy the new one
    Move-Item "$serverDir\AIConServer.exe" "$serverDir\AIConServer.exe.old" -Force
    Copy-Item "$root\server\AIConServer.exe" $serverDir -Force
}
Remove-Item "$serverDir\AIConServer.exe.old" -Force -ErrorAction SilentlyContinue
Unblock-File "$serverDir\AIConServer.exe" -ErrorAction SilentlyContinue
Write-Host "  [2/5] AICon server installed." -ForegroundColor Green

# --- 3) Routine settings: enable script Routines by default, but only on a FRESH machine ---
# %APPDATA%\AICon\routines.json (AiconRoutineSettings.cs) is a per-user SETTINGS file — a completely
# different location from the plugin folders step 1 wipes-and-recopies every install, so seeding it
# here is a one-time default, never something a later reinstall/update stomps back on. Written ONLY
# if the file does not already exist, so a colleague's own later choice (e.g. turning this back off)
# is never silently overwritten. allowRunCode is left unset on purpose — it already defaults to true
# in code, so there is nothing to seed there.
$settingsDir = "$env:APPDATA\AICon"
$settingsPath = "$settingsDir\routines.json"
if (-not (Test-Path $settingsPath)) {
    New-Item -ItemType Directory -Force $settingsDir | Out-Null
    $defaultSettings = @{ allowCodeExecution = $true } | ConvertTo-Json
    # Same BOM-free write as the Claude Desktop config below — a BOM here would break
    # System.Text.Json the same way it breaks Claude Desktop's parser.
    [IO.File]::WriteAllText($settingsPath, $defaultSettings, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  [3/5] Script Routines enabled by default (routines.json created)." -ForegroundColor Green
} else {
    Write-Host "  [3/5] Routine settings already exist - left untouched." -ForegroundColor Green
}

# --- 4) Claude Desktop configuration ---
$configDir = "$env:APPDATA\Claude"
$configPath = "$configDir\claude_desktop_config.json"
$exe = "$serverDir\AIConServer.exe"
if (-not (Test-Path $configDir)) {
    Write-Host "  [4/5] Claude Desktop is not installed yet." -ForegroundColor Yellow
    Write-Host "        Install it from https://claude.ai/download then run Setup.bat again."
} else {
    if (Test-Path $configPath) {
        try { $config = Get-Content $configPath -Raw | ConvertFrom-Json }
        catch { $config = [pscustomobject]@{} }
    } else {
        $config = [pscustomobject]@{}
    }
    if (-not $config.PSObject.Properties["mcpServers"]) {
        $config | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{})
    }
    if ($config.mcpServers.PSObject.Properties["revit"]) {
        $config.mcpServers.PSObject.Properties.Remove("revit")   # stale pre-AICon entry
    }
    if ($config.mcpServers.PSObject.Properties["aicon"]) {
        $config.mcpServers.aicon.command = $exe
    } else {
        $config.mcpServers | Add-Member -NotePropertyName aicon -NotePropertyValue ([pscustomobject]@{ command = $exe })
    }
    # IMPORTANT: write WITHOUT BOM — PowerShell 5.1's `Out-File -Encoding utf8` adds a BOM,
    # and Claude Desktop's JSON parser rejects the file with "Unexpected token".
    $json = $config | ConvertTo-Json -Depth 32
    [IO.File]::WriteAllText($configPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  [4/5] Claude Desktop connected to AICon." -ForegroundColor Green
}

# --- 5) ChatGPT Desktop / Codex CLI configuration (shared ~/.codex/config.toml) ---
# ChatGPT Desktop, Codex CLI, and the Codex IDE extension all read LOCAL stdio MCP servers from one
# shared file, %USERPROFILE%\.codex\config.toml — same idea as the Claude Desktop config above, just
# TOML instead of JSON. This is a DIFFERENT thing from ChatGPT's web/cloud "Connectors" (those need a
# remote HTTPS server and are out of scope for AICon's local-only design) — this file only configures
# the desktop app, which — like Claude Desktop — runs locally and can launch a local stdio process.
$codexConfigDir = "$env:USERPROFILE\.codex"
$codexConfigPath = "$codexConfigDir\config.toml"
if (-not (Test-Path $codexConfigDir)) {
    Write-Host "  [5/5] ChatGPT Desktop / Codex is not installed yet." -ForegroundColor Yellow
    Write-Host "        Install it from https://chatgpt.com/download then run Setup.bat again."
} else {
    $existingToml = if (Test-Path $codexConfigPath) { Get-Content $codexConfigPath -Raw } else { "" }
    if ($existingToml -match '(?m)^\s*\[mcp_servers\.aicon\]\s*$') {
        Write-Host "  [5/5] ChatGPT Desktop / Codex already configured for AICon - left untouched." -ForegroundColor Green
    } else {
        # A TOML LITERAL string ('...') takes a Windows path exactly as written, backslashes and all —
        # no escaping needed, unlike a TOML/JSON basic ("...") string. Appended, never replacing
        # anything already in the file, so Codex's own settings and any other [mcp_servers.*] entries
        # survive untouched.
        $aiconToml = "[mcp_servers.aicon]`ncommand = '$exe'`n"
        $newToml = if ($existingToml.Trim().Length -gt 0) { $existingToml.TrimEnd() + "`n`n" + $aiconToml } else { $aiconToml }
        # Same no-BOM write as everywhere else in this script (see the Claude Desktop step above) —
        # no reason to trust a TOML parser to be more forgiving of a BOM than Claude Desktop's was.
        [IO.File]::WriteAllText($codexConfigPath, $newToml, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "  [5/5] ChatGPT Desktop / Codex connected to AICon." -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "  Setup complete!" -ForegroundColor Cyan
if ($revitRunning -or $lockedYears.Count -gt 0) {
    # The add-in's display name is always "AICon" (AICon.addin's <Name>) — no need to re-derive it from
    # the manifest file every run. The old version of this line did that AND hard-coded "AICon" too,
    # printing "load AICon AICon".
    Write-Host "   * Revit is running: close and reopen Revit to load the new AICon version." -ForegroundColor Yellow
} else {
    Write-Host "   1. Start Revit -> click 'Always Load' when asked about AICon."
}
Write-Host "   2. Open Claude Desktop, or ChatGPT Desktop (Codex) - both configured automatically."
Write-Host "   3. Open a Revit project and chat: 'What's in my Revit model?'"
Write-Host ""
