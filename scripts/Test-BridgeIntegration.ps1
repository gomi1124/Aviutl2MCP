[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
& (Join-Path $PSScriptRoot "Invoke-TestWithDebugReport.ps1") `
    -SuiteName "bridge-integration" `
    -TestProject (Join-Path $repositoryRoot "tests\AviUtl2MCP.BridgeIntegrationTests\AviUtl2MCP.BridgeIntegrationTests.csproj") `
    -CheckName @(
        "pipe.late-connect",
        "pipe.instance-selection",
        "diagnostics.pipe-recovery",
        "ipc.mutation-at-most-once"
    ) `
    -Configuration $Configuration `
    -NoBuild:$NoBuild
