[CmdletBinding()]
param(
    [string]$InstallRoot = $PSScriptRoot,
    [string]$Repository = "",
    [string]$BridgePluginDirectory = "",
    [switch]$SkipBridgeUpdate,
    [switch]$Force,
    [string]$ReleaseMetadataPath = "",
    [string]$ReleaseAssetDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Utf8JsonAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporaryPath = Join-Path $parent (".{0}.{1}.tmp" -f ([IO.Path]::GetFileName($Path)), [Guid]::NewGuid().ToString("N"))
    try {
        $json = ($Value | ConvertTo-Json -Depth 12) + "`r`n"
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Get-ContainedPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $normalizedChild = [IO.Path]::GetFullPath($Child)
    if (-not $normalizedChild.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escaped its owned root: $normalizedChild"
    }
    return $normalizedChild
}

function Remove-OwnedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $ownedPath = Get-ContainedPath -Root $Root -Child $Path
    if (Test-Path -LiteralPath $ownedPath -PathType Container) {
        Remove-Item -LiteralPath $ownedPath -Recurse -Force
    }
}

function Copy-ReleaseAsset {
    param(
        [Parameter(Mandatory = $true)][object]$Asset,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$LocalAssetDirectory
    )

    if (-not [string]::IsNullOrWhiteSpace($LocalAssetDirectory)) {
        $source = Join-Path $LocalAssetDirectory ([string]$Asset.name)
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Release fixture asset is missing: $source"
        }
        Copy-Item -LiteralPath $source -Destination $Destination
        return
    }
    $downloadUrl = [string]$Asset.browser_download_url
    if (-not $downloadUrl.StartsWith("https://github.com/", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release asset URL is not hosted on GitHub: $downloadUrl"
    }
    Invoke-WebRequest -Uri $downloadUrl -OutFile $Destination -UseBasicParsing
}

function Get-ReleaseAsset {
    param(
        [Parameter(Mandatory = $true)][object]$Release,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $matches = @($Release.assets | Where-Object { $_.name -eq $Name })
    if ($matches.Count -ne 1) {
        throw "Release must contain exactly one asset named '$Name'."
    }
    return $matches[0]
}

function Assert-ReleaseFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$ManifestFile,
        [Parameter(Mandatory = $true)][hashtable]$Checksums
    )

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne [long]$ManifestFile.byteLength) {
        throw "Release asset size mismatch: $($item.Name)"
    }
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = ([string]$ManifestFile.sha256).ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Release asset hash mismatch: $($item.Name)"
    }
    if (-not $Checksums.ContainsKey($item.Name) -or $Checksums[$item.Name] -ne $actualHash) {
        throw "checksums.sha256 does not agree with the release manifest: $($item.Name)"
    }
}

function Expand-SafeZip {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    $root = [IO.Path]::GetFullPath($DestinationDirectory).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $destination = [IO.Path]::GetFullPath((Join-Path $DestinationDirectory $entry.FullName))
            if (-not $destination.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Archive entry escaped its destination: $($entry.FullName)"
            }
            if ([string]::IsNullOrEmpty($entry.Name)) {
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                continue
            }
            $parent = Split-Path -Parent $destination
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
            $input = $entry.Open()
            try {
                $output = [IO.File]::Open(
                    $destination,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None)
                try {
                    $input.CopyTo($output)
                }
                finally {
                    $output.Dispose()
                }
            }
            finally {
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-ServerPackage {
    param(
        [Parameter(Mandatory = $true)][string]$ServerPath,
        [Parameter(Mandatory = $true)][string]$Version
    )

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($ServerPath)
    if ([string]::IsNullOrWhiteSpace($versionInfo.ProductVersion) -or
        -not $versionInfo.ProductVersion.StartsWith($Version, [StringComparison]::Ordinal)) {
        throw "Server ProductVersion '$($versionInfo.ProductVersion)' does not match release $Version."
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ServerPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = "--self-test"
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Server smoke process did not start."
        }
        if ($process.WaitForExit(5000)) {
            $standardOutput = $process.StandardOutput.ReadToEnd()
            $standardError = $process.StandardError.ReadToEnd()
            if ($process.ExitCode -ne 0) {
                throw "Server self-test failed with exit code $($process.ExitCode): $standardError"
            }
            if (-not [string]::IsNullOrWhiteSpace($standardOutput)) {
                $result = $standardOutput | ConvertFrom-Json
                if ($result.ok -ne $true) {
                    throw "Server self-test did not return ok=true."
                }
            }
            return
        }

        # v0.2.1 and earlier have no --self-test switch. Remaining alive with an open
        # stdin after startup is the backwards-compatible smoke check.
        $process.Kill()
        $process.WaitForExit()
    }
    finally {
        $process.Dispose()
    }
}

function Get-InstalledBridgeVersion {
    param([Parameter(Mandatory = $true)][string]$PluginDirectory)

    $manifestPath = Join-Path $PluginDirectory "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return ""
    }
    try {
        return [string]((Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).version)
    }
    catch {
        return ""
    }
}

