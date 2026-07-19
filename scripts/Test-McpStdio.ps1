[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
& (Join-Path $PSScriptRoot "Invoke-TestWithDebugReport.ps1") `
    -SuiteName "mcp-stdio" `
    -TestProject (Join-Path $repositoryRoot "tests\AviUtl2MCP.StdioTests\AviUtl2MCP.StdioTests.csproj") `
    -CheckName @(
        "stdio.offline-initialize",
        "mcp.catalog-snapshot",
        "stdio.stdout-purity"
    ) `
    -Configuration $Configuration `
    -NoBuild:$NoBuild
