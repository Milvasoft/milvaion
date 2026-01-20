#!/bin/bash

# Milvaion Quick Start Script
# This script starts all required services and runs a test job

set -e

echo "[START] Starting Milvaion Distributed Job Scheduler..."
echo ""

# Step 1: Start Docker services
echo "[DOCKER] Starting Docker services (PostgreSQL, Redis, RabbitMQ, API, Worker)..."
docker-compose up -d
echo "[OK] Services started!"
echo ""

# Step 2: Wait for services to be healthy
echo "[WAIT] Waiting for services to be healthy..."
sleep 15

# Check health
echo "[CHECK] Checking service health..."
docker-compose ps

echo ""
echo "[INFO] Service URLs:"
echo "  - API:              http://localhost:5000"
echo "  - Swagger:          http://localhost:5000/swagger"
echo "  - RabbitMQ Admin:   http://localhost:15672 (guest/guest)"
echo "  - Health Check:     http://localhost:5000/api/v1/healthcheck/ready"
echo ""

# Step 3: Run migrations (if needed)
echo "[DB] Running database migrations..."
if [ -f "src/Milvaion.Api/Migrations/002_CreateJobOccurrencesTable.sql" ]; then
    docker exec -i milvaion-postgres psql -U postgres -d MilvaionDb < src/Milvaion.Api/Migrations/002_CreateJobOccurrencesTable.sql 2>/dev/null || echo "Migration already applied or failed (this is OK if table exists)"
fi
echo ""

# Step 4: Wait a bit more for API to be ready
echo "[WAIT] Waiting for API to be ready..."
sleep 5

# Step 5: Schedule a test job
echo "[JOB] Scheduling a test job..."
EXECUTE_AT=$(date -u -d "+1 minute" +"%Y-%m-%dT%H:%M:%SZ")

RESPONSE=$(curl -s -X POST http://localhost:5000/api/v1/jobs/job \
-H "Content-Type: application/json" \
-d "{
  \"displayName\": \"Quick Start Test Job\",
  \"jobType\": \"TestJob\",
  \"executeAt\": \"$EXECUTE_AT\",
  \"isActive\": true
}" 2>/dev/null || echo '{"error": "API not ready"}')

echo "$RESPONSE" | jq '.' 2>/dev/null || echo "$RESPONSE"
echo ""

# Step 6: Show logs
echo "[LOGS] Viewing logs (Ctrl+C to exit)..."
echo "------------------------------------------------"
docker-compose logs -f --tail=20

# Cleanup function
cleanup() {
    echo ""
    echo "[STOP] Stopping services..."
    docker-compose down
    echo "[OK] Services stopped"
}

trap cleanup EXIT
