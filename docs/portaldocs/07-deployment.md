# Deployment Guide

This guide covers deploying Milvaion to production environments using Docker, Kubernetes, and traditional VMs.

## Deployment Checklist

Before going to production, ensure you have:

- PostgreSQL with proper backup strategy
- Redis with persistence enabled (RDB or AOF)
- RabbitMQ with durable queues
- Health checks configured
- Resource limits defined

## Docker Compose (Simple Production)

For single-server or small deployments:

### docker-compose.prod.yml

```yaml
version: '3.8'

services:
  postgres:
    image: postgres:16-alpine
    container_name: milvaion-postgres
    restart: always
    environment:
      POSTGRES_DB: MilvaionDb
      POSTGRES_USER: milvaion
      POSTGRES_PASSWORD_FILE: /run/secrets/db_password
    secrets:
      - db_password
    volumes:
      - postgres_data:/var/lib/postgresql/data
      - ./backups:/backups
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U milvaion -d MilvaionDb"]
      interval: 10s
      timeout: 5s
      retries: 5

  redis:
    image: redis:7-alpine
    container_name: milvaion-redis
    restart: always
    command: redis-server --appendonly yes --requirepass ${REDIS_PASSWORD}
    volumes:
      - redis_data:/data
    healthcheck:
      test: ["CMD", "redis-cli", "-a", "${REDIS_PASSWORD}", "ping"]
      interval: 10s
      timeout: 5s
      retries: 5

  rabbitmq:
    image: rabbitmq:3-management-alpine
    container_name: milvaion-rabbitmq
    restart: always
    environment:
      RABBITMQ_DEFAULT_USER: milvaion
      RABBITMQ_DEFAULT_PASS_FILE: /run/secrets/rabbitmq_password
    secrets:
      - rabbitmq_password
    volumes:
      - rabbitmq_data:/var/lib/rabbitmq
    healthcheck:
      test: ["CMD", "rabbitmqctl", "status"]
      interval: 30s
      timeout: 10s
      retries: 5

  milvaion-api:
    image: milvasoft/milvaion-api:1.0.0
    container_name: milvaion-api
    restart: always
    ports:
      - "5000:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ConnectionStrings__DefaultConnectionString=Host=postgres;Port=5432;Database=MilvaionDb;Username=milvaion;Password=${DB_PASSWORD}
      - MilvaionConfig__Redis__ConnectionString=redis:6379
      - MilvaionConfig__Redis__Password=${REDIS_PASSWORD}
      - MilvaionConfig__RabbitMQ__Host=rabbitmq
      - MilvaionConfig__RabbitMQ__Username=milvaion
      - MilvaionConfig__RabbitMQ__Password=${RABBITMQ_PASSWORD}
      - MILVAION_ROOT_PASSWORD=admin
      - MILVA_ENV=prod
    depends_on:
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy
      rabbitmq:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/health"]
      interval: 30s
      timeout: 10s
      retries: 3

  email-worker:
    image: my-company/email-worker:1.0.0
    restart: always
    deploy:
      replicas: 3
    environment:
      - Worker__WorkerId=email-worker
      - Worker__MaxParallelJobs=20
      - Worker__RabbitMQ__Host=rabbitmq
      - Worker__RabbitMQ__Username=milvaion
      - Worker__RabbitMQ__Password=${RABBITMQ_PASSWORD}
      - Worker__Redis__ConnectionString=redis:6379,password=${REDIS_PASSWORD}
    depends_on:
      - milvaion-api

secrets:
  db_password:
    file: ./secrets/db_password.txt
  rabbitmq_password:
    file: ./secrets/rabbitmq_password.txt

volumes:
  postgres_data:
  redis_data:
  rabbitmq_data:
```

### Deploy Commands

