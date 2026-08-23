# Builds AICon and produces the distributable package: dist\AICon-<version>.zip
# The server is published SELF-CONTAINED so end users do not need .NET installed.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
# Read from plugin\AICon.csproj instead of a hand-typed literal, so this script can't silently drift
# from the version actually built into AICon.dll.
$csprojContent = Get-Content "$root\plugin\AICon.csproj" -Raw
if ($csprojContent -notmatch '<Version>([^<]+)</Version>') { throw "Could not find <Version> in plugin\AICon.csproj" }
$version = $Matches[1]
Write-Host "Packaging version $version"

Write-Host "Building plugin..."
dotnet build "$root\plugin\AICon.csproj" -c Release -v q
if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }

Write-Host "Publishing server (self-contained win-x64)..."
dotnet publish "$root\server\AIConServer.csproj" -c Release -r win-x64 --self-contained true `
    /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:DebugType=none `
    -o "$root\dist\server" -v q
if ($LASTEXITCODE -ne 0) { throw "server publish failed" }

Write-Host "Generating icons..."
& "$root\assets\make-icons.ps1" | Out-Null

Write-Host "Staging package..."
$stage = "$root\dist\AICon-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\AICon\net48", "$stage\AICon\net8.0-windows", "$stage\AICon\net10.0-windows", "$stage\AICon\icons", "$stage\server" | Out-Null
# Three builds, one package: net48 (Revit 2023/2024), net8.0-windows (Revit 2025), net10.0-windows
# (Revit 2026 — a further .NET bump past 2025's net8.0-windows, verified by compiler error, not
# assumed; see plugin\AICon.csproj's <TargetFrameworks> comment). See plugin\ElementIdCompat.cs /
# Json.cs for why the source itself needed a compatibility split. install.ps1 picks the matching
# folder per detected Revit year. Each folder gets AICon.dll AND its runtime dependencies — net48 needs
# the full System.Text.Json/Roslyn dependency chain (nothing is built in); net8.0-windows/net10.0-windows
# only need Roslyn's own two DLLs (CopyLocalLockFileAssemblies=true in the csproj — everything else
# Roslyn needs is already in the shared framework).
Copy-Item "$root\plugin\bin\Release\net48\*.dll" "$stage\AICon\net48\" -Force
Copy-Item "$root\plugin\bin\Release\net8.0-windows\*.dll" "$stage\AICon\net8.0-windows\" -Force
Copy-Item "$root\plugin\bin\Release\net10.0-windows\*.dll" "$stage\AICon\net10.0-windows\" -Force
Copy-Item "$root\assets\icons\*.png" "$stage\AICon\icons\" -Force
# The routine authoring guide is served verbatim by get_authoring_guide, so it must sit next to the DLL.
Copy-Item "$root\AUTHORING.md" "$stage\AICon\" -Force
# Example routines, so a new user has something to look at (and copy) on day one.
New-Item -ItemType Directory -Force "$stage\example-routines" | Out-Null
Copy-Item "$root\examples\routines\*" "$stage\example-routines\" -Recurse -Force
Copy-Item "$root\schemas\routine.schema.json" "$stage\example-routines\" -Force
Copy-Item "$root\plugin\AICon.addin" "$stage\" -Force
Copy-Item "$root\dist\server\AIConServer.exe" "$stage\server\" -Force
Copy-Item "$PSScriptRoot\install.ps1" "$stage\" -Force
Copy-Item "$PSScriptRoot\Setup.bat" "$stage\" -Force
Copy-Item "$root\README.md" "$stage\" -Force

$zip = "$root\dist\AICon-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Write-Host "Package ready: $zip"
Get-Item $zip | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
















