[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "AviUtl2MCP"),
    [string]$BridgePluginDirectory = (Join-Path $env:ProgramData "aviutl2\Plugin\AviUtl2MCP"),
    [switch]$SkipBridgeUpdate,
    [string]$Repository = "gomi1124/Aviutl2MCP",
    [string]$ReleaseMetadataPath = "",
    [string]$ReleaseAssetDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $json = ($Value | ConvertTo-Json -Depth 8) + "`r`n"
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

$resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot)
New-Item -ItemType Directory -Path $resolvedInstallRoot -Force | Out-Null
foreach ($scriptName in @("Run-AviUtl2MCP.ps1", "Update-AviUtl2MCP.ps1")) {
    $sourcePath = Join-Path $PSScriptRoot $scriptName
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Installer component is missing: $sourcePath"
    }
    $destinationPath = Join-Path $resolvedInstallRoot $scriptName
    if (-not [string]::Equals(
            [IO.Path]::GetFullPath($sourcePath),
            [IO.Path]::GetFullPath($destinationPath),
            [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    }
}

$settings = [ordered]@{
    schemaVersion = "1.0"
    repository = $Repository
    bridgePluginDirectory = [IO.Path]::GetFullPath($BridgePluginDirectory)
    skipBridgeUpdate = [bool]$SkipBridgeUpdate
    updateIntervalHours = 6
}
Write-Utf8Json -Path (Join-Path $resolvedInstallRoot "state\settings.json") -Value $settings

$updaterArguments = @(
    "-NoProfile",
    "-NonInteractive",
    "-ExecutionPolicy", "Bypass",
    "-File", (Join-Path $resolvedInstallRoot "Update-AviUtl2MCP.ps1"),
    "-InstallRoot", $resolvedInstallRoot,
    "-Force"
)
if (-not [string]::IsNullOrWhiteSpace($ReleaseMetadataPath)) {
    $updaterArguments += @("-ReleaseMetadataPath", [IO.Path]::GetFullPath($ReleaseMetadataPath))
}
if (-not [string]::IsNullOrWhiteSpace($ReleaseAssetDirectory)) {
    $updaterArguments += @("-ReleaseAssetDirectory", [IO.Path]::GetFullPath($ReleaseAssetDirectory))
}
if ($SkipBridgeUpdate) {
    $updaterArguments += "-SkipBridgeUpdate"
}

$powershellPath = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
& $powershellPath @updaterArguments
if ($LASTEXITCODE -ne 0) {
    throw "AviUtl2MCP update failed with exit code $LASTEXITCODE."
}

$launcherPath = Join-Path $resolvedInstallRoot "Run-AviUtl2MCP.ps1"
$activeStatePath = Join-Path $resolvedInstallRoot "state\active-server.json"
if (-not (Test-Path -LiteralPath $activeStatePath -PathType Leaf)) {
    $updateStatePath = Join-Path $resolvedInstallRoot "state\update-state.json"
    $updateState = if (Test-Path -LiteralPath $updateStatePath -PathType Leaf) {
        Get-Content -LiteralPath $updateStatePath -Raw | ConvertFrom-Json
    }
    else {
        $null
    }
    if ($null -ne $updateState -and [string]$updateState.status -eq "bridge_pending") {
        Write-Warning "AviUtl2 is running. Close it and run this installer again to activate v$($updateState.availableVersion)."
        Write-Output "Staged AviUtl2MCP update: $($updateState.pendingBridgePath)"
        return
    }
    throw "The updater completed without activating a verified server."
}
Write-Output "Installed AviUtl2MCP launcher: $launcherPath"
Write-Output "Configure the MCP client command as: $powershellPath"
Write-Output "Configure its arguments as: -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$launcherPath`""
