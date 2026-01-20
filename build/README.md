# Milvaion Docker Build Scripts

Scripts for building and pushing Milvaion Docker images to Docker Hub.

```cmd
build-all.bat -Registry milvasoft -Tag 1.0.0

build-all.bat -Registry milvasoft -Tag 1.0.0 -SkipPush

build-all.bat -Registry milvasoft -Tag 1.0.0 -SkipWorker

build-all.bat -Registry milvasoft -Tag 1.0.0 -SkipApi

build-api.bat -Registry milvasoft -Tag 1.0.0

build-worker.bat -Registry milvasoft -Tag 1.0.0 -SkipPush
```

## ?? Files

### Individual Builds
- `build-api.ps1` / `build-api.sh` - Build Milvaion API
- `build-worker.ps1` / `build-worker.sh` - Build Sample Worker
- `build-api.bat` / `build-worker.bat` - Windows quick launchers

### Combined Build
- `build-all.ps1` / `build-all.sh` - Build both API and Worker

### Documentation
- `README.md` - This file
- `QUICKSTART.md` - Quick start guide

## ?? Quick Start

### Build Everything (API + Worker)

**Windows:**
```cmd
cd build
build-all.ps1 -Registry "milvasoft" -Tag "1.0.0"
```

**Linux/macOS:**
```bash
chmod +x build/*.sh
./build/build-all.sh -r milvasoft -t 1.0.0
```

### Build API Only

**Windows:**
```powershell
.\build\build-api.ps1 -Registry "milvasoft" -Tag "1.0.0"
```

**Linux/macOS:**
```bash
./build/build-api.sh -r milvasoft -t 1.0.0
```

### Build Worker Only

**Windows:**
```powershell
.\build\build-worker.ps1 -Registry "milvasoft" -Tag "1.0.0"
```

**Linux/macOS:**
```bash
./build/build-worker.sh -r milvasoft -t 1.0.0
```

## ?? What Gets Built

### Milvaion API (`milvaion-api`)
- ASP.NET Core API
- React SPA (embedded)
- Job scheduling engine
- Worker management
- PostgreSQL, Redis, RabbitMQ integration

**Docker Image:** `milvasoft/milvaion-api:TAG`

### Sample Worker (`milvaion-sampleworker`)
- .NET Console Worker
- Job execution engine
- RabbitMQ consumer
- Redis integration for cancellation
- Offline resilience (outbox pattern)

**Docker Image:** `milvasoft/milvaion-sampleworker:TAG`

## ?? Registry Authentication

### Docker Hub (Recommended for Production)

```bash
# Login
docker login

# Or with username
docker login -u milvasoft

# Or with access token
echo $DOCKER_TOKEN | docker login -u milvasoft --password-stdin
```

### GitHub Container Registry

```bash
echo $GITHUB_TOKEN | docker login ghcr.io -u USERNAME --password-stdin
```

### Azure Container Registry

```bash
az acr login --name yourregistry
```

## ?? Parameters

### PowerShell Scripts

| Parameter | Required | Default | Description |
|-----------|----------|---------|-------------|
| `-Registry` | Yes | - | Docker registry (e.g., `milvasoft`, `ghcr.io/milvasoft`) |
| `-Tag` | No | `latest` | Image tag |
| `-SkipPush` | No | `false` | Build but don't push |
| `-SkipApi` | No | `false` | Skip API (build-all only) |
| `-SkipWorker` | No | `false` | Skip Worker (build-all only) |

### Bash Scripts

| Parameter | Required | Default | Description |
|-----------|----------|---------|-------------|
| `-r`, `--registry` | Yes | - | Docker registry |
| `-t`, `--tag` | No | `latest` | Image tag |
| `--skip-push` | No | `false` | Build but don't push |
| `--skip-api` | No | `false` | Skip API (build-all only) |
| `--skip-worker` | No | `false` | Skip Worker (build-all only) |

## ?? Examples

### Production Release (Docker Hub)

```bash
# Login to Docker Hub
docker login -u milvasoft

# Build and push both images
./build/build-all.sh -r milvasoft -t 1.0.0

# Result:
# - milvasoft/milvaion-api:1.0.0
# - milvasoft/milvaion-api:latest
# - milvasoft/milvaion-sampleworker:1.0.0
# - milvasoft/milvaion-sampleworker:latest
```

### Development Build

```bash
# Build with git commit hash
./build/build-all.sh -r milvasoft -t dev-$(git rev-parse --short HEAD)
```

### Build Without Pushing

```bash
# Local testing
./build/build-all.sh -r localhost:5000 -t test --skip-push
```

### Build Only API

```bash
# Skip worker
./build/build-all.sh -r milvasoft -t 1.0.0 --skip-worker
```

### Build Only Worker

```bash
# Skip API
./build/build-all.sh -r milvasoft -t 1.0.0 --skip-api
```

### Different Registries

```bash
# Docker Hub
./build/build-all.sh -r milvasoft -t 1.0.0

# GitHub Container Registry
./build/build-all.sh -r ghcr.io/milvasoft -t 1.0.0

# Azure Container Registry
./build/build-all.sh -r milvasoft.azurecr.io -t 1.0.0
```

## ?? Deployment

### Pull Images from Docker Hub

```bash
docker pull milvasoft/milvaion-api:1.0.0
docker pull milvasoft/milvaion-sampleworker:1.0.0
```

