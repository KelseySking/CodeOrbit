# CodeOrbit - publish artifacts
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDir = "release",
    [string]$DownloadUrl = ""
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$runtimeHostProject = Join-Path $projectRoot "src\CodeOrbit.RuntimeHost"
$bridgeProject = Join-Path $projectRoot "src\CodeOrbit.Bridge"
$runtimeHostPublish = Join-Path $runtimeHostProject "bin\$Configuration\net8.0\$Runtime\publish"
$bridgePublish = Join-Path $bridgeProject "bin\$Configuration\net8.0\$Runtime\publish"
$stagingDir = Join-Path $projectRoot ".runtime-staging"
$outputPath = Join-Path $projectRoot $OutputDir

function Get-FileSha256 {
    param([string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $hash = $sha256.ComputeHash($stream)
        return ($hash | ForEach-Object { $_.ToString("x2") }) -join ""
    }
    finally {
        $stream.Dispose()
    }
}

function Write-RuntimeManifest {
    param(
        [string]$RuntimeDir,
        [string]$RuntimeVersion
    )

    $manifest = [ordered]@{
        runtimeVersion = $RuntimeVersion
        contractVersion = "1"
        hostExe = "CodeOrbit.RuntimeHost.exe"
        bridgeExe = "CodeOrbit.Bridge.exe"
        defaultPort = 32145
        defaultHost = "127.0.0.1"
        defaultPipeName = $null
        defaultSettingsDir = $null
    }
    $manifest | ConvertTo-Json | Set-Content -Path (Join-Path $RuntimeDir "runtime-manifest.json") -Encoding UTF8
}

Write-Host "Publishing CodeOrbit.RuntimeHost ($Runtime)..." -ForegroundColor Cyan
dotnet publish $runtimeHostProject -c $Configuration -r $Runtime --self-contained -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publishing CodeOrbit.Bridge ($Runtime)..." -ForegroundColor Cyan
dotnet publish $bridgeProject -c $Configuration -r $Runtime --self-contained -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$runtimeHostExe = Join-Path $runtimeHostPublish "CodeOrbit.RuntimeHost.exe"
$bridgeExe = Join-Path $bridgePublish "CodeOrbit.Bridge.exe"
if (-not (Test-Path -LiteralPath $runtimeHostExe)) { throw "RuntimeHost executable not found: $runtimeHostExe" }
if (-not (Test-Path -LiteralPath $bridgeExe)) { throw "Bridge executable not found: $bridgeExe" }

if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir | Out-Null
Copy-Item -Path (Join-Path $runtimeHostPublish "*") -Destination $stagingDir -Recurse -Force
Copy-Item -Path (Join-Path $bridgePublish "*") -Destination $stagingDir -Recurse -Force

$versionInfo = (Get-Item -LiteralPath $runtimeHostExe).VersionInfo
$version = $versionInfo.ProductVersion
if (-not $version) { $version = $versionInfo.FileVersion }
if (-not $version) { $version = "0.0.0" }
Write-RuntimeManifest -RuntimeDir $stagingDir -RuntimeVersion $version

if (-not (Test-Path -LiteralPath $outputPath)) { New-Item -ItemType Directory -Path $outputPath | Out-Null }
$zipName = "CodeOrbit-$Runtime-v$version.zip"
$zipPath = Join-Path $outputPath $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $zipPath

$updateManifest = [ordered]@{
    runtimeVersion = $version
    contractVersion = "1"
    downloadUrl = $DownloadUrl
    sha256 = Get-FileSha256 -Path $zipPath
}
$manifestPath = Join-Path $outputPath "CodeOrbit-$Runtime-v$version.update.json"
$updateManifest | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding UTF8

Remove-Item -LiteralPath $stagingDir -Recurse -Force
Write-Host "ZIP created: $zipPath" -ForegroundColor Green
Write-Host "Update manifest created: $manifestPath" -ForegroundColor Green
