<#
.SYNOPSIS
    Backs up Cordon Hive PostgreSQL database and artifact store with SHA-256 manifest verification.
#>
[CmdletBinding()]
param(
    [string]$PgHost = 'localhost',
    [int]$PgPort = 5432,
    [string]$PgDatabase = 'cordon_hive',
    [string]$PgUser = 'hive',
    [Parameter(Mandatory = $true)][string]$ArtifactRoot,
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PgHost) -or
    $PgPort -le 0 -or
    [string]::IsNullOrWhiteSpace($PgDatabase) -or
    [string]::IsNullOrWhiteSpace($PgUser) -or
    [string]::IsNullOrWhiteSpace($ArtifactRoot) -or
    [string]::IsNullOrWhiteSpace($OutputDir)) {
    throw 'All PostgreSQL, ArtifactRoot, and OutputDir parameters must be non-empty and valid.'
}

if (-not (Test-Path -LiteralPath $ArtifactRoot -PathType Container)) {
    throw "ArtifactRoot directory does not exist: $ArtifactRoot"
}

$resolvedArtifactRoot = (Resolve-Path -LiteralPath $ArtifactRoot).Path
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$resolvedOutputDir = (Resolve-Path -LiteralPath $OutputDir).Path

# Read PostgreSQL password from environment only (never hardcoded).
$pgPasswordPresent = -not [string]::IsNullOrWhiteSpace($env:PGPASSWORD)

$dbDumpFileName = "$PgDatabase.dump"
$dbDumpPath = Join-Path $resolvedOutputDir $dbDumpFileName
$pgDumpArgs = @(
    '--format=custom',
    "--host=$PgHost",
    "--port=$PgPort",
    "--username=$PgUser",
    "--file=$dbDumpPath",
    $PgDatabase
)

$dbDumpSha256 = $null
if ($DryRun) {
    Write-Host "[DryRun] Validated backup targets. Planned command: pg_dump $($pgDumpArgs -join ' ') (PGPASSWORD env set: $pgPasswordPresent)"
}
else {
    $pgDumpCmd = Get-Command 'pg_dump' -ErrorAction SilentlyContinue
    if ($null -eq $pgDumpCmd) {
        throw 'pg_dump executable not found on PATH.'
    }

    & $pgDumpCmd.Source @pgDumpArgs
    if ($LASTEXITCODE -ne 0) {
        throw "pg_dump failed with exit code $LASTEXITCODE."
    }

    $dbDumpSha256 = (Get-FileHash -LiteralPath $dbDumpPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

$artifactsBackupDir = Join-Path $resolvedOutputDir 'artifacts'
$sourceFiles = @(Get-ChildItem -LiteralPath $resolvedArtifactRoot -Recurse -File | Sort-Object FullName)
$artifactEntries = [System.Collections.Generic.List[object]]::new()

foreach ($file in $sourceFiles) {
    $relativePath = [System.IO.Path]::GetRelativePath($resolvedArtifactRoot, $file.FullName).Replace('\', '/')
    $targetToHash = $file.FullName

    if (-not $DryRun) {
        $destFile = Join-Path $artifactsBackupDir $relativePath
        $destParent = Split-Path -Path $destFile -Parent
        if (-not [string]::IsNullOrWhiteSpace($destParent)) {
            New-Item -ItemType Directory -Path $destParent -Force | Out-Null
        }

        Copy-Item -LiteralPath $file.FullName -Destination $destFile -Force
        $targetToHash = $destFile
    }

    $hash = (Get-FileHash -LiteralPath $targetToHash -Algorithm SHA256).Hash.ToLowerInvariant()
    $artifactEntries.Add([ordered]@{
        relativePath = $relativePath
        sha256       = $hash
        sizeBytes    = [long]$file.Length
    })
}

$manifest = [ordered]@{
    createdUtc         = [DateTimeOffset]::UtcNow.ToString('o')
    dryRun             = [bool]$DryRun
    database           = $PgDatabase
    databaseBackup     = [ordered]@{
        host       = $PgHost
        port       = $PgPort
        user       = $PgUser
        dumpFile   = $dbDumpFileName
        dumpSha256 = $dbDumpSha256
    }
    sourceArtifactRoot = $resolvedArtifactRoot
    artifactCount      = $artifactEntries.Count
    artifacts          = @($artifactEntries.ToArray())
}

$manifestPath = Join-Path $resolvedOutputDir 'backup-manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Backup manifest written to $manifestPath (DryRun=$([bool]$DryRun), Artifacts=$($artifactEntries.Count))."
