# Milvaion Quick Start Script (PowerShell)
# This script starts all required services and runs a test job

Write-Host "[START] Starting Milvaion Distributed Job Scheduler..." -ForegroundColor Green
Write-Host ""

# Step 1: Start Docker services
Write-Host "[DOCKER] Starting Docker services (PostgreSQL, Redis, RabbitMQ, API, Worker)..." -ForegroundColor Yellow
docker compose up -d
Write-Host "[OK] Services started!" -ForegroundColor Green
Write-Host ""

# Step 2: Wait for services to be healthy
Write-Host "[WAIT] Waiting for services to be healthy..." -ForegroundColor Yellow
Start-Sleep -Seconds 15

# Check health
Write-Host "[CHECK] Checking service health..." -ForegroundColor Yellow
docker compose ps
Write-Host ""

Write-Host "[INFO] Service URLs:" -ForegroundColor Cyan
Write-Host "  - API:              http://localhost:5000" -ForegroundColor White
Write-Host "  - API Docs:         http://localhost:5000/api/documentation" -ForegroundColor White
Write-Host "  - RabbitMQ Admin:   http://localhost:15672 (guest/guest)" -ForegroundColor White
Write-Host "  - Login:            rootuser / value of MILVAION_ROOT_PASSWORD (default: admin)" -ForegroundColor White
Write-Host "  - Health Check:     http://localhost:5000/api/v1/healthcheck/ready" -ForegroundColor White
Write-Host ""

# The API applies its own migrations on startup, and creating a job needs a signed-in user, so create the
# first job from the UI.

# Show logs. Ctrl+C only stops following; the services keep running.
Write-Host "[LOGS] Viewing logs (Ctrl+C to stop following, 'docker compose down' to stop the services)..." -ForegroundColor Yellow
Write-Host "------------------------------------------------" -ForegroundColor Gray
docker compose logs -f --tail=20
