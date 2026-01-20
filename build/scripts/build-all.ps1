#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds and pushes both Milvaion API and Worker Docker images.

.PARAMETER Registry
    Docker registry URL (e.g., milvasoft, ghcr.io/milvasoft)

.PARAMETER Tag
    Image tag (default: latest)

.PARAMETER SkipPush
    Build images but don't push to registry

.PARAMETER SkipApi
    Skip building API image

.PARAMETER SkipWorker
    Skip building Worker image

.EXAMPLE
    .\build-all.ps1 -Registry "milvasoft" -Tag "1.0.0"

.EXAMPLE
    .\build-all.ps1 -Registry "milvasoft" -Tag "1.0.0" -SkipPush

.EXAMPLE
    .\build-all.ps1 -Registry "milvasoft" -Tag "1.0.0" -SkipWorker
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Registry,

    [Parameter(Mandatory = $false)]
    [string]$Tag = "latest",

    [Parameter(Mandatory = $false)]
    [switch]$SkipPush,

    [Parameter(Mandatory = $false)]
    [switch]$SkipApi,

    [Parameter(Mandatory = $false)]
    [switch]$SkipWorker
)

$ErrorActionPreference = "Stop"

Write-Host @"
===============================================================
              Milvaion Build All - Docker Hub
===============================================================
  Registry: $Registry
  Tag:      $Tag
  API:      $(if ($SkipApi) { "Skipped" } else { "Enabled" })
  Worker:   $(if ($SkipWorker) { "Skipped" } else { "Enabled" })
  Push:     $(if ($SkipPush) { "Disabled" } else { "Enabled" })
===============================================================
"@ -ForegroundColor Magenta

$scriptDir = $PSScriptRoot
$buildApiScript = Join-Path $scriptDir "build-api.ps1"
$buildWorkerScript = Join-Path $scriptDir "build-worker.ps1"

$totalSteps = 0
$currentStep = 0

if (-not $SkipApi) { $totalSteps++ }
if (-not $SkipWorker) { $totalSteps++ }

# Build API
if (-not $SkipApi) {
    $currentStep++
    Write-Host "`n[$currentStep/$totalSteps] Building Milvaion API..." -ForegroundColor Yellow

    $params = @{
        Registry = $Registry
        Tag = $Tag
    }

    if ($SkipPush) {
        $params.Add("SkipPush", $true)
    }

    & $buildApiScript @params

    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] API build failed!" -ForegroundColor Red
        exit 1
    }
}

# Build Worker
if (-not $SkipWorker) {
    $currentStep++
    Write-Host "`n[$currentStep/$totalSteps] Building Milvaion Worker..." -ForegroundColor Yellow

    $params = @{
        Registry = $Registry
        Tag = $Tag
    }

    if ($SkipPush) {
        $params.Add("SkipPush", $true)
    }

    & $buildWorkerScript @params

    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] Worker build failed!" -ForegroundColor Red
        exit 1
    }
}

# Summary
Write-Host @"

===============================================================
                   All Builds Complete!
===============================================================
$(if (-not $SkipApi) { "  [OK] API:    $Registry/milvaion-api:$Tag" })
$(if (-not $SkipWorker) { "  [OK] Worker: $Registry/milvaion-sampleworker:$Tag" })
===============================================================
"@ -ForegroundColor Green

if (-not $SkipPush) {
    Write-Host "`n[INFO] Images available on Docker Hub:" -ForegroundColor Cyan
    if (-not $SkipApi) {
        Write-Host "  docker pull $Registry/milvaion-api:$Tag" -ForegroundColor White
    }
    if (-not $SkipWorker) {
        Write-Host "  docker pull $Registry/milvaion-sampleworker:$Tag" -ForegroundColor White
    }
}

Write-Host "`n[INFO] Deploy with Docker Compose:" -ForegroundColor Cyan
Write-Host "  docker-compose up -d" -ForegroundColor White


echo.
echo Press any key to exit...
pause >nul
