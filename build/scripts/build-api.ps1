#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds and pushes Milvaion Scheduler API Docker image.

.PARAMETER Registry
    Docker registry URL (e.g., ghcr.io/milvasoft, milvasoft.azurecr.io)

.PARAMETER Tag
    Image tag (default: latest)

.PARAMETER SkipPush
    Build image but don't push to registry

.EXAMPLE
    .\build-api.ps1 -Registry "ghcr.io/milvasoft" -Tag "1.0.0"
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
function Write-Info { Write-Host "ℹ️  $args" -ForegroundColor Cyan }
function Write-Success { Write-Host "✅ $args" -ForegroundColor Green }
function Write-Error { Write-Host "❌ $args" -ForegroundColor Red }
function Write-Step { Write-Host "`n🔹 $args" -ForegroundColor Yellow }

# Paths
$rootDir = Split-Path -Parent $PSScriptRoot
$dockerfile = Join-Path $rootDir "src/Milvaion.Api/Dockerfile"

Write-Host @"
╔═══════════════════════════════════════════════════════════╗
║           Milvaion Scheduler API Build Script            ║
╠═══════════════════════════════════════════════════════════╣
║  Registry: $Registry
║  Tag:      $Tag
║  Push:     $(if ($SkipPush) { "Disabled" } else { "Enabled" })
╚═══════════════════════════════════════════════════════════╝
"@ -ForegroundColor Magenta

# Build Docker Image
Write-Step "Building Docker image..."
$imageName = "$Registry/milvaion-api:$Tag"

Write-Info "Building $imageName..."
Push-Location $rootDir
docker build -t $imageName -f $dockerfile .

if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker build failed"
    exit 1
}
Pop-Location
Write-Success "Image built successfully"

# Tag as latest
if ($Tag -ne "latest") {
    Write-Step "Tagging image as 'latest'..."
    docker tag $imageName "$Registry/milvaion-api:latest"
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
        Write-Info "Pushing $Registry/milvaion-api:latest..."
        docker push "$Registry/milvaion-api:latest"

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

╔═══════════════════════════════════════════════════════════╗
║                    🎉 Build Complete!                     ║
╠═══════════════════════════════════════════════════════════╣
║  🐳 Docker Image: $imageName
╚═══════════════════════════════════════════════════════════╝
"@ -ForegroundColor Green

Write-Host "`n📋 Next steps:" -ForegroundColor Cyan
Write-Host "  Deploy with Docker:" -ForegroundColor White
Write-Host "     docker run -d -p 5000:5000 $imageName" -ForegroundColor Gray
Write-Host "`n  Or use Docker Compose:" -ForegroundColor White
Write-Host "     docker-compose up -d" -ForegroundColor Gray


echo.
echo Press any key to exit...
pause >nul
