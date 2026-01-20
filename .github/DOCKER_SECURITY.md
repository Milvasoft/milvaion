# Docker Security Improvements

## Overview
This document outlines the security improvements made to Docker images to improve Docker Hub health scores.

## Changes Made

### 1. Non-Root User Implementation

All Docker images now run as a non-root user (`milvaion`) instead of root:

**Benefits:**
- ✅ Improved security posture
- ✅ Follows container security best practices
- ✅ Reduces attack surface
- ✅ Improves Docker Hub health score from D to higher grades

**Implementation:**
```dockerfile
# Create non-root user
RUN groupadd -r milvaion && useradd -r -g milvaion milvaion

# ... (after copying files)

# Change ownership to non-root user
RUN chown -R milvaion:milvaion /app

# Switch to non-root user
USER milvaion
```

**Affected Images:**
- milvaion-api
- milvaion-http-worker
- milvaion-sql-worker
- milvaion-email-worker
- milvaion-maintenance-worker
- milvaion-sample-worker

### 2. Supply Chain Attestation

All GitHub Actions workflows now include:
- **Provenance attestation**: `provenance: true`
- **SBOM (Software Bill of Materials)**: `sbom: true`

**Benefits:**
- ✅ Supply chain security
- ✅ Build verification
- ✅ Dependency transparency
- ✅ Compliance with security standards

**Implementation in GitHub Actions:**
```yaml
- name: Build and push Docker image
  uses: docker/build-push-action@v5
  with:
    provenance: true  # ✅ Added
    sbom: true        # ✅ Added
```

### 3. Additional Security Enhancements

**Milvaion API:**
- Added `curl` to base image for health checks
- Health check uses localhost endpoint

**All Workers:**
- Process-based health checks using `pgrep`
- Proper signal handling for graceful shutdown

## Docker Hub Health Score

### Before
- **Grade**: D
- **Issues**:
  - ❌ Missing supply chain attestation(s)
  - ❌ No default non-root user found

### After
- **Grade**: Expected A or B
- **Issues**: ✅ All resolved

## Migration Notes

### Running Containers

If you're running containers locally with volume mounts, you may need to adjust permissions:

```bash
# Option 1: Change ownership on host (Linux/macOS)
chown -R 1000:1000 /path/to/volume

# Option 2: Use anonymous volumes (data persists but no host binding)
docker run -v /app/data milvaion-api:latest

# Option 3: Named volumes (recommended)
docker volume create milvaion-data
docker run -v milvaion-data:/app/data milvaion-api:latest
```

### Docker Compose

The `docker-compose.yml` already uses named volumes, so no changes needed:
```yaml
volumes:
  postgres_data:
    driver: local
  redis_data:
    driver: local
```

### Kubernetes

If deploying to Kubernetes, ensure:
- `runAsNonRoot: true` in SecurityContext (already enforced by Dockerfile)
- PersistentVolumes have proper permissions

## Testing

To test the security improvements locally:

```bash
# Build image
docker build -t milvaion-api:test -f src/Milvaion.Api/Dockerfile .

# Verify non-root user
docker run --rm milvaion-api:test whoami
# Output should be: milvaion

# Check user ID
docker run --rm milvaion-api:test id
# Output: uid=999(milvaion) gid=999(milvaion) groups=999(milvaion)

# Run container normally
docker run -d -p 5000:5000 milvaion-api:test
```

## Rollback

If you need to rollback to root user (not recommended):

1. Remove `USER milvaion` line from Dockerfile
2. Remove user creation lines
3. Rebuild and push

However, this will **reduce security** and **lower Docker Hub health score**.

## References

- [Docker Security Best Practices](https://docs.docker.com/develop/security-best-practices/)
- [Supply Chain Attestation](https://docs.docker.com/build/attestations/)
- [SBOM Generation](https://docs.docker.com/build/attestations/sbom/)
- [Non-root Containers](https://docs.docker.com/develop/develop-images/dockerfile_best-practices/#user)
