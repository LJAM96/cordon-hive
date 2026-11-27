<#
.SYNOPSIS
    Packages Cordon Hive release artifacts and writes SHA256SUMS.txt and release-manifest.json.
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0-rc1',
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Version) -or $Version -match '[\\/:\*\?"<>\|]') {
    throw "Invalid release version: '$Version'"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    throw 'OutputDir must not be empty.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$resolvedOutputDir = (Resolve-Path -LiteralPath $OutputDir).Path

$targets = @(
    @{ Name = 'Hive.Server'; Project = 'src\Hive.Server\Hive.Server.csproj'; Runtime = 'linux-x64' },
    @{ Name = 'Hive.Agent'; Project = 'src\Hive.Agent\Hive.Agent.csproj'; Runtime = 'win-x64' },
    @{ Name = 'Hive.Installer'; Project = 'src\Hive.Installer\Hive.Installer.csproj'; Runtime = 'win-x64' },
    @{ Name = 'Hive.App'; Project = 'src\Hive.App\Hive.App.csproj'; Runtime = 'win-x64' }
)

foreach ($target in $targets) {
    $projPath = Join-Path $repoRoot $target.Project
    if (-not (Test-Path -LiteralPath $projPath -PathType Leaf)) {
        throw "Release target project not found: $projPath"
    }
}

$gitCommit = 'unknown'
if (Test-Path -LiteralPath (Join-Path $repoRoot '.git')) {
    $gitCmd = Get-Command 'git' -ErrorAction SilentlyContinue
    if ($null -ne $gitCmd) {
        $revOutput = & $gitCmd.Source -C $repoRoot rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($revOutput)) {
            $gitCommit = $revOutput.Trim()
        }
    }
}

$builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
$packagedFiles = [System.Collections.Generic.List[object]]::new()
$checksumLines = [System.Collections.Generic.List[string]]::new()

if ($DryRun) {
    foreach ($target in $targets) {
        $projPath = Join-Path $repoRoot $target.Project
        Write-Host "[DryRun] Planned publish: dotnet publish `"$projPath`" -c Release -r $($target.Runtime) -p:Version=$Version"
    }
    $checksumLines.Add("# DryRun release checksums for Cordon Hive $Version ($gitCommit)")
}
else {
    $stagingRoot = Join-Path $resolvedOutputDir '_staging'
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

    try {
        foreach ($target in $targets) {
            $projPath = Join-Path $repoRoot $target.Project
            $targetStage = Join-Path $stagingRoot "$($target.Name)-$($target.Runtime)"
            $publishArgs = @(
                'publish',
                $projPath,
                '-c', 'Release',
                '-r', $target.Runtime,
                '--self-contained', 'false',
                "-p:Version=$Version",
                '-o', $targetStage
            )

            & dotnet @publishArgs
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet publish failed for $($target.Name) with exit code $LASTEXITCODE."
            }

            $archiveName = "$($target.Name)-$Version-$($target.Runtime).zip"
            $archivePath = Join-Path $resolvedOutputDir $archiveName
            if (Test-Path -LiteralPath $archivePath) {
                Remove-Item -LiteralPath $archivePath -Force
            }

            Compress-Archive -Path (Join-Path $targetStage '*') -DestinationPath $archivePath -Force
            $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
            $sizeBytes = (Get-Item -LiteralPath $archivePath).Length

            $checksumLines.Add("$hash  $archiveName")
            $packagedFiles.Add([ordered]@{
                name      = $target.Name
                runtime   = $target.Runtime
                fileName  = $archiveName
                sha256    = $hash
                sizeBytes = [long]$sizeBytes
            })
        }
    }
    finally {
        if (Test-Path -LiteralPath $stagingRoot) {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$sumsPath = Join-Path $resolvedOutputDir 'SHA256SUMS.txt'
$checksumLines | Set-Content -LiteralPath $sumsPath -Encoding UTF8

$manifest = [ordered]@{
    version    = $Version
    gitCommit  = $gitCommit
    builtAtUtc = $builtAtUtc
    dryRun     = [bool]$DryRun
    targets    = @($targets | ForEach-Object { [ordered]@{ name = $_.Name; runtime = $_.Runtime; project = $_.Project } })
    files      = @($packagedFiles.ToArray())
}

$manifestPath = Join-Path $resolvedOutputDir 'release-manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Release packaging complete at $resolvedOutputDir (Version=$Version, DryRun=$([bool]$DryRun))."
