# Produces dist\AICon-source-<version>.zip — everything a developer needs to build and modify AICon,
# and nothing else: no build output, no 1.8 GB of past releases, and no configuration that could
# carry an API key (all of that lives in %APPDATA%\AICon at run time, never in the repo).
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot

# plugin\AICon.csproj is the single source of the version (App.cs reads it back off the assembly,
# and build-package.ps1 reads the same place) — so there is nothing to keep in sync by hand.
$csproj = Get-Content "$root\plugin\AICon.csproj" -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw "Could not find <Version> in plugin\AICon.csproj" }
$version = $Matches[1]

$stage = "$root\dist\AICon-source-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

# Source folders, minus build output.
foreach ($dir in @("plugin", "shared", "server", "agent", "scripts", "schemas", "examples", "assets")) {
    if (-not (Test-Path "$root\$dir")) { continue }
    Copy-Item "$root\$dir" $stage -Recurse -Force
    foreach ($junk in @("bin", "obj", "icons")) {
        Get-ChildItem "$stage\$dir" -Recurse -Directory -Filter $junk -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Root-level files a developer needs.
foreach ($file in @("AICon.sln", ".gitignore", "README.md", "BUILDING.md", "HANDOFF-PROMPT.md", "AUTHORING.md")) {
    if (Test-Path "$root\$file") { Copy-Item "$root\$file" $stage -Force }
}
if (Test-Path "$root\plugin\AICon.addin") { Copy-Item "$root\plugin\AICon.addin" $stage -Force }

# Refuse to ship a zip containing anything that looks like a credential. Cheap insurance: this
# archive is going to another person.
$secretHits = Get-ChildItem $stage -Recurse -File -Include *.cs,*.json,*.ps1,*.md,*.csproj,*.bat,*.addin |
    Select-String -Pattern 'sk-[A-Za-z0-9]{16,}', 'AIza[A-Za-z0-9_-]{20,}', 'AQ\.[A-Za-z0-9]{20,}' -ErrorAction SilentlyContinue
if ($secretHits) {
    $secretHits | ForEach-Object { Write-Host "  SECRET? $($_.Path):$($_.LineNumber)" -ForegroundColor Red }
    Remove-Item $stage -Recurse -Force
    throw "Possible credentials found - source zip NOT created. Remove them and re-run."
}

$zip = "$root\dist\AICon-source-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Remove-Item $stage -Recurse -Force

Write-Host ""
Write-Host "Source package ready: $zip" -ForegroundColor Green
Get-Item $zip | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 2) } }
