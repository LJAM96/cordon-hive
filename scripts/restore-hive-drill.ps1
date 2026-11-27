<#
.SYNOPSIS
    Executes or simulates a Cordon Hive backup restore drill with SHA-256 verification and RPO/RTO reporting.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BackupDir,
    [string]$PgHost = 'localhost',
    [int]$PgPort = 5432,
    [string]$PgDatabase = 'cordon_hive',
    [string]$PgUser = 'hive',
    [Parameter(Mandatory = $true)][string]$ArtifactRoot,
    [string]$HiveApiBaseUrl = 'https://localhost:5001',
    [string]$AdminBearerToken = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$rtoStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

if ([string]::IsNullOrWhiteSpace($BackupDir) -or -not (Test-Path -LiteralPath $BackupDir -PathType Container)) {
    throw "BackupDir does not exist: $BackupDir"
}

$resolvedBackupDir = (Resolve-Path -LiteralPath $BackupDir).Path
$manifestPath = Join-Path $resolvedBackupDir 'backup-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Backup manifest not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($manifest.createdUtc)) {
    throw 'Backup manifest is missing createdUtc.'
}

$createdUtc = if ($manifest.createdUtc -is [DateTimeOffset]) {
    $manifest.createdUtc
}
elseif ($manifest.createdUtc -is [DateTime]) {
    [DateTimeOffset]::new($manifest.createdUtc.ToUniversalTime(), [TimeSpan]::Zero)
}
else {
    [DateTimeOffset]::Parse([string]$manifest.createdUtc, [System.Globalization.CultureInfo]::InvariantCulture)
}
$rpoAge = [DateTimeOffset]::UtcNow - $createdUtc

# Read PostgreSQL password from environment only (never hardcoded).
$pgPasswordPresent = -not [string]::IsNullOrWhiteSpace($env:PGPASSWORD)

# Verify every artifact file's SHA-256 before restoring.
$artifactsBackupDir = Join-Path $resolvedBackupDir 'artifacts'
$verifiedCount = 0

foreach ($item in @($manifest.artifacts)) {
    $relPath = [string]$item.relativePath
    if ([string]::IsNullOrWhiteSpace($relPath) -or
        [System.IO.Path]::IsPathRooted($relPath) -or
        $relPath.Split(@('/', '\')) -contains '..') {
        throw "Unsafe relativePath in backup manifest: $relPath"
    }

    $candidatePath = Join-Path $artifactsBackupDir $relPath
    if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
        if ($DryRun -and $manifest.dryRun -and -not [string]::IsNullOrWhiteSpace($manifest.sourceArtifactRoot)) {
            $candidatePath = Join-Path ([string]$manifest.sourceArtifactRoot) $relPath
        }
    }

    if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
        throw "Backup artifact file missing for checksum verification: $relPath"
    }

    $actualHash = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = ([string]$item.sha256).ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "SHA-256 mismatch for '$relPath': expected '$expectedHash', actual '$actualHash'."
    }

    $verifiedCount++
}

$dumpFileName = if ($manifest.databaseBackup -and $manifest.databaseBackup.dumpFile) {
    [string]$manifest.databaseBackup.dumpFile
}
else {
    "$PgDatabase.dump"
}
$dumpFilePath = Join-Path $resolvedBackupDir $dumpFileName