```bash
# Create secrets
mkdir -p secrets
echo "your-secure-db-password" > secrets/db_password.txt
echo "your-secure-rabbitmq-password" > secrets/rabbitmq_password.txt

# Create .env file
cat > .env << EOF
DB_PASSWORD=your-secure-db-password
REDIS_PASSWORD=your-secure-redis-password
RABBITMQ_PASSWORD=your-secure-rabbitmq-password
EOF

# Deploy
docker compose -f docker-compose.prod.yml up -d

# Scale workers
docker compose -f docker-compose.prod.yml up -d --scale email-worker=5
```

---

## Kubernetes Deployment

For larger, scalable deployments:

### Namespace and Secrets

```yaml
# namespace.yaml
apiVersion: v1
kind: Namespace
metadata:
  name: milvaion

---
# secrets.yaml
apiVersion: v1
kind: Secret
metadata:
  name: milvaion-secrets
  namespace: milvaion
type: Opaque
stringData:
  db-password: "your-secure-db-password"
  redis-password: "your-secure-redis-password"
  rabbitmq-password: "your-secure-rabbitmq-password"
```

### API Deployment

```yaml
# api-deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: milvaion-api
  namespace: milvaion
spec:
  replicas: 2
  selector:
    matchLabels:
      app: milvaion-api
  template:
    metadata:
      labels:
        app: milvaion-api
    spec:
      containers:
        - name: api
          image: milvasoft/milvaion-api:1.0.0
          ports:
            - containerPort: 8080
          env:
            - name: ASPNETCORE_ENVIRONMENT
              value: "Production"
            - name: ConnectionStrings__DefaultConnectionString
              valueFrom:
                secretKeyRef:
                  name: milvaion-secrets
                  key: db-connection-string
            - name: MilvaionConfig__Redis__ConnectionString
              value: "redis.milvaion.svc:6379"
            - name: MilvaionConfig__Redis__Password
              valueFrom:
                secretKeyRef:
                  name: milvaion-secrets
                  key: redis-password
            - name: MilvaionConfig__RabbitMQ__Host
              value: "rabbitmq.milvaion.svc"
            - name: MilvaionConfig__RabbitMQ__Password
              valueFrom:
                secretKeyRef:
                  name: milvaion-secrets
                  key: rabbitmq-password
          resources:
            requests:
              memory: "256Mi"
              cpu: "250m"
            limits:
              memory: "1Gi"
              cpu: "1000m"
          livenessProbe:
            httpGet:
              path: /health/live
              port: 8080
            initialDelaySeconds: 30
            periodSeconds: 10
          readinessProbe:
            httpGet:
              path: /health/ready
              port: 8080
            initialDelaySeconds: 10
            periodSeconds: 5

---
apiVersion: v1
kind: Service
metadata:
  name: milvaion-api
  namespace: milvaion
spec:
  selector:
    app: milvaion-api
  ports:
    - port: 80
      targetPort: 8080
  type: ClusterIP
```

### Worker Deployment

```yaml
# worker-deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: email-worker
  namespace: milvaion
spec:
  replicas: 3
  selector:
    matchLabels:
      app: email-worker
  template:
    metadata:
      labels:
        app: email-worker
    spec:
      containers:
        - name: worker
          image: my-company/email-worker:1.0.0
          env:
            - name: Worker__WorkerId
              value: "email-worker"
            - name: Worker__MaxParallelJobs
              value: "20"
            - name: Worker__RabbitMQ__Host
              value: "rabbitmq.milvaion.svc"
            - name: Worker__RabbitMQ__Password
              valueFrom:
                secretKeyRef:
                  name: milvaion-secrets
                  key: rabbitmq-password
            - name: Worker__Redis__ConnectionString
              value: "redis.milvaion.svc:6379"
          resources:
            requests:
              memory: "128Mi"
              cpu: "100m"
            limits:
              memory: "512Mi"
              cpu: "500m"
          livenessProbe:
            exec:
              command: ["cat", "/tmp/healthy"]
            initialDelaySeconds: 30
            periodSeconds: 10
```

