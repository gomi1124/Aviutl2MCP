[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = "0.1.0",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required package input is missing: $Source"
    }
    $parent = Split-Path -Parent $Destination
    if (-not [string]::IsNullOrEmpty($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Copy-Item -LiteralPath $Source -Destination $Destination
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Required package directory is missing: $Source"
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse
    }
}

function Get-FileManifest {
    param([Parameter(Mandatory = $true)][string]$Root)

    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $normalizedRoot + [IO.Path]::DirectorySeparatorChar
    return @(Get-ChildItem -LiteralPath $normalizedRoot -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            if (-not $_.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Package manifest input escaped its root."
            }
            [ordered]@{
                path = $_.FullName.Substring($prefix.Length).Replace('\', '/')
                byteLength = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    $normalizedSource = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd(
        [IO.Path]::DirectorySeparatorChar)
    $prefix = $normalizedSource + [IO.Path]::DirectorySeparatorChar
    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }
    $destinationParent = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    $stream = [IO.File]::Open(
        $DestinationPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $normalizedSource -File -Recurse |
                    Sort-Object FullName) {
                if (-not $file.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Zip input escaped its source directory."
                }
                $entryName = $file.FullName.Substring($prefix.Length).Replace('\', '/')
                $entry = $archive.CreateEntry(
                    $entryName,
                    [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(
                    1980,
                    1,
                    1,
                    0,
                    0,
                    0,
                    [TimeSpan]::Zero)
                $input = $file.OpenRead()
                try {
                    $output = $entry.Open()
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
    finally {
        $stream.Dispose()
    }
}

function Get-CMakePath {
    $command = Get-Command cmake -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }
    $visualStudioCMake = "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    if (Test-Path -LiteralPath $visualStudioCMake -PathType Leaf) {
        return $visualStudioCMake
    }
    throw "CMake was not found."
}

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$resolvedOutput = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repositoryRoot "artifacts\release"
}
else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$stagingParent = Join-Path $repositoryRoot "artifacts\.package-staging"
New-Item -ItemType Directory -Path $stagingParent -Force | Out-Null
$stagingId = [Guid]::NewGuid().ToString("N")
$stagingRoot = Join-Path $stagingParent $stagingId
$pluginStage = Join-Path $stagingRoot "plugin"
$serverStage = Join-Path $stagingRoot "server"
$publishStage = Join-Path $stagingRoot "publish"
New-Item -ItemType Directory -Path $pluginStage, $serverStage, $publishStage | Out-Null

try {
    $cmake = Get-CMakePath
    $nativeBuildDirectory = Join-Path $repositoryRoot "build\native"
    if (-not (Test-Path -LiteralPath (Join-Path $nativeBuildDirectory "CMakeCache.txt") -PathType Leaf)) {
        & $cmake -S $repositoryRoot -B $nativeBuildDirectory -A x64
        if ($LASTEXITCODE -ne 0) {
            throw "Native CMake configure failed with exit code $LASTEXITCODE."
        }
    }
    & $cmake --build $nativeBuildDirectory --config $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Native Bridge build failed with exit code $LASTEXITCODE."
    }

    $serverProject = Join-Path $repositoryRoot "src\AviUtl2MCP.Server\AviUtl2MCP.Server.csproj"
    dotnet restore $serverProject `
        --locked-mode `
        --runtime win-x64 `
        -p:NuGetLockFilePath=packages.win-x64.lock.json
    if ($LASTEXITCODE -ne 0) {
        throw "Server locked restore failed with exit code $LASTEXITCODE."
    }
    dotnet publish $serverProject `
        --no-restore `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $publishStage `
        -p:NuGetLockFilePath=packages.win-x64.lock.json `
        -p:Version=$Version `
        -p:PublishTrimmed=false `
        -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) {
        throw "Server publish failed with exit code $LASTEXITCODE."
    }

    $pluginDirectory = Join-Path $pluginStage "Plugin\AviUtl2MCP"
    New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
    $bridgePath = Join-Path $nativeBuildDirectory "artifacts\$Configuration\AviUtl2MCP.Bridge.aux2"
    Copy-RequiredFile `
        -Source $bridgePath `
        -Destination (Join-Path $pluginDirectory "AviUtl2MCP.Bridge.aux2")
    Copy-DirectoryContents `
        -Source (Join-Path $repositoryRoot "assets") `
        -Destination (Join-Path $pluginDirectory "assets")
    Copy-RequiredFile `
        -Source (Join-Path $repositoryRoot "LICENSE") `
        -Destination (Join-Path $pluginDirectory "licenses\AviUtl2MCP.txt")
    Copy-RequiredFile `
        -Source (Join-Path $repositoryRoot "external\aviutl2_sdk\license.txt") `
        -Destination (Join-Path $pluginDirectory "licenses\AviUtl2-SDK.txt")
    Copy-RequiredFile `
        -Source (Join-Path $repositoryRoot "external\nlohmann_json\LICENSE.MIT") `
        -Destination (Join-Path $pluginDirectory "licenses\nlohmann-json.txt")

    $subtitlePath = Join-Path $pluginDirectory "assets\psdtoolkit2\v1\subtitle.object"
    $subtitleHash = (Get-FileHash -LiteralPath $subtitlePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedSubtitleHash = "f8063841c273854ba5b2f150ba29958bec0da1256653b4ba3bccd84c95d87fdc"
    if ($subtitleHash -ne $expectedSubtitleHash) {
        throw "The packaged subtitle template does not match the Bridge V1 hash."
    }
    $pluginManifest = [ordered]@{
        schemaVersion = "1.0"
        packageId = "gomi1124.AviUtl2MCP"
        version = $Version
        architecture = "x64"
        subtitleTemplateSha256 = $subtitleHash
        files = Get-FileManifest -Root $pluginDirectory
    }
    Write-Utf8NoBom `
        -Path (Join-Path $pluginDirectory "manifest.json") `
        -Value (($pluginManifest | ConvertTo-Json -Depth 8) + "`r`n")
    Write-Utf8NoBom `
        -Path (Join-Path $pluginStage "package.ini") `
        -Value ("[package]`r`nid=gomi1124.AviUtl2MCP`r`nname=AviUtl2 MCP Bridge`r`ninformation=AviUtl2 MCP Bridge v$Version by gomi1124`r`nuninstallSubFolderFile=1`r`n")
    Write-Utf8NoBom `
        -Path (Join-Path $pluginStage "package.txt") `
        -Value ("AviUtl2 MCP Bridge v$Version`r`n`r`nAviUtl2のプレビュー画面へこのpackageをD&Dして導入してください。`r`nMCP serverは別配布のzipを展開し、MCP clientへ絶対pathを設定します。`r`n")

    Copy-DirectoryContents -Source $publishStage -Destination $serverStage
    Copy-RequiredFile `
        -Source (Join-Path $repositoryRoot "schemas\mcp\v1\catalog.json") `
        -Destination (Join-Path $serverStage "schemas\mcp\v1\catalog.json")
    Copy-RequiredFile `
        -Source (Join-Path $repositoryRoot "examples\mcp-config.example.json") `
        -Destination (Join-Path $serverStage "mcp-config.example.json")
    Copy-RequiredFile `
        -Source (Join-Path $repositoryRoot "docs\install.md") `
        -Destination (Join-Path $serverStage "INSTALL.md")
    Copy-RequiredFile `
        -Source (Join-Path $repositoryRoot "README.md") `
        -Destination (Join-Path $serverStage "README.md")
    Copy-RequiredFile `
        -Source (Join-Path $repositoryRoot "LICENSE") `
        -Destination (Join-Path $serverStage "LICENSE.txt")
    Copy-RequiredFile `
        -Source (Join-Path $repositoryRoot "THIRD_PARTY_NOTICES.md") `
        -Destination (Join-Path $serverStage "THIRD_PARTY_NOTICES.md")

    $bridgeArchive = Join-Path $resolvedOutput "AviUtl2MCP-Bridge-v$Version.au2pkg.zip"
    $serverArchive = Join-Path $resolvedOutput "AviUtl2MCP-Server-win-x64-v$Version.zip"
    New-DeterministicZip -SourceDirectory $pluginStage -DestinationPath $bridgeArchive
    New-DeterministicZip -SourceDirectory $serverStage -DestinationPath $serverArchive
    $releaseFiles = @($bridgeArchive, $serverArchive) | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [ordered]@{
            name = $item.Name
            byteLength = $item.Length
            sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $releaseManifest = [ordered]@{
        schemaVersion = "1.0"
        version = $Version
        configuration = $Configuration
        runtime = "win-x64"
        files = $releaseFiles
    }
    $releaseManifestPath = Join-Path $resolvedOutput "release-manifest.json"
    Write-Utf8NoBom `
        -Path $releaseManifestPath `
        -Value (($releaseManifest | ConvertTo-Json -Depth 6) + "`r`n")
    $checksumLines = @($releaseFiles | ForEach-Object { "$($_.sha256)  $($_.name)" })
    Write-Utf8NoBom `
        -Path (Join-Path $resolvedOutput "checksums.sha256") `
        -Value (($checksumLines -join "`r`n") + "`r`n")
    Write-Output "Bridge package: $bridgeArchive"
    Write-Output "Server package: $serverArchive"
    Write-Output "Release manifest: $releaseManifestPath"
}
finally {
    $normalizedParent = [IO.Path]::GetFullPath($stagingParent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar)
    $normalizedStaging = [IO.Path]::GetFullPath($stagingRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar)
    $expectedStaging = Join-Path $normalizedParent $stagingId
    if (-not [string]::Equals(
            $normalizedStaging,
            $expectedStaging,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an unowned package staging directory."
    }
    if (Test-Path -LiteralPath $normalizedStaging) {
        Remove-Item -LiteralPath $normalizedStaging -Recurse -Force
    }
}
