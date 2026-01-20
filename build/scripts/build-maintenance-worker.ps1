#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds and pushes Milvaion Maintenance Worker Docker image.

.PARAMETER Registry
    Docker registry URL (e.g., ghcr.io/milvasoft, milvasoft.azurecr.io)

.PARAMETER Tag
    Image tag (default: latest)

.PARAMETER SkipPush
    Build image but don't push to registry

.EXAMPLE
    .\build-maintenance-worker.ps1 -Registry "milvasoft" -Tag "1.0.0"

.EXAMPLE
    .\build-maintenance-worker.ps1 -Registry "ghcr.io/milvasoft" -Tag "latest" -SkipPush
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Registry,

    [Parameter(Mandatory = $false)]
    [string]$Tag = "latest",

    [Parameter(Mandatory = $false)]
    [switch]$SkipPush
)

$ErrorActionPreference = "Stop"

# Colors
function Write-Info { Write-Host "[INFO]  $args" -ForegroundColor Cyan }
function Write-Success { Write-Host "[OK] $args" -ForegroundColor Green }
function Write-Error { Write-Host "[ERROR] $args" -ForegroundColor Red }
function Write-Step { Write-Host "`n[STEP] $args" -ForegroundColor Yellow }

# Paths
$rootDir = Split-Path -Parent $PSScriptRoot
$dockerfile = Join-Path $rootDir "src/Workers/MilvaionMaintenanceWorker/Dockerfile"

Write-Host @"
===============================================================
        Milvaion Maintenance Worker Build Script
===============================================================
  Registry: $Registry
  Tag:      $Tag
  Push:     $(if ($SkipPush) { "Disabled" } else { "Enabled" })
===============================================================
"@ -ForegroundColor Magenta

# Validate Dockerfile exists
if (-not (Test-Path $dockerfile)) {
    Write-Error "Dockerfile not found: $dockerfile"
    exit 1
}

# Build Docker Image
Write-Step "Building Docker image..."
$imageName = "$Registry/milvaion-maintenance-worker:$Tag"

Write-Info "Building $imageName..."
Push-Location $rootDir
docker build -t $imageName -f $dockerfile .

if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker build failed"
    Pop-Location
    exit 1
}
Pop-Location
Write-Success "Image built successfully"

# Tag as latest
if ($Tag -ne "latest") {
    Write-Step "Tagging image as 'latest'..."
    docker tag $imageName "$Registry/milvaion-maintenance-worker:latest"
    Write-Success "Image tagged as 'latest'"
}

# Push to Registry
if (-not $SkipPush) {
    Write-Step "Pushing image to registry..."

    Write-Info "Pushing $imageName..."
    docker push $imageName

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker push failed"
        exit 1
    }

    if ($Tag -ne "latest") {
        Write-Info "Pushing $Registry/milvaion-maintenance-worker:latest..."
        docker push "$Registry/milvaion-maintenance-worker:latest"

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Docker push (latest) failed"
            exit 1
        }
    }

    Write-Success "Image pushed to registry"
} else {
    Write-Info "Push skipped"
}

# Summary
Write-Host @"

===============================================================
                       Build Complete!
===============================================================
  Docker Image: $imageName
===============================================================
"@ -ForegroundColor Green

Write-Host "`n[INFO] Next steps:" -ForegroundColor Cyan
Write-Host "  Deploy with Docker:" -ForegroundColor White
Write-Host "     docker run -d --network milvaion_network $imageName" -ForegroundColor Gray
Write-Host "`n  Or with Docker Compose:" -ForegroundColor White
Write-Host "     docker-compose up -d maintenance-worker" -ForegroundColor Gray


echo.
echo Press any key to exit...
pause >nul
