[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SuiteName,

    [Parameter(Mandatory = $true)]
    [string]$TestProject,

    [Parameter(Mandatory = $true)]
    [string[]]$CheckName,

    [string]$Configuration = "Debug",
    [string]$TestFilter = "",
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-UuidV7 {
    $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString(
        "x12",
        [Globalization.CultureInfo]::InvariantCulture)
    $randomBytes = [byte[]]::new(10)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($randomBytes)
    }
    finally {
        $generator.Dispose()
    }
    $random = -join ($randomBytes | ForEach-Object {
        $_.ToString("x2", [Globalization.CultureInfo]::InvariantCulture)
    })
    $variant = (([Convert]::ToInt32($random.Substring(3, 1), 16) -band 3) + 8).ToString(
        "x",
        [Globalization.CultureInfo]::InvariantCulture)
    return [Guid]::Parse(
        "$($timestamp.Substring(0, 8))-$($timestamp.Substring(8, 4))-7$($random.Substring(0, 3))-$variant$($random.Substring(4, 3))-$($random.Substring(7, 12))")
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

if ($SuiteName -notmatch '^[a-z0-9][a-z0-9-]{0,63}$') {
    throw "SuiteName must use lowercase letters, digits, and hyphens."
}
if ($CheckName.Count -eq 0 -or $CheckName.Count -gt 32) {
    throw "CheckName must contain between 1 and 32 values."
}
if ($CheckName.Count -ne @($CheckName | Select-Object -Unique).Count) {
    throw "CheckName values must be unique."
}

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$resolvedTestProject = (Resolve-Path -LiteralPath $TestProject).Path
if ([IO.Path]::GetExtension($resolvedTestProject) -ine ".csproj") {
    throw "TestProject must identify a .csproj file."
}
$correlationId = New-UuidV7
$correlationText = $correlationId.ToString("D").ToLowerInvariant()
$runRoot = Join-Path $repositoryRoot "artifacts\test-runs\$correlationText"
if (Test-Path -LiteralPath $runRoot) {
    throw "The test correlation directory already exists."
}
New-Item -ItemType Directory -Path $runRoot | Out-Null
$runnerLogPath = Join-Path $runRoot "$SuiteName.log"
$trxPath = Join-Path $runRoot "$SuiteName.trx"
$checksPath = Join-Path $runRoot "checks.json"
$versionsPath = Join-Path $runRoot "components.json"
$savedCorrelation = [Environment]::GetEnvironmentVariable(
    "AVIUTL2_MCP_TEST_CORRELATION_ID",
    "Process")
$arguments = [Collections.Generic.List[string]]::new()
$arguments.Add("test")
$arguments.Add($resolvedTestProject)
$arguments.Add("--configuration")
$arguments.Add($Configuration)
$arguments.Add("--no-restore")
if ($NoBuild.IsPresent) {
    $arguments.Add("--no-build")
}
if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
    $arguments.Add("--filter")
    $arguments.Add($TestFilter)
}
$arguments.Add("--logger")
$arguments.Add("trx;LogFileName=$SuiteName.trx")
$arguments.Add("--results-directory")
$arguments.Add($runRoot)
$arguments.Add("--verbosity")
$arguments.Add("minimal")
$commandText = "dotnet " + (($arguments | ForEach-Object {
    if ($_ -match '[\s"]') {
        '"' + $_.Replace('"', '\"') + '"'
    }
    else {
        $_
    }
}) -join ' ')

$exitCode = 1
try {
    [Environment]::SetEnvironmentVariable(
        "AVIUTL2_MCP_TEST_CORRELATION_ID",
        $correlationText,
        "Process")
    $output = & dotnet @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $logLines = @($output | ForEach-Object {
        "[$correlationText] $([string]$_)"
    })
    [IO.File]::WriteAllLines(
        $runnerLogPath,
        $logLines,
        [Text.UTF8Encoding]::new($false))
}
finally {
    [Environment]::SetEnvironmentVariable(
        "AVIUTL2_MCP_TEST_CORRELATION_ID",
        $savedCorrelation,
        "Process")
}

$status = if ($exitCode -eq 0) { "pass" } else { "fail" }
$checks = @($CheckName | ForEach-Object {
    [ordered]@{
        name = $_
        status = $status
        evidence = @(
            "suite=$SuiteName",
            "testProject=$([IO.Path]::GetFileName($resolvedTestProject))",
            "exitCode=$exitCode",
            "trx=$([IO.Path]::GetFileName($trxPath))"
        )
    }
})
Write-Utf8NoBom -Path $checksPath -Value (($checks | ConvertTo-Json -Depth 5) + "`r`n")
$componentVersions = [ordered]@{
    suite = $SuiteName
    configuration = $Configuration
    testProject = [IO.Path]::GetFileName($resolvedTestProject)
}
Write-Utf8NoBom -Path $versionsPath -Value (($componentVersions | ConvertTo-Json) + "`r`n")

$artifacts = [Collections.Generic.List[string]]::new()
$artifacts.Add($runnerLogPath)
if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
    $artifacts.Add($trxPath)
}
$debugRoot = Join-Path $repositoryRoot "artifacts\debug\$SuiteName"
& (Join-Path $PSScriptRoot "New-DebugReport.ps1") `
    -CorrelationId $correlationText `
    -OutputDirectory $debugRoot `
    -Command $commandText `
    -ExitCode $exitCode `
    -ServerLogPath $runnerLogPath `
    -ArtifactPath $artifacts.ToArray() `
    -ChecksPath $checksPath `
    -ComponentVersionsPath $versionsPath `
    -RepositoryRoot $repositoryRoot | Out-Null
$reportPath = Join-Path $debugRoot "$correlationText\debug-report.json"
Write-Output "$SuiteName test report: $reportPath"
if ($exitCode -ne 0) {
    throw "$SuiteName tests failed with exit code $exitCode. Debug report: $reportPath"
}
