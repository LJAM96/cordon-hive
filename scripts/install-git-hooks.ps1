<#
.SYNOPSIS
    Configures Git to use the version-controlled .githooks directory (pre-push local CI gate).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    git config core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to set core.hooksPath."
    }

    $configured = git config --get core.hooksPath
    Write-Host "Configured git core.hooksPath = $configured" -ForegroundColor Green
}
finally {
    Pop-Location
}