if (-not $DryRun) {
    if (-not (Test-Path -LiteralPath $dumpFilePath -PathType Leaf)) {
        throw "Database dump file missing: $dumpFilePath"
    }

    if ($manifest.databaseBackup -and -not [string]::IsNullOrWhiteSpace($manifest.databaseBackup.dumpSha256)) {
        $actualDumpHash = (Get-FileHash -LiteralPath $dumpFilePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $expectedDumpHash = ([string]$manifest.databaseBackup.dumpSha256).ToLowerInvariant()
        if ($actualDumpHash -ne $expectedDumpHash) {
            throw "Database dump SHA-256 mismatch: expected '$expectedDumpHash', actual '$actualDumpHash'."
        }
    }

    $pgRestoreCmd = Get-Command 'pg_restore' -ErrorAction SilentlyContinue
    if ($null -eq $pgRestoreCmd) {
        throw 'pg_restore executable not found on PATH.'
    }

    $pgRestoreArgs = @(
        '--clean',
        '--if-exists',
        '--no-owner',
        "--host=$PgHost",
        "--port=$PgPort",
        "--username=$PgUser",
        "--dbname=$PgDatabase",
        $dumpFilePath
    )

    & $pgRestoreCmd.Source @pgRestoreArgs
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore failed with exit code $LASTEXITCODE."
    }

    New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null
    foreach ($item in @($manifest.artifacts)) {
        $relPath = [string]$item.relativePath
        $srcFile = Join-Path $artifactsBackupDir $relPath
        $dstFile = Join-Path $ArtifactRoot $relPath
        $dstParent = Split-Path -Path $dstFile -Parent
        if (-not [string]::IsNullOrWhiteSpace($dstParent)) {
            New-Item -ItemType Directory -Path $dstParent -Force | Out-Null
        }
        Copy-Item -LiteralPath $srcFile -Destination $dstFile -Force
    }
}
else {
    Write-Host "[DryRun] Verified $verifiedCount artifact checksum(s). Simulated pg_restore for '$dumpFileName' -> '$PgDatabase' (PGPASSWORD env set: $pgPasswordPresent) and artifact restore to '$ArtifactRoot'."
}

$trimmedBaseUrl = $HiveApiBaseUrl.TrimEnd('/')
$emergencyStopUri = "$trimmedBaseUrl/api/v1/operations/emergency-stop"
$readyUri = "$trimmedBaseUrl/health/ready"
$emergencyStopPayload = @{
    pauseDeployments      = $true
    invalidateActiveLeases = $true
    reason                = 'Post-restore drill lease invalidation'
} | ConvertTo-Json -Compress

if ($DryRun) {
    Write-Host "[DryRun] Skipping network call POST $emergencyStopUri with payload $emergencyStopPayload"
    Write-Host "[DryRun] Skipping network call GET $readyUri"
}
else {
    $effectiveToken = if (-not [string]::IsNullOrWhiteSpace($AdminBearerToken)) {
        $AdminBearerToken
    }
    else {
        $env:HIVE_ADMIN_BEARER_TOKEN
    }

    if ([string]::IsNullOrWhiteSpace($effectiveToken)) {
        throw 'AdminBearerToken or HIVE_ADMIN_BEARER_TOKEN environment variable is required for post-restore emergency-stop.'
    }

    $headers = @{
        Authorization = "Bearer $effectiveToken"
    }

    $stopResp = Invoke-RestMethod -Method Post -Uri $emergencyStopUri -Headers $headers -ContentType 'application/json' -Body $emergencyStopPayload
    if (-not $stopResp.deploymentsPaused) {
        throw 'Emergency stop response did not confirm deploymentsPaused=true.'
    }

    $readyResp = Invoke-RestMethod -Method Get -Uri $readyUri
    Write-Host "Readiness check succeeded at $readyUri ($($readyResp | ConvertTo-Json -Compress))."
}

$rtoStopwatch.Stop()

Write-Host "=== Cordon Hive Restore Drill Verification ==="
Write-Host ("BackupCreatedUtc : {0}" -f $createdUtc.ToString('o'))
Write-Host ("RPO Age          : {0:N2} seconds" -f [Math]::Max(0.0, $rpoAge.TotalSeconds))
Write-Host ("RTO Elapsed      : {0:N3} seconds" -f $rtoStopwatch.Elapsed.TotalSeconds)
Write-Host ("ArtifactsVerified: {0}" -f $verifiedCount)
Write-Host ("DryRun           : {0}" -f [bool]$DryRun)
