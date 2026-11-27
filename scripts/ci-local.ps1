<#
.SYNOPSIS
    Local Windows CI build, hygiene gate, and test runner for Cordon Hive.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solutionPath = Join-Path $repoRoot 'CordonHive.sln'
$winUiProject = Join-Path $repoRoot 'src\Hive.App\Hive.App.csproj'

$testProjects = @(
    'tests\Hive.Domain.Tests\Hive.Domain.Tests.csproj',
    'tests\Hive.Server.IntegrationTests\Hive.Server.IntegrationTests.csproj',
    'tests\Hive.Windows.Tests\Hive.Windows.Tests.csproj'
)

$summary = [System.Collections.Generic.List[pscustomobject]]::new()

function Invoke-HygieneGate {
    Write-Host ""
    Write-Host "=== Static Hygiene & Forensic Invariant Gate ===" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    $sourceFiles = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -File -Include '*.cs', '*.xaml', '*.sql' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

    foreach ($file in $sourceFiles) {
        $content = Get-Content -Path $file.FullName -Raw
        if ($content -match '(?im)(FROM|SELECT\s+[^\r\n]*\s+FROM)\s+Win32_Product\b') {
            throw "Forensic invariant violation: Win32_Product WMI query detected in $($file.FullName)"
        }
    }

    if (Test-Path (Join-Path $repoRoot '.git')) {
        $tracked = & git ls-files
        if ($LASTEXITCODE -eq 0) {
            foreach ($path in $tracked) {
                if ($path -match '(?i)(\.pfx|\.p12|\.key|\.pem|\.db|\.sqlite)$' -or
                    $path -match '(?i)(^|[\\/])\.env$' -or
                    $path -match '(?i)appsettings\.(Development|Local)\.json$') {
                    throw "Secret/runtime artifact hygiene violation: forbidden file tracked in git: $path"
                }
            }
        }
    }

    $sw.Stop()
    $summary.Add([pscustomobject]@{
        Step     = 'Static Hygiene & Invariant Gate'
        Status   = 'PASS'
        Duration = "{0:N2}s" -f $sw.Elapsed.TotalSeconds
    })
}

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet @Arguments
    $exitCode = $LASTEXITCODE
    $sw.Stop()

    $passed = ($exitCode -eq 0)
    $summary.Add([pscustomobject]@{
        Step     = $Name
        Status   = if ($passed) { 'PASS' } else { 'FAIL' }
        Duration = "{0:N2}s" -f $sw.Elapsed.TotalSeconds
    })

    if (-not $passed) {
        throw "Step '$Name' failed with exit code $exitCode."
    }
}

try {
    Push-Location $repoRoot

    if (-not (Test-Path $solutionPath)) {
        throw "Solution file not found: $solutionPath"
    }

    Invoke-HygieneGate

    Invoke-Step -Name 'Restore Solution' -Arguments @('restore', $solutionPath)

    $nonWinUiProjects = Get-ChildItem -Path (Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'tests') -Filter '*.csproj' -Recurse |
        Where-Object { $_.Name -ne 'Hive.App.csproj' } |
        Sort-Object FullName

    foreach ($proj in $nonWinUiProjects) {
        $relPath = $proj.FullName.Substring($repoRoot.Length).TrimStart('\', '/')
        Invoke-Step -Name "Build $relPath (Debug)" -Arguments @('build', $proj.FullName, '-c', 'Debug', '-p:TreatWarningsAsErrors=true')
    }

    if (-not (Test-Path $winUiProject)) {
        throw "WinUI project not found: $winUiProject"
    }
    Invoke-Step -Name 'Build src\Hive.App\Hive.App.csproj (Debug|x64)' -Arguments @('build', $winUiProject, '-c', 'Debug', '-p:Platform=x64', '-p:TreatWarningsAsErrors=true')

    foreach ($relTest in $testProjects) {
        $fullTest = Join-Path $repoRoot $relTest
        if (-not (Test-Path $fullTest)) {
            throw "Required test project not found: $fullTest"
        }

        Invoke-Step -Name "Test $relTest" -Arguments @('test', $fullTest, '-c', 'Debug', '--no-build')
    }

    Write-Host ""
    Write-Host "=== Cordon Hive Local CI Summary ===" -ForegroundColor Green
    $summary | Format-Table -AutoSize | Out-String | Write-Host
    exit 0
}
catch {
    Write-Host ""
    Write-Host "=== Cordon Hive Local CI FAILED ===" -ForegroundColor Red
    Write-Error $_.Exception.Message
    if ($summary.Count -gt 0) {
        $summary | Format-Table -AutoSize | Out-String | Write-Host
    }
    exit 1
}
finally {
    Pop-Location
}
