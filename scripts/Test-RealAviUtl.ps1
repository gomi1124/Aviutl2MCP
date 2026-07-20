[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [switch]$Real,

    [string]$AviUtlPath = "C:\Program Files\AviUtl2\aviutl2.exe",
    [string]$DataPath = "C:\ProgramData\aviutl2",
    [string]$ProjectPath = "",
    [string]$BridgePackagePath = "",
    [string]$TemporaryRoot = "C:\tmp\AviUtl2MCP-real",
    [string]$Configuration = "Debug",
    [string]$TestFilter = "TestCategory=RealAviUtl2"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $Real.IsPresent) {
    throw "Real AviUtl2 tests require the explicit -Real switch."
}
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    throw "ProjectPath must be specified explicitly."
}
if ([string]::IsNullOrWhiteSpace($TestFilter)) {
    throw "TestFilter must not be empty."
}

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$resolvedAviUtlPath = (Resolve-Path -LiteralPath $AviUtlPath).Path
$resolvedDataPath = (Resolve-Path -LiteralPath $DataPath).Path
$resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$resolvedBridgePackagePath = if ([string]::IsNullOrWhiteSpace($BridgePackagePath)) {
    $null
}
else {
    (Resolve-Path -LiteralPath $BridgePackagePath).Path
}
$resolvedTemporaryRoot = [IO.Path]::GetFullPath($TemporaryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$allowedTemporaryRoot = [IO.Path]::GetFullPath("C:\tmp").TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not $resolvedTemporaryRoot.StartsWith(
        $allowedTemporaryRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "TemporaryRoot must be a dedicated child directory of C:\tmp."
}
if ([IO.Path]::GetExtension($resolvedProjectPath) -ine ".aup2") {
    throw "ProjectPath must identify an .aup2 fixture."
}
if ([IO.Path]::GetFileName($resolvedAviUtlPath) -ine "aviutl2.exe") {
    throw "AviUtlPath must identify aviutl2.exe."
}
if ($null -ne $resolvedBridgePackagePath -and
    -not $resolvedBridgePackagePath.EndsWith(".au2pkg.zip", [StringComparison]::OrdinalIgnoreCase)) {
    throw "BridgePackagePath must identify an .au2pkg.zip file."
}

New-Item -ItemType Directory -Path $resolvedTemporaryRoot -Force | Out-Null
$sourceHashBefore = (Get-FileHash -LiteralPath $resolvedProjectPath -Algorithm SHA256).Hash
$cmakePath = "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$nativeBridgePath = Join-Path $repositoryRoot "build\native\artifacts\$Configuration\AviUtl2MCP.Bridge.aux2"
$testProjectPath = Join-Path $repositoryRoot "tests\AviUtl2MCP.RealAviUtlTests\AviUtl2MCP.RealAviUtlTests.csproj"
$reportRoot = Join-Path $repositoryRoot "artifacts\real-e2e"
$existingReportPaths = @{}
if (Test-Path -LiteralPath $reportRoot) {
    Get-ChildItem -LiteralPath $reportRoot -Filter "debug-report.json" -File -Recurse |
        ForEach-Object { $existingReportPaths[$_.FullName] = $true }
}
$savedEnvironment = @{}
$environmentValues = [ordered]@{
    AVIUTL2_MCP_REAL_TEST = "1"
    AVIUTL2_MCP_REAL_AVIUTL_PATH = $resolvedAviUtlPath
    AVIUTL2_MCP_REAL_DATA_PATH = $resolvedDataPath
    AVIUTL2_MCP_REAL_PROJECT_PATH = $resolvedProjectPath
    AVIUTL2_MCP_REAL_TEMP_ROOT = $resolvedTemporaryRoot
    AVIUTL2_MCP_NATIVE_BRIDGE_PATH = $nativeBridgePath
    AVIUTL2_MCP_REAL_BRIDGE_PACKAGE_PATH = $resolvedBridgePackagePath
    AVIUTL2_MCP_REPOSITORY_ROOT = $repositoryRoot
}
$executionError = $null

try {
    foreach ($entry in $environmentValues.GetEnumerator()) {
        $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }

    & $cmakePath --build (Join-Path $repositoryRoot "build\native") --config $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Native Bridge build failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $nativeBridgePath -PathType Leaf)) {
        throw "The native Bridge artifact was not produced."
    }

    dotnet build $testProjectPath --no-restore --configuration $Configuration --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Real-test build failed with exit code $LASTEXITCODE."
    }

    dotnet test $testProjectPath `
        --no-restore `
        --no-build `
        --configuration $Configuration `
        --filter $TestFilter `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Real AviUtl2 test failed with exit code $LASTEXITCODE."
    }
}
catch {
    $executionError = $_
}
finally {
    foreach ($entry in $environmentValues.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $savedEnvironment[$entry.Key], "Process")
    }
    $sourceHashAfter = (Get-FileHash -LiteralPath $resolvedProjectPath -Algorithm SHA256).Hash
    if ($sourceHashBefore -ne $sourceHashAfter) {
        throw "The source .aup2 hash changed during the real test."
    }
}

$latestReport = Get-ChildItem -LiteralPath $reportRoot -Filter "debug-report.json" -File -Recurse |
    Where-Object { -not $existingReportPaths.ContainsKey($_.FullName) } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $latestReport) {
    if ($null -ne $executionError) {
        throw "The real test failed and did not produce a new debug report. $($executionError.Exception.Message)"
    }
    throw "The real test passed but did not produce a new debug report."
}
Write-Output "Debug report: $($latestReport.FullName)"
if ($null -ne $executionError) {
    throw $executionError
}
Write-Output "Real AviUtl2 E2E passed."
