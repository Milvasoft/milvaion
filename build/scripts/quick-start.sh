#!/bin/bash

# Milvaion Quick Start Script
# This script starts all required services and runs a test job

set -e

echo "[START] Starting Milvaion Distributed Job Scheduler..."
echo ""

# Step 1: Start Docker services
echo "[DOCKER] Starting Docker services (PostgreSQL, Redis, RabbitMQ, API, Worker)..."
docker compose up -d
echo "[OK] Services started!"
echo ""

# Step 2: Wait for services to be healthy
echo "[WAIT] Waiting for services to be healthy..."
sleep 15

# Check health
echo "[CHECK] Checking service health..."
docker compose ps

echo ""
echo "[INFO] Service URLs:"
echo "  - API:              http://localhost:5000"
echo "  - API Docs:         http://localhost:5000/api/documentation"
echo "  - RabbitMQ Admin:   http://localhost:15672 (guest/guest)"
echo "  - Login:            rootuser / value of MILVAION_ROOT_PASSWORD (default: admin)"
echo "  - Health Check:     http://localhost:5000/api/v1/healthcheck/ready"
echo ""

# The API applies its own migrations on startup, and creating a job needs a signed-in user, so create the
# first job from the UI.

# Show logs. Ctrl+C only stops following; the services keep running.
echo "[LOGS] Viewing logs (Ctrl+C to stop following, 'docker compose down' to stop the services)..."
echo "------------------------------------------------"
docker compose logs -f --tail=20