function Install-BridgePackage {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$PluginDirectory,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$OwnedInstallRoot
    )

    $normalizedPluginDirectory = [IO.Path]::GetFullPath($PluginDirectory).TrimEnd(
        [IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetFileName($normalizedPluginDirectory) -ne "AviUtl2MCP") {
        throw "Bridge plugin directory must end with AviUtl2MCP."
    }
    $bridgeExtract = Join-Path $WorkingDirectory "bridge-extract"
    Expand-SafeZip -ArchivePath $ArchivePath -DestinationDirectory $bridgeExtract
    $sourceDirectory = Join-Path $bridgeExtract "Plugin\AviUtl2MCP"
    $sourceManifest = Join-Path $sourceDirectory "manifest.json"
    if (-not (Test-Path -LiteralPath $sourceManifest -PathType Leaf)) {
        throw "Bridge package manifest is missing."
    }
    $packageVersion = [string]((Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json).version)
    if ($packageVersion -ne $Version) {
        throw "Bridge package version '$packageVersion' does not match release $Version."
    }

    $pluginParent = Split-Path -Parent $normalizedPluginDirectory
    New-Item -ItemType Directory -Path $pluginParent -Force | Out-Null
    $replacement = Join-Path $pluginParent (".AviUtl2MCP.update.{0}" -f [Guid]::NewGuid().ToString("N"))
    Copy-Item -LiteralPath $sourceDirectory -Destination $replacement -Recurse
    $backup = ""
    try {
        if (Test-Path -LiteralPath $normalizedPluginDirectory -PathType Container) {
            $backupRoot = Join-Path $OwnedInstallRoot "bridge-backups"
            New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
            $backup = Join-Path $backupRoot ("v{0}-{1}" -f $Version, [DateTimeOffset]::UtcNow.ToString("yyyyMMddHHmmss"))
            Move-Item -LiteralPath $normalizedPluginDirectory -Destination $backup
        }
        Move-Item -LiteralPath $replacement -Destination $normalizedPluginDirectory
    }
    catch {
        if (-not [string]::IsNullOrWhiteSpace($backup) -and
            -not (Test-Path -LiteralPath $normalizedPluginDirectory) -and
            (Test-Path -LiteralPath $backup -PathType Container)) {
            Move-Item -LiteralPath $backup -Destination $normalizedPluginDirectory
        }
        throw
    }
    finally {
        if (Test-Path -LiteralPath $replacement -PathType Container) {
            Remove-Item -LiteralPath $replacement -Recurse -Force
        }
    }
}

function Copy-StableScripts {
    param(
        [Parameter(Mandatory = $true)][string]$VersionDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    foreach ($scriptName in @("Run-AviUtl2MCP.ps1", "Update-AviUtl2MCP.ps1")) {
        $source = Join-Path $VersionDirectory $scriptName
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $DestinationRoot $scriptName) -Force
        }
    }
}

$resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot)
New-Item -ItemType Directory -Path $resolvedInstallRoot -Force | Out-Null
$stateDirectory = Join-Path $resolvedInstallRoot "state"
$settingsPath = Join-Path $stateDirectory "settings.json"
$updateStatePath = Join-Path $stateDirectory "update-state.json"
$activeStatePath = Join-Path $stateDirectory "active-server.json"
$updateIntervalHours = 6
if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($Repository)) {
        $Repository = [string]$settings.repository
    }
    if ([string]::IsNullOrWhiteSpace($BridgePluginDirectory)) {
        $BridgePluginDirectory = [string]$settings.bridgePluginDirectory
    }
    if (-not $PSBoundParameters.ContainsKey("SkipBridgeUpdate")) {
        $SkipBridgeUpdate = [bool]$settings.skipBridgeUpdate
    }
    if ($settings.PSObject.Properties.Name -contains "updateIntervalHours") {
        $updateIntervalHours = [int]$settings.updateIntervalHours
    }
}
if ([string]::IsNullOrWhiteSpace($Repository)) {
    $Repository = "gomi1124/Aviutl2MCP"
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Invalid GitHub repository name: $Repository"
}

