[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CorrelationId,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$Command = "",
    [int]$ExitCode = 0,
    [string]$BeforeRevision,
    [string]$AfterRevision,
    [string]$BeforePreviewPath,
    [string]$AfterPreviewPath,
    [string[]]$ServerLogPath = @(),
    [string[]]$BridgeLogPath = @(),
    [string[]]$AviUtlLogPath = @(),
    [string[]]$ArtifactPath = @(),
    [string]$ChecksPath,
    [string]$ComponentVersionsPath,
    [int[]]$LaunchedProcessId = @(),
    [ValidateRange(1, 2000)]
    [int]$MaxLogLines = 2000,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-NormalizedCorrelationId {
    param([Parameter(Mandatory = $true)][string]$Value)

    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse($Value, [ref]$parsed)) {
        throw "CorrelationId must be a UUID."
    }
    $normalized = $parsed.ToString("D").ToLowerInvariant()
    if ($normalized[14] -ne '7' -or "89ab".IndexOf($normalized[19]) -lt 0) {
        throw "CorrelationId must be an RFC 9562 UUIDv7."
    }
    return $normalized
}

function Protect-DebugText {
    param([AllowNull()][string]$Value)

    if ($null -eq $Value) {
        return $null
    }
    $masked = [Text.RegularExpressions.Regex]::Replace(
        $Value,
        '(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+',
        'Bearer [REDACTED]')
    $masked = [Text.RegularExpressions.Regex]::Replace(
        $masked,
        '(?i)\b(authorization|api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|passwd|secret)\b\s*[:=]\s*[^,\s;]+',
        '$1=[REDACTED]')
    return [Text.RegularExpressions.Regex]::Replace(
        $masked,
        '(?i)([A-Z]:\\{1,2}Users\\{1,2})[^\\\s"'']+',
        '$1[USER]')
}

function Get-RequiredLeafPath {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$ParameterName
    )

    $resolved = Resolve-Path -LiteralPath $Value -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved.Path -PathType Leaf)) {
        throw "$ParameterName must identify a file."
    }
    return $resolved.Path
}

function Get-FileDescriptor {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return [ordered]@{
            present = $false
            name = $null
            byteLength = $null
            sha256 = $null
        }
    }
    $path = Get-RequiredLeafPath -Value $Value -ParameterName "artifact path"
    $item = Get-Item -LiteralPath $path
    return [ordered]@{
        present = $true
        name = $item.Name
        byteLength = $item.Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Read-CorrelationLogs {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [string[]]$Paths,
        [Parameter(Mandatory = $true)][string]$NormalizedCorrelationId,
        [Parameter(Mandatory = $true)][int]$Limit
    )

    if ($Paths.Count -gt 32) {
        throw "$Source log path count exceeds 32."
    }
    $windowLines = [Math]::Min($Limit * 4, 8000)
    $entries = [Collections.Generic.List[string]]::new()
    $files = [Collections.Generic.List[object]]::new()
    foreach ($pathValue in $Paths) {
        $path = Get-RequiredLeafPath -Value $pathValue -ParameterName "$Source log path"
        $item = Get-Item -LiteralPath $path
        $files.Add([ordered]@{
            name = $item.Name
            byteLength = $item.Length
        })
        foreach ($line in (Get-Content -LiteralPath $path -Tail $windowLines -Encoding UTF8)) {
            if ($line.IndexOf($NormalizedCorrelationId, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                continue
            }
            $safeLine = Protect-DebugText -Value $line
            if ($safeLine.Length -gt 4096) {
                $safeLine = $safeLine.Substring(0, 4093) + "..."
            }
            $entries.Add($safeLine)
        }
    }
    $isTruncated = $entries.Count -gt $Limit
    $firstIndex = [Math]::Max(0, $entries.Count - $Limit)
    $limitedEntries = @()
    for ($index = $firstIndex; $index -lt $entries.Count; $index++) {
        $limitedEntries += $entries[$index]
    }
    return [ordered]@{
        source = $Source
        files = @($files)
        entries = $limitedEntries
        isTruncated = $isTruncated
        tailWindowLines = $windowLines
    }
}

function Read-Checks {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return @()
    }
    $path = Get-RequiredLeafPath -Value $Value -ParameterName "ChecksPath"
    $document = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $result = [Collections.Generic.List[object]]::new()
    foreach ($check in @($document)) {
        if ([string]::IsNullOrWhiteSpace([string]$check.name)) {
            throw "Every debug check must have a name."
        }
        $status = ([string]$check.status).ToLowerInvariant()
        if ($status -notin @("pass", "warning", "fail", "skipped")) {
            throw "Debug check status must be pass, warning, fail, or skipped."
        }
        $evidence = @($check.evidence | ForEach-Object { Protect-DebugText -Value ([string]$_) })
        if ($evidence.Count -gt 50) {
            throw "Debug check evidence count exceeds 50."
        }
        $result.Add([ordered]@{
            name = Protect-DebugText -Value ([string]$check.name)
            status = $status
            evidence = $evidence
        })
    }
    return @($result)
}

function Read-ComponentVersions {
    param([AllowNull()][string]$Value)

    $versions = [ordered]@{}
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $versions
    }
    $path = Get-RequiredLeafPath -Value $Value -ParameterName "ComponentVersionsPath"
    $document = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($property in $document.PSObject.Properties) {
        if ($property.Name.Length -gt 64) {
            throw "Component version name exceeds 64 characters."
        }
        $versions[$property.Name] = Protect-DebugText -Value ([string]$property.Value)
    }
    return $versions
}

