# Milvaion Quick Start Script (PowerShell)
# This script starts all required services and runs a test job

Write-Host "[START] Starting Milvaion Distributed Job Scheduler..." -ForegroundColor Green
Write-Host ""

# Step 1: Start Docker services
Write-Host "[DOCKER] Starting Docker services (PostgreSQL, Redis, RabbitMQ, API, Worker)..." -ForegroundColor Yellow
docker-compose up -d
Write-Host "[OK] Services started!" -ForegroundColor Green
Write-Host ""

# Step 2: Wait for services to be healthy
Write-Host "[WAIT] Waiting for services to be healthy..." -ForegroundColor Yellow
Start-Sleep -Seconds 15

# Check health
Write-Host "[CHECK] Checking service health..." -ForegroundColor Yellow
docker-compose ps
Write-Host ""

Write-Host "[INFO] Service URLs:" -ForegroundColor Cyan
Write-Host "  - API:              http://localhost:5000" -ForegroundColor White
Write-Host "  - Swagger:          http://localhost:5000/swagger" -ForegroundColor White
Write-Host "  - RabbitMQ Admin:   http://localhost:15672 (guest/guest)" -ForegroundColor White
Write-Host "  - Health Check:     http://localhost:5000/api/v1/healthcheck/ready" -ForegroundColor White
Write-Host ""

# Step 3: Run migrations
Write-Host "[DB] Running database migrations..." -ForegroundColor Yellow
if (Test-Path "src\Milvaion.Api\Migrations\002_CreateJobOccurrencesTable.sql") {
    Get-Content "src\Milvaion.Api\Migrations\002_CreateJobOccurrencesTable.sql" | docker exec -i milvaion-postgres psql -U postgres -d MilvaionDb 2>$null
    if ($?) {
        Write-Host "[OK] Migration applied" -ForegroundColor Green
    } else {
        Write-Host "[SKIP] Migration skipped (already applied or failed)" -ForegroundColor Yellow
    }
}
Write-Host ""

# Step 4: Wait for API
Write-Host "[WAIT] Waiting for API to be ready..." -ForegroundColor Yellow
Start-Sleep -Seconds 5

# Step 5: Schedule a test job
Write-Host "[JOB] Scheduling a test job..." -ForegroundColor Yellow
$executeAt = (Get-Date).AddMinutes(1).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

$body = @{
    displayName = "Quick Start Test Job"
    jobType = "TestJob"
    executeAt = $executeAt
    isActive = $true
} | ConvertTo-Json

try {
    $response = Invoke-RestMethod -Uri "http://localhost:5000/api/v1/jobs/job" `
        -Method Post `
        -ContentType "application/json" `
        -Body $body `
        -ErrorAction Stop

    Write-Host "[OK] Job scheduled successfully!" -ForegroundColor Green
    Write-Host "Job ID: $($response.data)" -ForegroundColor Cyan
    Write-Host "Execute At: $executeAt" -ForegroundColor Cyan
} catch {
    Write-Host "[WARN] Failed to schedule job: $($_.Exception.Message)" -ForegroundColor Yellow
}
Write-Host ""

# Step 6: Show logs
Write-Host "[LOGS] Viewing logs (Press Ctrl+C to exit)..." -ForegroundColor Yellow
Write-Host "------------------------------------------------" -ForegroundColor Gray
docker-compose logs -f --tail=20


echo.
echo Press any key to exit...
pause >nul