### Deploy with Docker Compose

```yaml
version: '3.8'

services:
  milvaion-api:
    image: milvasoft/milvaion-api:1.0.0
    ports:
      - "5000:5000"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__PostgreSQL=Host=postgres;Database=milvaion;...
      - ConnectionStrings__Redis=redis:6379
      - ConnectionStrings__RabbitMQ=amqp://guest:guest@rabbitmq:5672
    depends_on:
      - postgres
      - redis
      - rabbitmq

  milvaion-sampleworker:
    image: milvasoft/milvaion-sampleworker:1.0.0
    environment:
      - Worker__WorkerId=worker-01
      - Worker__RabbitMQ__Host=rabbitmq
      - Worker__Redis__ConnectionString=redis:6379
    depends_on:
      - rabbitmq
      - redis
    deploy:
      replicas: 4  # Scale workers

  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_PASSWORD: yourpass
      POSTGRES_DB: milvaion

  redis:
    image: redis:7-alpine

  rabbitmq:
    image: rabbitmq:3-management-alpine
    ports:
      - "15672:15672"  # Management UI
```

### Deploy with Docker Run

```bash
# API
docker run -d \
  --name milvaion-api \
  -p 5000:5000 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  milvasoft/milvaion-api:1.0.0

# Worker
docker run -d \
  --name milvaion-sampleworker \
  -e Worker__WorkerId=worker-01 \
  -e Worker__RabbitMQ__Host=rabbitmq \
  -e Worker__Redis__ConnectionString=redis:6379 \
  milvasoft/milvaion-sampleworker:1.0.0
```

### Scale Workers

```bash
# With Docker Compose
docker-compose up --scale milvaion-sampleworker=8 -d

# Manually
for i in {1..4}; do
  docker run -d \
    --name worker-$i \
    -e Worker__WorkerId=worker-$i \
    milvasoft/milvaion-sampleworker:1.0.0
done
```

## ?? Troubleshooting

### Build Fails

```bash
# Check Docker is running
docker ps

# Check Dockerfiles exist
ls src/Milvaion.Api/Dockerfile
ls src/Workers/SampleWorker/Dockerfile

# Build manually
docker build -t test -f src/Milvaion.Api/Dockerfile .
```

### Push Permission Denied

```bash
# Check authentication
docker info | grep Username

# Re-login
docker logout
docker login -u milvasoft
```

### "denied: requested access to the resource is denied"

```bash
# Verify registry format
# Correct: milvasoft/milvaion-api:1.0.0
# Wrong: Milvasoft/milvaion-api:1.0.0 (uppercase not allowed in Docker Hub)

# Check you're logged in to correct registry
docker login
```

### Image Too Large

```bash
# Check sizes
docker images | grep milvaion

# Expected sizes (compressed):
# - milvaion-api: ~250-300 MB
# - milvaion-sampleworker: ~150-200 MB

# Clean up build cache
docker builder prune -a
```

### Cannot Connect to Registry

```bash
# Test connectivity
docker pull hello-world

# Check proxy settings
echo $HTTP_PROXY
echo $HTTPS_PROXY

# Verify DNS
nslookup hub.docker.com
```

## ?? Image Information

### Docker Hub Repositories

- **API:** https://hub.docker.com/r/milvasoft/milvaion-api
- **Worker:** https://hub.docker.com/r/milvasoft/milvaion-sampleworker

### Pull Commands

```bash
# Latest versions
docker pull milvasoft/milvaion-api:latest
docker pull milvasoft/milvaion-sampleworker:latest

# Specific versions
docker pull milvasoft/milvaion-api:1.0.0
docker pull milvasoft/milvaion-sampleworker:1.0.0
```

### Image Layers

Both images use multi-stage builds for optimization:
1. **Build Stage:** SDK image with source code
2. **Final Stage:** Runtime image with compiled binaries only

## ?? Security Notes

- **Never commit credentials** to version control
- Use **Docker Hub access tokens** instead of passwords
- Enable **2FA** on Docker Hub account
- Use **secrets management** in CI/CD
- Scan images for vulnerabilities: `docker scan milvasoft/milvaion-api:latest`
- Use specific tags in production (not `latest`)

## ?? CI/CD Integration

### GitHub Actions

```yaml
- name: Login to Docker Hub
  uses: docker/login-action@v3
  with:
    username: ${{ secrets.DOCKER_USERNAME }}
    password: ${{ secrets.DOCKER_TOKEN }}

- name: Build and Push
  run: |
    ./build/build-all.sh -r milvasoft -t ${{ github.ref_name }}
```

### Azure Pipelines

```yaml
- task: Docker@2
  inputs:
    command: login
    containerRegistry: DockerHub
    
- script: |
    ./build/build-all.sh -r milvasoft -t $(Build.BuildNumber)
```

## ?? Links

- [Docker Hub - milvaion-api](https://hub.docker.com/r/milvasoft/milvaion-api)
- [Docker Hub - milvaion-sampleworker](https://hub.docker.com/r/milvasoft/milvaion-sampleworker)
- [Milvaion Documentation](https://github.com/milvasoft/milvaion-api)
- [Docker Documentation](https://docs.docker.com/)
- [Docker Hub Documentation](https://docs.docker.com/docker-hub/)