function Invoke-VersionCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )

    try {
        $output = & $FilePath @Arguments 2>&1
        $commandExitCode = $LASTEXITCODE
        if ($commandExitCode -ne 0) {
            return [ordered]@{
                available = $false
                value = $null
                error = "exit_code_$commandExitCode"
            }
        }
        return [ordered]@{
            available = $true
            value = Protect-DebugText -Value (($output | Out-String).Trim())
            error = $null
        }
    }
    catch {
        return [ordered]@{
            available = $false
            value = $null
            error = $_.Exception.GetType().Name
        }
    }
}

$normalizedCorrelationId = Get-NormalizedCorrelationId -Value $CorrelationId
$resolvedRepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot -ErrorAction Stop).Path
$resolvedOutputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$correlationDirectory = Join-Path $resolvedOutputRoot $normalizedCorrelationId
$reportPath = Join-Path $correlationDirectory "debug-report.json"
if (Test-Path -LiteralPath $reportPath) {
    throw "A debug report already exists for this correlation ID."
}
New-Item -ItemType Directory -Path $correlationDirectory -Force | Out-Null

$repositoryRevision = Invoke-VersionCommand -FilePath "git" -Arguments @(
    "-c",
    "safe.directory=$resolvedRepositoryRoot",
    "-C",
    $resolvedRepositoryRoot,
    "rev-parse",
    "HEAD")
$repositoryBranch = Invoke-VersionCommand -FilePath "git" -Arguments @(
    "-c",
    "safe.directory=$resolvedRepositoryRoot",
    "-C",
    $resolvedRepositoryRoot,
    "branch",
    "--show-current")
$repositoryStatus = Invoke-VersionCommand -FilePath "git" -Arguments @(
    "-c",
    "safe.directory=$resolvedRepositoryRoot",
    "-C",
    $resolvedRepositoryRoot,
    "status",
    "--porcelain")

$checks = @(Read-Checks -Value $ChecksPath)
$componentVersions = Read-ComponentVersions -Value $ComponentVersionsPath
$beforePreview = Get-FileDescriptor -Value $BeforePreviewPath
$afterPreview = Get-FileDescriptor -Value $AfterPreviewPath
$artifactValues = [Collections.Generic.List[object]]::new()
$uniqueArtifactPaths = @($ArtifactPath + $BeforePreviewPath + $AfterPreviewPath) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -Unique
if ($uniqueArtifactPaths.Count -gt 64) {
    throw "Artifact path count exceeds 64."
}
foreach ($pathValue in $uniqueArtifactPaths) {
    $artifactValues.Add((Get-FileDescriptor -Value $pathValue))
}

$hasFailedCheck = @($checks | Where-Object { $_.status -eq "fail" }).Count -gt 0
$hasWarningCheck = @($checks | Where-Object { $_.status -eq "warning" }).Count -gt 0
$overallStatus = if ($ExitCode -ne 0 -or $hasFailedCheck) {
    "failed"
}
elseif ($hasWarningCheck) {
    "degraded"
}
else {
    "passed"
}

$report = [ordered]@{
    schemaVersion = "1.0"
    correlationId = $normalizedCorrelationId
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    status = $overallStatus
    command = [ordered]@{
        text = Protect-DebugText -Value $Command
        exitCode = $ExitCode
    }
    versions = [ordered]@{
        script = "1.0.0"
        repository = [ordered]@{
            revision = $repositoryRevision.value
            branch = $repositoryBranch.value
            dirty = $repositoryStatus.available -and -not [string]::IsNullOrEmpty($repositoryStatus.value)
        }
        dotnet = Invoke-VersionCommand -FilePath "dotnet" -Arguments @("--version")
        cmake = Invoke-VersionCommand -FilePath "cmake" -Arguments @("--version")
        components = $componentVersions
    }
    checks = $checks
    revisions = [ordered]@{
        before = $BeforeRevision
        after = $AfterRevision
        changed = if ($null -ne $BeforeRevision -and $null -ne $AfterRevision) {
            $BeforeRevision -ne $AfterRevision
        }
        else {
            $null
        }
    }
    previews = [ordered]@{
        before = $beforePreview
        after = $afterPreview
        hashChanged = if ($beforePreview.present -and $afterPreview.present) {
            $beforePreview.sha256 -ne $afterPreview.sha256
        }
        else {
            $null
        }
    }
    logs = [ordered]@{
        server = Read-CorrelationLogs -Source "server" -Paths $ServerLogPath -NormalizedCorrelationId $normalizedCorrelationId -Limit $MaxLogLines
        bridge = Read-CorrelationLogs -Source "bridge" -Paths $BridgeLogPath -NormalizedCorrelationId $normalizedCorrelationId -Limit $MaxLogLines
        aviutl = Read-CorrelationLogs -Source "aviutl" -Paths $AviUtlLogPath -NormalizedCorrelationId $normalizedCorrelationId -Limit $MaxLogLines
    }
    artifacts = @($artifactValues)
    cleanupScope = [ordered]@{
        correlationId = $normalizedCorrelationId
        directoryName = $normalizedCorrelationId
        launchedProcessIds = @($LaunchedProcessId | Sort-Object -Unique)
    }
}

$json = $report | ConvertTo-Json -Depth 12
$temporaryPath = Join-Path $correlationDirectory ("debug-report.{0}.tmp" -f [Guid]::NewGuid().ToString("N"))
try {
    [IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $reportPath
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Write-Output $reportPath
