[CmdletBinding()]
param(
    [switch]$SkipUpdateCheck,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ServerArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-ContainedPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    if ([IO.Path]::IsPathRooted($RelativePath)) {
        throw "The active server path must be relative to the install root."
    }
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $candidate = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    if (-not $candidate.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The active server path escaped the install root."
    }
    return $candidate
}

$installRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$activeStatePath = Join-Path $installRoot "state\active-server.json"
if (-not (Test-Path -LiteralPath $activeStatePath -PathType Leaf)) {
    throw "A verified AviUtl2MCP server is not active. Run Install-AviUtl2MCP.ps1 first."
}

$activeState = Get-Content -LiteralPath $activeStatePath -Raw | ConvertFrom-Json
if ($activeState.schemaVersion -ne "1.0" -or
    [string]::IsNullOrWhiteSpace([string]$activeState.serverPath)) {
    throw "The active AviUtl2MCP state is invalid: $activeStatePath"
}
$serverPath = Get-ContainedPath -Root $installRoot -RelativePath $activeState.serverPath
if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) {
    throw "The active AviUtl2MCP server is missing: $serverPath"
}

if (-not $SkipUpdateCheck) {
    $updaterPath = Join-Path $installRoot "Update-AviUtl2MCP.ps1"
    if (Test-Path -LiteralPath $updaterPath -PathType Leaf) {
        $powershellPath = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
        Start-Process `
            -FilePath $powershellPath `
            -ArgumentList @(
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy", "Bypass",
                "-File", $updaterPath,
                "-InstallRoot", $installRoot
            ) `
            -WindowStyle Hidden | Out-Null
    }
}

& $serverPath @ServerArguments
exit $LASTEXITCODE
