# ?? Quick Start - Docker Hub Push

Complete guide to building and pushing Milvaion images to Docker Hub.

## 1?? Login to Docker Hub

```bash
docker login
```

Enter your Docker Hub credentials:
- **Username:** `milvasoft`
- **Password:** Your Docker Hub password or access token

## 2?? Build and Push

### Option A: Build Everything (API + Worker)

**Windows:**
```cmd
cd build
.\build-all.ps1 -Registry "milvasoft" -Tag "1.0.0"
```

**Linux/macOS:**
```bash
./build/build-all.sh -r milvasoft -t 1.0.0
```

### Option B: Build API Only

**Windows:**
```cmd
.\build\build-api.ps1 -Registry "milvasoft" -Tag "1.0.0"
```

**Linux/macOS:**
```bash
./build/build-api.sh -r milvasoft -t 1.0.0
```

### Option C: Build Worker Only

**Windows:**
```cmd
.\build\build-worker.ps1 -Registry "milvasoft" -Tag "1.0.0"
```

**Linux/macOS:**
```bash
./build/build-worker.sh -r milvasoft -t 1.0.0
```

## 3?? Verify on Docker Hub

### API Image
Visit: https://hub.docker.com/r/milvasoft/milvaion-api/tags

You should see:
- `milvasoft/milvaion-api:1.0.0`
- `milvasoft/milvaion-api:latest`

### Worker Image
Visit: https://hub.docker.com/r/milvasoft/milvaion-sampleworker/tags

You should see:
- `milvasoft/milvaion-sampleworker:1.0.0`
- `milvasoft/milvaion-sampleworker:latest`

## 4?? Test Pull

```bash
# Pull API
docker pull milvasoft/milvaion-api:1.0.0

# Pull Worker
docker pull milvasoft/milvaion-sampleworker:1.0.0
```

## ?? Common Commands

### Build Commands

```bash
# Build all with latest tag
./build/build-all.sh -r milvasoft

# Build all with specific tag
./build/build-all.sh -r milvasoft -t 1.0.0

# Build without pushing (local testing)
./build/build-all.sh -r milvasoft -t test --skip-push

# Build only API
./build/build-all.sh -r milvasoft -t 1.0.0 --skip-worker

# Build only Worker
./build/build-all.sh -r milvasoft -t 1.0.0 --skip-api

# Build with git commit hash
./build/build-all.sh -r milvasoft -t dev-$(git rev-parse --short HEAD)
```

### Docker Commands

```bash
# View local images
docker images | grep milvaion

# Remove old images
docker rmi milvasoft/milvaion-api:old-tag
docker rmi milvasoft/milvaion-sampleworker:old-tag

# View image details
docker inspect milvasoft/milvaion-api:1.0.0

# Check image size
docker images milvasoft/milvaion-api:1.0.0 --format "{{.Size}}"
```

## ?? Deploy After Push

### Deploy with Docker Compose

Create `docker-compose.yml`:

```yaml
version: '3.8'

services:
  milvaion-api:
    image: milvasoft/milvaion-api:1.0.0
    ports:
      - "5000:5000"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
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
      replicas: 4

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
      - "15672:15672"
```

Run:
```bash
docker-compose up -d
```

### Deploy with Docker Run

```bash
# Start infrastructure
docker network create milvaion-net
docker run -d --name postgres --network milvaion-net postgres:16-alpine
docker run -d --name redis --network milvaion-net redis:7-alpine
docker run -d --name rabbitmq --network milvaion-net -p 15672:15672 rabbitmq:3-management-alpine

# Start API
docker run -d \
  --name milvaion-api \
  --network milvaion-net \
  -p 5000:5000 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  milvasoft/milvaion-api:1.0.0

# Start Workers (scale to 4)
for i in {1..4}; do
  docker run -d \
    --name worker-$i \
    --network milvaion-net \
    -e Worker__WorkerId=worker-$i \
    milvasoft/milvaion-sampleworker:1.0.0
done
```

### Scale Workers

```bash
# With Docker Compose
docker-compose up --scale milvaion-sampleworker=8 -d

# With Docker Swarm
docker service scale milvaion-sampleworker=8
```

## ?? Using Access Token (Recommended)

### Create Token
1. Go to: https://hub.docker.com/settings/security
2. Click "New Access Token"
3. Give it a name (e.g., "milvaion-ci")
4. Copy the token

### Login with Token
```bash
echo "YOUR_TOKEN" | docker login -u milvasoft --password-stdin
```