### Horizontal Pod Autoscaler

```yaml
# hpa.yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: email-worker-hpa
  namespace: milvaion
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: email-worker
  minReplicas: 2
  maxReplicas: 10
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: 70
```

---

## Health Checks

### API Endpoints

| Endpoint | Purpose |
|----------|---------|
| `/health/live` | Liveness probe (is the process running?) |
| `/health/ready` | Readiness probe (are dependencies healthy?) |
| `/health` | Full health check with details |

### Worker Health

Workers don't expose HTTP endpoints by default. Options:

**Option 1: File-based health check**

```csharp
// In worker, create a health file periodically
File.WriteAllText("/tmp/healthy", DateTime.UtcNow.ToString());
```

```yaml
livenessProbe:
  exec:
    command: ["test", "-f", "/tmp/healthy"]
```

**Option 2: Use API Worker template**

The `milvaion-sampleworker-api` template exposes HTTP endpoints:

```yaml
livenessProbe:
  httpGet:
    path: /health
    port: 8080
```

---

## Resource Recommendations

### API Server

| Workload | CPU | Memory | Replicas |
|----------|-----|--------|----------|
| Small (&lt;1K jobs/day) | 250m | 512Mi | 1 |
| Medium (1K-10K jobs/day) | 500m | 2Gi | 1 |
| Large (>10K jobs/day) | 1000m | 4Gi | 1 |

### Workers

| Job Type | CPU | Memory | Concurrency |
|----------|-----|--------|-------------|
| I/O-bound (email, API calls) | 100m | 128Mi | 50-100 |
| CPU-bound (reports, processing) | 500m | 512Mi | 2-5 |
| Memory-intensive (data analysis) | 250m | 2Gi | 1-2 |

### Infrastructure

| Component | Production Recommendation |
|-----------|---------------------------|
| PostgreSQL | 2 CPU, 4GB RAM, SSD storage |
| Redis | 1 CPU, 2GB RAM, persistence enabled |
| RabbitMQ | 2 CPU, 2GB RAM, durable queues |

---

## Logging in Workers

### Structured Logging

Configure JSON logging for production:

```json
{
  "Serilog": {
    "Using": ["Serilog.Sinks.Console"],
    "MinimumLevel": "Information",
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "formatter": "Serilog.Formatting.Json.JsonFormatter, Serilog"
        }
      }
    ],
    "Enrich": ["FromLogContext", "WithMachineName", "WithThreadId"]
  }
}
```

### Centralized Logging

Send logs to ELK, Seq, or cloud logging:

```csharp
// Program.cs
builder.Host.UseSerilog((context, config) =>
{
    config
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Seq("http://seq.internal:5341")
        .Enrich.WithProperty("Service", "email-worker")
        .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName);
});
```

---

## Backup Strategy

### PostgreSQL

```bash
# Daily backup script
#!/bin/bash
BACKUP_DIR="/backups"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
pg_dump -h localhost -U milvaion MilvaionDb | gzip > $BACKUP_DIR/milvaion_$TIMESTAMP.sql.gz

# Keep last 7 days
find $BACKUP_DIR -name "*.sql.gz" -mtime +7 -delete
```

### Redis

Enable persistence in `redis.conf`:

```conf
# RDB snapshots
save 900 1
save 300 10
save 60 10000

# AOF for durability
appendonly yes
appendfsync everysec
```

---

## Security Checklist

- All passwords are in secrets, not config files
- TLS enabled for all external connections
- Database accessible only from API server
- Redis/RabbitMQ not exposed publicly
- API behind load balancer/reverse proxy
- Rate limiting on API endpoints
- Audit logging enabled
- Regular security updates

---

## What's Next?

- **[Reliability](08-reliability.md)** - Retry, DLQ, and error handling
- **[Scaling](09-scaling.md)** - Horizontal scaling strategies
- **[Monitoring](10-monitoring.md)** - Metrics and alerting
