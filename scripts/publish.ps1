param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    $productInfo = Get-Content (Join-Path $root "src/GhostScreen.Core/ProductInfo.cs") -Raw
    $match = [regex]::Match($productInfo, 'Version\s*=\s*"([^"]+)"')
    if (-not $match.Success) {
        throw "Cannot read ProductInfo.Version."
    }
    $Version = $match.Groups[1].Value
}

$outRoot = Join-Path $root "artifacts"
$out = Join-Path $outRoot "GhostScreen-$Version"

if (Test-Path $out) {
    Remove-Item -Recurse -Force $out
}

New-Item -ItemType Directory -Path $out | Out-Null
New-Item -ItemType Directory -Path (Join-Path $out "docs") | Out-Null

Write-Host "Publishing MCP..."
dotnet publish (Join-Path $root "src/GhostScreen.Mcp/GhostScreen.Mcp.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $out "mcp")

Write-Host "Publishing CLI..."
dotnet publish (Join-Path $root "src/GhostScreen.Cli/GhostScreen.Cli.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $out "cli")

Write-Host "Publishing tray..."
dotnet publish (Join-Path $root "src/GhostScreen.Tray/GhostScreen.Tray.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $out "tray")

Copy-Item (Join-Path $root "README.md") (Join-Path $out "README.md")
Copy-Item (Join-Path $root "CHANGELOG.md") (Join-Path $out "CHANGELOG.md")
Copy-Item (Join-Path $root "LICENSE") (Join-Path $out "LICENSE")

# Match doc names by ASCII prefix so the script never depends on how the
# host decodes non-ASCII file names.
$docRoot = Join-Path $root "doc"
$mcpDoc = Get-ChildItem $docRoot -Filter "08-*.md" | Select-Object -First 1
$releaseDoc = Get-ChildItem $docRoot -Filter "17-*.md" | Select-Object -First 1
if ($null -eq $mcpDoc -or $null -eq $releaseDoc) {
    throw "Missing doc/08 or doc/17."
}

Copy-Item $mcpDoc.FullName (Join-Path $out "docs/MCP-CLIENTS.md")
Copy-Item $releaseDoc.FullName (Join-Path $out "docs/RELEASE-NOTES.md")
Set-Content -Path (Join-Path $out "VERSION") -Value $Version

$zip = Join-Path $outRoot "GhostScreen-$Version-win-x64.zip"
if (Test-Path $zip) {
    Remove-Item $zip -Force
}

Write-Host "Compressing..."
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -Force

$hash = Get-FileHash -Path $zip -Algorithm SHA256
Set-Content -Path "$zip.sha256" -Value "$($hash.Hash)  $(Split-Path -Leaf $zip)"

$size = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Created $zip ($size MB)"
Get-Content "$zip.sha256"