### Store Token Securely
```bash
# Linux/macOS
echo "YOUR_TOKEN" > ~/.docker-token
chmod 600 ~/.docker-token
cat ~/.docker-token | docker login -u milvasoft --password-stdin

# Windows (PowerShell)
$token = "YOUR_TOKEN"
$token | docker login -u milvasoft --password-stdin
```

## ? One-Liner Commands

### Linux/macOS

```bash
# Build and push everything
docker login && ./build/build-all.sh -r milvasoft -t 1.0.0

# Build, push, and deploy
docker login && \
  ./build/build-all.sh -r milvasoft -t 1.0.0 && \
  docker-compose up -d

# Quick test build (no push)
./build/build-all.sh -r localhost:5000 -t test --skip-push
```

### Windows PowerShell

```powershell
# Build and push everything
docker login; .\build\build-all.ps1 -Registry "milvasoft" -Tag "1.0.0"

# Build, push, and deploy
docker login; `
  .\build\build-all.ps1 -Registry "milvasoft" -Tag "1.0.0"; `
  docker-compose up -d
```

## ?? Troubleshooting

### "denied: requested access to the resource is denied"

```bash
# Check you're logged in
docker info | grep Username

# Re-login
docker logout
docker login -u milvasoft
```

### Build Fails

```bash
# Check Docker is running
docker ps

# Check Dockerfiles exist
ls src/Milvaion.Api/Dockerfile
ls src/Workers/SampleWorker/Dockerfile

# Check disk space
df -h  # Linux/macOS
Get-PSDrive C  # Windows PowerShell
```

### Push is Slow

Docker Hub free tier has rate limits. Solutions:

1. **Use Docker Hub Pro** for faster uploads
2. **Build without push** for local testing:
   ```bash
   ./build/build-all.sh -r milvasoft -t test --skip-push
   ```
3. **Use GitHub Container Registry** (faster for GitHub Actions):
   ```bash
   ./build/build-all.sh -r ghcr.io/milvasoft -t 1.0.0
   ```

### Image Too Large

```bash
# Check current size
docker images | grep milvaion

# Expected sizes (compressed):
# - milvaion-api: ~250-300 MB
# - milvaion-sampleworker: ~150-200 MB

# Clean up build cache
docker builder prune -a

# Remove unused images
docker image prune -a
```

### Cannot Connect to Registry

```bash
# Test Docker Hub connectivity
docker pull hello-world

# Check internet connection
ping hub.docker.com

# Check proxy settings
echo $HTTP_PROXY
echo $HTTPS_PROXY

# Bypass proxy (if needed)
unset HTTP_PROXY HTTPS_PROXY
```

## ?? Expected Results

After successful build and push:

### Docker Hub Images

- **API:** https://hub.docker.com/r/milvasoft/milvaion-api
  - Tags: `latest`, `1.0.0`
  - Size: ~250-300 MB
  - Pulls: Public

- **Worker:** https://hub.docker.com/r/milvasoft/milvaion-sampleworker
  - Tags: `latest`, `1.0.0`
  - Size: ~150-200 MB
  - Pulls: Public

### Local Images

```bash
$ docker images | grep milvasoft
milvasoft/milvaion-api      1.0.0    abc123def456   2 minutes ago   287MB
milvasoft/milvaion-api      latest   abc123def456   2 minutes ago   287MB
milvasoft/milvaion-sampleworker   1.0.0    def456abc789   5 minutes ago   178MB
milvasoft/milvaion-sampleworker   latest   def456abc789   5 minutes ago   178MB
```

## ?? Next Steps

1. **Test the images:**
   ```bash
   docker pull milvasoft/milvaion-api:1.0.0
   docker run -p 5000:5000 milvasoft/milvaion-api:1.0.0
   ```

2. **Deploy to production:**
   ```bash
   docker-compose -f docker-compose.prod.yml up -d
   ```

3. **Set up CI/CD:**
   - Configure GitHub Actions
   - Use secrets for Docker Hub credentials
   - Automate builds on git tags

4. **Monitor deployments:**
   ```bash
   docker ps
   docker logs milvaion-api
   docker stats
   ```

## ?? Links

- **Docker Hub - API:** https://hub.docker.com/r/milvasoft/milvaion-api
- **Docker Hub - Worker:** https://hub.docker.com/r/milvasoft/milvaion-sampleworker
- **Milvaion Docs:** https://github.com/milvasoft/milvaion-api
- **Docker Docs:** https://docs.docker.com/
- **Docker Hub Docs:** https://docs.docker.com/docker-hub/