$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $rootHashBytes = $sha256.ComputeHash(
        [Text.Encoding]::UTF8.GetBytes($resolvedInstallRoot.ToLowerInvariant()))
}
finally {
    $sha256.Dispose()
}
$rootHash = ([BitConverter]::ToString($rootHashBytes) -replace '-', '').Substring(0, 24)
$mutex = [Threading.Mutex]::new($false, "Local\AviUtl2MCP.Update.$rootHash")
$hasMutex = $false
try {
    try {
        $hasMutex = $mutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $hasMutex = $true
    }
    if (-not $hasMutex) {
        return
    }

    if (-not $Force -and (Test-Path -LiteralPath $updateStatePath -PathType Leaf)) {
        $previousState = Get-Content -LiteralPath $updateStatePath -Raw | ConvertFrom-Json
        $lastCheckedAt = [DateTimeOffset]::MinValue
        if ([DateTimeOffset]::TryParse([string]$previousState.lastCheckedAt, [ref]$lastCheckedAt) -and
            [string]$previousState.status -ne "bridge_pending" -and
            [DateTimeOffset]::UtcNow - $lastCheckedAt -lt [TimeSpan]::FromHours($updateIntervalHours)) {
            return
        }
    }

    $release = if (-not [string]::IsNullOrWhiteSpace($ReleaseMetadataPath)) {
        Get-Content -LiteralPath $ReleaseMetadataPath -Raw | ConvertFrom-Json
    }
    else {
        $apiUri = "https://api.github.com/repos/$Repository/releases/latest"
        Invoke-RestMethod -Uri $apiUri -Headers @{ "User-Agent" = "AviUtl2MCP-Updater" }
    }
    if ([bool]$release.draft -or [bool]$release.prerelease) {
        throw "The GitHub latest endpoint returned a draft or prerelease."
    }
    $tag = [string]$release.tag_name
    if ($tag -notmatch '^v(?<version>[0-9]+\.[0-9]+\.[0-9]+)$') {
        throw "Latest release tag is not a stable semantic version: $tag"
    }
    $version = $Matches.version
    $activeVersion = ""
    if (Test-Path -LiteralPath $activeStatePath -PathType Leaf) {
        try {
            $activeVersion = [string]((Get-Content -LiteralPath $activeStatePath -Raw | ConvertFrom-Json).version)
        }
        catch {
            $activeVersion = ""
        }
    }
    $isBridgeCurrent = $SkipBridgeUpdate
    if (-not $SkipBridgeUpdate -and -not [string]::IsNullOrWhiteSpace($BridgePluginDirectory)) {
        $isBridgeCurrent = (Get-InstalledBridgeVersion -PluginDirectory $BridgePluginDirectory) -eq $version
    }
    if ($activeVersion -eq $version -and $isBridgeCurrent) {
        Write-Utf8JsonAtomic -Path $updateStatePath -Value ([ordered]@{
            schemaVersion = "1.0"
            status = "current"
            availableVersion = $version
            lastCheckedAt = [DateTimeOffset]::UtcNow.ToString("O")
        })
        return
    }
    $manifestAsset = Get-ReleaseAsset -Release $release -Name "release-manifest.json"
    $checksumsAsset = Get-ReleaseAsset -Release $release -Name "checksums.sha256"
    $stagingRoot = Join-Path $resolvedInstallRoot ("staging\{0}" -f [Guid]::NewGuid().ToString("N"))
    $stagingRoot = Get-ContainedPath -Root $resolvedInstallRoot -Child $stagingRoot
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    try {
        $manifestPath = Join-Path $stagingRoot "release-manifest.json"
        $checksumsPath = Join-Path $stagingRoot "checksums.sha256"
        Copy-ReleaseAsset -Asset $manifestAsset -Destination $manifestPath -LocalAssetDirectory $ReleaseAssetDirectory
        Copy-ReleaseAsset -Asset $checksumsAsset -Destination $checksumsPath -LocalAssetDirectory $ReleaseAssetDirectory
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.schemaVersion -ne "1.0" -or [string]$manifest.version -ne $version) {
            throw "Release manifest version does not match tag $tag."
        }
        if ([string]$manifest.configuration -ne "Release" -or
            [string]$manifest.runtime -ne "win-x64") {
            throw "Release manifest must describe a Release win-x64 package."
        }
        $checksums = @{}
        foreach ($line in Get-Content -LiteralPath $checksumsPath) {
            if ($line -notmatch '^(?<hash>[0-9a-fA-F]{64})\s{2}(?<name>[^\\/]+)$') {
                throw "Invalid checksums.sha256 line: $line"
            }
            $checksums[$Matches.name] = $Matches.hash.ToLowerInvariant()
        }
        $serverName = "AviUtl2MCP-Server-win-x64-v$version.zip"
        $bridgeName = "AviUtl2MCP-Bridge-v$version.au2pkg.zip"
        $requiredNames = @($serverName)
        if (-not $SkipBridgeUpdate) {
            $requiredNames += $bridgeName
        }
        $downloaded = @{}
        foreach ($name in $requiredNames) {
            $manifestMatches = @($manifest.files | Where-Object { $_.name -eq $name })
            if ($manifestMatches.Count -ne 1) {
                throw "Release manifest must contain exactly one '$name' entry."
            }
            $asset = Get-ReleaseAsset -Release $release -Name $name
            $destination = Join-Path $stagingRoot $name
            Copy-ReleaseAsset -Asset $asset -Destination $destination -LocalAssetDirectory $ReleaseAssetDirectory
            Assert-ReleaseFile -Path $destination -ManifestFile $manifestMatches[0] -Checksums $checksums
            $downloaded[$name] = $destination
        }

        $versionsRoot = Join-Path $resolvedInstallRoot "versions"
        New-Item -ItemType Directory -Path $versionsRoot -Force | Out-Null
        $versionDirectory = Get-ContainedPath -Root $versionsRoot -Child (Join-Path $versionsRoot "v$version")
        if (-not (Test-Path -LiteralPath $versionDirectory -PathType Container)) {
            $versionStage = Get-ContainedPath -Root $versionsRoot -Child (Join-Path $versionsRoot (".v$version.{0}" -f [Guid]::NewGuid().ToString("N")))
            try {
                Expand-SafeZip -ArchivePath $downloaded[$serverName] -DestinationDirectory $versionStage
                $stagedServer = Join-Path $versionStage "AviUtl2MCP.Server.exe"
                if (-not (Test-Path -LiteralPath $stagedServer -PathType Leaf)) {
                    throw "Server archive does not contain AviUtl2MCP.Server.exe."
                }
                Test-ServerPackage -ServerPath $stagedServer -Version $version
                Move-Item -LiteralPath $versionStage -Destination $versionDirectory
            }
            finally {
                if (Test-Path -LiteralPath $versionStage -PathType Container) {
                    Remove-OwnedDirectory -Root $versionsRoot -Path $versionStage
                }
            }
        }
        else {
            Test-ServerPackage -ServerPath (Join-Path $versionDirectory "AviUtl2MCP.Server.exe") -Version $version
        }

        if (-not $SkipBridgeUpdate) {
            $installedBridgeVersion = Get-InstalledBridgeVersion -PluginDirectory $BridgePluginDirectory
            if ($installedBridgeVersion -ne $version) {
                if (@(Get-Process -Name "aviutl2" -ErrorAction SilentlyContinue).Count -gt 0) {
                    $pendingRoot = Join-Path $resolvedInstallRoot "pending"
                    New-Item -ItemType Directory -Path $pendingRoot -Force | Out-Null
                    $pendingBridge = Join-Path $pendingRoot $bridgeName
                    Copy-Item -LiteralPath $downloaded[$bridgeName] -Destination $pendingBridge -Force
                    Write-Utf8JsonAtomic -Path $updateStatePath -Value ([ordered]@{
                        schemaVersion = "1.0"
                        status = "bridge_pending"
                        availableVersion = $version
                        pendingBridgePath = $pendingBridge
                        lastCheckedAt = [DateTimeOffset]::UtcNow.ToString("O")
                    })
                    return
                }
                Install-BridgePackage `
                    -ArchivePath $downloaded[$bridgeName] `
                    -PluginDirectory $BridgePluginDirectory `
                    -Version $version `
                    -WorkingDirectory $stagingRoot `
                    -OwnedInstallRoot $resolvedInstallRoot
            }
        }

        Copy-StableScripts -VersionDirectory $versionDirectory -DestinationRoot $resolvedInstallRoot
        $relativeServerPath = "versions\v$version\AviUtl2MCP.Server.exe"
        Write-Utf8JsonAtomic -Path $activeStatePath -Value ([ordered]@{
            schemaVersion = "1.0"
            version = $version
            serverPath = $relativeServerPath
            activatedAt = [DateTimeOffset]::UtcNow.ToString("O")
        })
        Write-Utf8JsonAtomic -Path $updateStatePath -Value ([ordered]@{
            schemaVersion = "1.0"
            status = "current"
            availableVersion = $version
            lastCheckedAt = [DateTimeOffset]::UtcNow.ToString("O")
        })
    }
    finally {
        Remove-OwnedDirectory -Root $resolvedInstallRoot -Path $stagingRoot
    }
}
catch {
    Write-Utf8JsonAtomic -Path $updateStatePath -Value ([ordered]@{
        schemaVersion = "1.0"
        status = "failed"
        message = $_.Exception.Message
        lastCheckedAt = [DateTimeOffset]::UtcNow.ToString("O")
    })
    throw
}
finally {
    if ($hasMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
