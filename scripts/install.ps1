param(
    [string]$Source = "",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$installRoot = Join-Path $env:LOCALAPPDATA "GhostScreen\app"
$shortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\GhostScreen Tray.lnk"

if ($Uninstall) {
    Get-Process GhostScreen.Tray -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $installRoot) {
        Remove-Item -Recurse -Force $installRoot
    }
    if (Test-Path $shortcutPath) {
        Remove-Item -Force $shortcutPath
    }
    Write-Host "GhostScreen removed from $installRoot."
    Write-Host "MCP client configs, journal, and web profiles were left untouched."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Source)) {
    $artifacts = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts"
    $candidate = Get-ChildItem $artifacts -Directory -Filter "GhostScreen-*" |
        Where-Object { Test-Path (Join-Path $_.FullName "mcp\GhostScreen.Mcp.exe") } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $candidate) {
        throw "No extracted package found under artifacts/. Run scripts/publish.ps1 first."
    }

    $Source = $candidate.FullName
}

$sourcePath = (Resolve-Path $Source).Path
foreach ($required in @(
        "mcp\GhostScreen.Mcp.exe",
        "cli\GhostScreen.Cli.exe",
        "tray\GhostScreen.Tray.exe")) {
    if (-not (Test-Path (Join-Path $sourcePath $required))) {
        throw "Package is missing $required (source: $sourcePath)"
    }
}

Get-Process GhostScreen.Tray -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $installRoot) {
    Remove-Item -Recurse -Force $installRoot
}

New-Item -ItemType Directory -Path $installRoot | Out-Null
Copy-Item (Join-Path $sourcePath "*") $installRoot -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installRoot "tray\GhostScreen.Tray.exe"
$shortcut.WorkingDirectory = Join-Path $installRoot "tray"
$shortcut.Description = "GhostScreen tray (status / panic)"
$shortcut.Save()

Write-Host "Installed to $installRoot"
Write-Host "Start Menu shortcut: $shortcutPath"
Write-Host "MCP command: $(Join-Path $installRoot 'mcp\GhostScreen.Mcp.exe')"
Write-Host "Uninstall: powershell -ExecutionPolicy Bypass -File scripts/install.ps1 -Uninstall"
