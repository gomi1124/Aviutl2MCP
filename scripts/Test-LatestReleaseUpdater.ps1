[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )

    [IO.File]::WriteAllText(
        $Path,
        (($Value | ConvertTo-Json -Depth 10) + "`r`n"),
        [Text.UTF8Encoding]::new($false))
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse
    }
}

function Invoke-WindowsPowerShell {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $powershellPath = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $powershellPath @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$serverProject = Join-Path $repositoryRoot "src\AviUtl2MCP.Server\AviUtl2MCP.Server.csproj"
if (-not $NoBuild) {
    dotnet build $serverProject --configuration $Configuration --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Server build failed with exit code $LASTEXITCODE."
    }
}

$version = [IO.File]::ReadAllText((Join-Path $repositoryRoot "VERSION")).Trim()
$serverOutput = Join-Path $repositoryRoot "src\AviUtl2MCP.Server\bin\$Configuration\net10.0-windows10.0.19041.0"
$serverExecutable = Join-Path $serverOutput "AviUtl2MCP.Server.exe"
if (-not (Test-Path -LiteralPath $serverExecutable -PathType Leaf)) {
    throw "Built server executable is missing: $serverExecutable"
}

$ownedRoot = Join-Path $repositoryRoot "artifacts\updater-tests"
New-Item -ItemType Directory -Path $ownedRoot -Force | Out-Null
$testRoot = Join-Path $ownedRoot ([Guid]::NewGuid().ToString("N"))
$assetDirectory = Join-Path $testRoot "assets"
$packageDirectory = Join-Path $testRoot "package"
$installDirectory = Join-Path $testRoot "install"
$tamperedInstallDirectory = Join-Path $testRoot "tampered-install"
New-Item -ItemType Directory -Path $assetDirectory, $packageDirectory | Out-Null

try {
    # Arrange: create a local stable Release fixture from the real built server.
    Copy-DirectoryContents -Source $serverOutput -Destination $packageDirectory
    foreach ($scriptName in @("Run-AviUtl2MCP.ps1", "Update-AviUtl2MCP.ps1")) {
        Copy-Item `
            -LiteralPath (Join-Path $repositoryRoot "scripts\$scriptName") `
            -Destination (Join-Path $packageDirectory $scriptName)
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $serverArchiveName = "AviUtl2MCP-Server-win-x64-v$version.zip"
    $serverArchivePath = Join-Path $assetDirectory $serverArchiveName
    [IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $serverArchivePath)
    $archiveItem = Get-Item -LiteralPath $serverArchivePath
    $archiveHash = (Get-FileHash -LiteralPath $serverArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Utf8Json -Path (Join-Path $assetDirectory "release-manifest.json") -Value ([ordered]@{
        schemaVersion = "1.0"
        version = $version
        configuration = "Release"
        runtime = "win-x64"
        files = @([ordered]@{
            name = $serverArchiveName
            byteLength = $archiveItem.Length
            sha256 = $archiveHash
        })
    })
    [IO.File]::WriteAllText(
        (Join-Path $assetDirectory "checksums.sha256"),
        "$archiveHash  $serverArchiveName`r`n",
        [Text.UTF8Encoding]::new($false))
    $releasePath = Join-Path $testRoot "release.json"
    $assetNames = @("release-manifest.json", "checksums.sha256", $serverArchiveName)
    Write-Utf8Json -Path $releasePath -Value ([ordered]@{
        tag_name = "v$version"
        draft = $false
        prerelease = $false
        assets = @($assetNames | ForEach-Object {
            [ordered]@{
                name = $_
                browser_download_url = "https://github.com/gomi1124/Aviutl2MCP/releases/download/v$version/$_"
            }
        })
    })

    # Act: install the verified fixture and launch the server through the stable path.
    $updateResult = Invoke-WindowsPowerShell -Arguments @(
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $repositoryRoot "scripts\Update-AviUtl2MCP.ps1"),
        "-InstallRoot", $installDirectory,
        "-ReleaseMetadataPath", $releasePath,
        "-ReleaseAssetDirectory", $assetDirectory,
        "-SkipBridgeUpdate", "-Force"
    )
    $launcherResult = Invoke-WindowsPowerShell -Arguments @(
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $installDirectory "Run-AviUtl2MCP.ps1"),
        "-SkipUpdateCheck", "--self-test"
    )

    # Assert: activation is atomic and the stable launcher reaches the installed server.
    Assert-True ($updateResult.ExitCode -eq 0) "Verified update failed: $($updateResult.Output)"
    $activeStatePath = Join-Path $installDirectory "state\active-server.json"
    Assert-True (Test-Path -LiteralPath $activeStatePath -PathType Leaf) "Active state was not created."
    $activeState = Get-Content -LiteralPath $activeStatePath -Raw | ConvertFrom-Json
    Assert-True ($activeState.version -eq $version) "The wrong server version was activated."
    Assert-True ($launcherResult.ExitCode -eq 0) "Stable launcher failed: $($launcherResult.Output)"
    $selfTest = $launcherResult.Output.Trim() | ConvertFrom-Json
    Assert-True ($selfTest.ok -eq $true) "Stable launcher self-test did not return ok=true."

    # Arrange: mutate the signed server archive without updating the manifest.
    [IO.File]::AppendAllText($serverArchivePath, "tampered", [Text.UTF8Encoding]::new($false))

    # Act: try installing the tampered fixture into a fresh root.
    $tamperedResult = Invoke-WindowsPowerShell -Arguments @(
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $repositoryRoot "scripts\Update-AviUtl2MCP.ps1"),
        "-InstallRoot", $tamperedInstallDirectory,
        "-ReleaseMetadataPath", $releasePath,
        "-ReleaseAssetDirectory", $assetDirectory,
        "-SkipBridgeUpdate", "-Force"
    )

    # Assert: a failed integrity check never changes the active pointer.
    Assert-True ($tamperedResult.ExitCode -ne 0) "Tampered Release was unexpectedly accepted."
    Assert-True `
        (-not (Test-Path -LiteralPath (Join-Path $tamperedInstallDirectory "state\active-server.json"))) `
        "Tampered Release created an active state."

    Write-Output "Latest Release updater tests passed."
}
finally {
    $normalizedOwnedRoot = [IO.Path]::GetFullPath($ownedRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $normalizedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if (-not $normalizedTestRoot.StartsWith($normalizedOwnedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an unowned updater test directory."
    }
    if (Test-Path -LiteralPath $normalizedTestRoot -PathType Container) {
        Remove-Item -LiteralPath $normalizedTestRoot -Recurse -Force
    }
}
