# Troubleshooting Guide

This guide helps diagnose and resolve common issues when working with Milvaion.

## Table of Contents

- [Quick Diagnosis](#quick-diagnosis)
- [Infrastructure Issues](#infrastructure-issues)
- [API Issues](#api-issues)
- [Worker Issues](#worker-issues)
- [Job Execution Issues](#job-execution-issues)
- [Database Issues](#database-issues)
- [Performance Issues](#performance-issues)
- [Deployment Issues](#deployment-issues)
- [Getting Help](#getting-help)

---

## Quick Diagnosis

### Health Check Commands

```bash
# Check all services
docker compose ps

# Check API health
curl http://localhost:5000/api/v1/healthcheck/ready

# Check PostgreSQL
docker exec -it milvaion-postgres pg_isready -U postgres

# Check Redis
docker exec -it milvaion-redis redis-cli ping

# Check RabbitMQ
curl -u guest:guest http://localhost:15672/api/health/checks/alarms
```

### Log Commands

```bash
# API logs
docker logs milvaion-api --tail 100 -f

# Worker logs
docker logs sample-worker --tail 100 -f

# PostgreSQL logs
docker logs milvaion-postgres --tail 50

# RabbitMQ logs
docker logs milvaion-rabbitmq --tail 50

# View Seq logs
# Open: http://localhost:5341
```

---

## Infrastructure Issues

### PostgreSQL Issues

#### Problem: "Connection refused" to PostgreSQL

**Symptoms:**
```
Npgsql.NpgsqlException: Connection refused
Unable to connect to database
```

**Solutions:**

1. **Check if container is running:**
```bash
docker ps | grep postgres
```

2. **Check container health:**
```bash
docker inspect milvaion-postgres | grep -A 5 "Health"
```

3. **Restart container:**
```bash
docker compose restart postgres
```

4. **Check logs for errors:**
```bash
docker logs milvaion-postgres
```

5. **Verify connection string:**
```bash
# Test connection
docker exec -it milvaion-postgres psql -U postgres -d MilvaionDb -c "SELECT 1;"
```

#### Problem: "Password authentication failed"

**Solutions:**

1. **Verify credentials in appsettings.json**
2. **Check environment variables:**
```bash
docker exec milvaion-postgres env | grep POSTGRES
```

3. **Reset password:**
```bash
docker exec -it milvaion-postgres psql -U postgres
ALTER USER postgres WITH PASSWORD 'postgres123';
```

#### Problem: Database tables don't exist

**Symptoms:**
```
Npgsql.PostgresException: relation "ScheduledJobs" does not exist
```

**Solutions:**

1. **Apply migrations:**
```bash
cd src/Milvaion.Api
dotnet ef database update
```

2. **Check migration status:**
```bash
dotnet ef migrations list
```

3. **Recreate database (development only):**
```bash
dotnet ef database drop --force
dotnet ef database update
```

---

### Redis Issues

#### Problem: "Connection refused" to Redis

**Solutions:**

1. **Check container status:**
```bash
docker ps | grep redis
docker logs milvaion-redis
```

2. **Test connection:**
```bash
docker exec -it milvaion-redis redis-cli ping
# Should return: PONG
```

3. **Restart Redis:**
```bash
docker compose restart redis
```

4. **Check connection string in config:**
```json
{
  "MilvaionConfig": {
    "Redis": {
      "ConnectionString": "redis:6379",
      "Database": 0
    }
  }
}
```

#### Problem: Redis authentication error

**Symptoms:**
```
NOAUTH Authentication required
```

**Solutions:**

1. **Check if password is required:**
```bash
docker exec -it milvaion-redis redis-cli CONFIG GET requirepass
```

2. **Update connection string with password:**
```
redis:6379,password=your_password
```

#### Problem: Redis keys not found

**Solutions:**

1. **Check database number:**
```bash
docker exec -it milvaion-redis redis-cli
SELECT 0
KEYS Milvaion:JobScheduler:*
```

2. **Verify key prefix in config:**
```json
{
  "MilvaionConfig": {
    "Redis": {
      "KeyPrefix": "Milvaion:JobScheduler:"
    }
  }
}
```

---

### RabbitMQ Issues

#### Problem: "Connection refused" to RabbitMQ

**Solutions:**

1. **Check container status:**
```bash
docker ps | grep rabbitmq
docker logs milvaion-rabbitmq
```

2. **Wait for startup (can take 30-60 seconds):**
```bash
docker logs milvaion-rabbitmq | grep "Server startup complete"
```

3. **Test connection:**
```bash
curl -u guest:guest http://localhost:15672/api/overview
```

4. **Restart RabbitMQ:**
```bash
docker compose restart rabbitmq
```

#### Problem: Queue not found

**Symptoms:**
```
RabbitMQ.Client.Exceptions.OperationInterruptedException: The AMQP operation was interrupted
Channel closed: NOT_FOUND
```

**Solutions:**

1. **Check queues in Management UI:**
   - Open: http://localhost:15672
   - Login: guest/guest
   - Go to "Queues" tab

2. **Verify queue bindings:**
   - Expected queues:
     - `milvaion.job.{workerId}`
     - `milvaion.status`
     - `milvaion.logs`
     - `milvaion.dlq`

3. **Recreate queues (development only):**
```bash
docker compose down
docker volume rm milvaion_rabbitmq_data
docker compose up -d
```

#### Problem: Messages accumulating in queue

**Solutions:**

1. **Check queue depth in Management UI**

2. **Verify workers are running:**
```bash
docker ps | grep worker
```

3. **Check worker logs for errors:**
```bash
docker logs sample-worker --tail 100
```

4. **Increase worker parallelism:**
```json
{
  "Worker": {
    "MaxParallelJobs": 20
  }
}
```

5. **Scale workers:**
```bash
docker compose up -d --scale sample-worker=3
```

---

## API Issues

### Problem: API not starting

**Symptoms:**
```
Application startup exception
The application failed to start
```

**Solutions:**

1. **Check logs:**
```bash
docker logs milvaion-api
```

2. **Verify configuration:**
```bash
docker exec milvaion-api env | grep -E "ConnectionStrings|MilvaionConfig"
```

3. **Check port conflicts:**
```bash
# Windows
netstat -ano | findstr :5000

# Linux/macOS
lsof -i :5000
```

4. **Validate appsettings.json:**
```bash
# Check for JSON syntax errors
cat src/Milvaion.Api/appsettings.json | jq .
```

### Problem: 401 Unauthorized

**Solutions:**

1. **Obtain access token:**
```bash
curl -X POST http://localhost:5000/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"userName":"rootuser","password":"your-password"}'
```

2. **Use token in requests:**
```bash
curl http://localhost:5000/api/v1/jobs \
  -H "Authorization: Bearer YOUR_TOKEN"
```

3. **Check token expiration:**
```json
{
  "Milvasoft": {
    "Identity": {
      "Token": {
        "ExpirationMinute": 90
      }
    }
  }
}
```

### Problem: 419 Token Expired

**Solutions:**

1. **Use refresh token:**
```bash
curl -X POST http://localhost:5000/api/v1/auth/refresh \
  -H "Content-Type: application/json" \
  -d '{"refreshToken":"YOUR_REFRESH_TOKEN"}'
```

2. **Re-authenticate:**
```bash
curl -X POST http://localhost:5000/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"userName":"rootuser","password":"your-password"}'
```

### Problem: CORS errors in browser

**Symptoms:**
```
Access to fetch at 'http://localhost:5000' has been blocked by CORS policy
No 'Access-Control-Allow-Origin' header is present
```

**Solutions:**

Milvaion uses **configuration-based CORS** via `appsettings.json`. No code changes required.

#### 1. Public/Production Configuration

For production environments with specific allowed origins:

```json
{
  "Cors": {
    "DefaultPolicy": "Public",
    "Policies": {
      "Public": {
        "Origins": [
          "https://app.your-domain.com",
          "https://dashboard.your-domain.com"
        ],
        "Methods": [ "GET", "POST", "PUT", "PATCH", "DELETE" ],
        "Headers": [ "Content-Type", "Authorization" ],
        "AllowCredentials": false
      }
    }
  }
}
```

**Configuration Options:**
- `DefaultPolicy`: Policy name to use (must exist in `Policies`)
- `Origins`: Allowed origin URLs (exact match)
- `Methods`: Allowed HTTP methods (or `["All"]`)
- `Headers`: Allowed request headers (or `["All"]`)
- `AllowCredentials`: Allow cookies/auth headers

#### 2. Development Configuration

For local development with multiple ports:

```json
{
  "Cors": {
    "DefaultPolicy": "Development",
    "Policies": {
      "Development": {
        "Origins": [
          "http://localhost:3000",
          "http://localhost:5173",
          "http://localhost:8080"
        ],
        "Methods": [ "All" ],
        "Headers": [ "All" ],
        "ExposedHeaders": [ "Content-Disposition" ],
        "AllowCredentials": true
      }
    }
  }
}
```

#### 3. Internal/Trusted Environments (Allow All)

?? **For internal APIs, admin panels, or gateway-protected services only:**

```json
{
  "Cors": {
    "DefaultPolicy": "AllowAll",
    "Policies": {
      "AllowAll": {
        "AllowAnyOriginWithCredentials": true,
        "Origins": [ "All" ],
        "Methods": [ "All" ],
        "Headers": [ "All" ],
        "ExposedHeaders": [ "Content-Disposition" ],
        "AllowCredentials": true
      }
    }
  }
}
```

> **?? Security Warning:**  
> This configuration **MUST NOT be exposed to the public internet**.  
> Use it only behind a reverse proxy, VPN, or API gateway.

#### 4. Verify CORS is applied

Check if CORS headers are present:

```bash
curl -I -X OPTIONS http://localhost:5000/api/v1/jobs \
  -H "Origin: http://localhost:3000" \
  -H "Access-Control-Request-Method: GET"

# Expected headers:
# Access-Control-Allow-Origin: http://localhost:3000
# Access-Control-Allow-Methods: GET, POST, PUT, ...
# Access-Control-Allow-Headers: Content-Type, Authorization
```

#### 5. Common CORS Issues

**Issue:** Origin not allowed
```
Access-Control-Allow-Origin header has a value 'null'
```
**Fix:** Add your origin to the `Origins` array in config.

**Issue:** Credentials error
```
The value of the 'Access-Control-Allow-Credentials' header must be 'true'
```
**Fix:** Set `"AllowCredentials": true` in policy configuration.

**Issue:** Preflight request failing
```
Response to preflight request doesn't pass access control check
```
**Fix:** Ensure `OPTIONS` method is included in allowed methods.

---

## Worker Issues

### Problem: Worker not receiving jobs

**Solutions:**

1. **Verify WorkerId matches job:**
```bash
# Check job's workerId
curl http://localhost:5000/api/v1/jobs/{jobId}

# Check worker configuration
cat appsettings.json | jq '.Worker.WorkerId'
```

2. **Check RabbitMQ bindings:**
   - Management UI ? Queues ? `milvaion.job.{workerId}`
   - Should have binding from `milvaion.job` exchange

3. **Verify worker is connected:**
   - Check RabbitMQ Management UI ? Connections
   - Should see connection from worker

4. **Check worker logs:**
```bash
docker logs sample-worker | grep -E "Connected|Consuming"
```

### Problem: Worker crashes on startup

**Solutions:**

1. **Check logs for exception:**
```bash
docker logs sample-worker --tail 50
```

2. **Common causes:**
   - Missing configuration
   - Invalid RabbitMQ credentials
   - Missing dependencies

3. **Validate configuration:**
```bash
docker exec sample-worker cat /app/appsettings.json | jq .
```

4. **Test connection manually:**
```bash
docker exec sample-worker curl rabbitmq:15672
```

### Problem: Jobs timing out

**Solutions:**

1. **Increase timeout:**
```json
{
  "Worker": {
    "ExecutionTimeoutSeconds": 600
  }
}
```

2. **Or per-job:**
```json
{
  "JobConsumers": {
    "LongRunningJob": {
      "ExecutionTimeoutSeconds": 1800
    }
  }
}
```

3. **Check job logic for hangs:**
```csharp
// Add timeout to operations
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await operation.ExecuteAsync(cts.Token);
```

### Problem: Memory leak in worker

**Solutions:**

1. **Monitor memory usage:**
```bash
docker stats sample-worker
```

2. **Check for common causes:**
   - Not disposing DbContext
   - Accumulating event handlers
   - Static collections growing

3. **Use using statements:**
```csharp
using (var scope = serviceProvider.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MyDbContext>();
    // Use dbContext
}
```

4. **Restart worker periodically:**
```yaml
# In docker-compose.yml
deploy:
  restart_policy:
    condition: any
    max_attempts: 3
```

---

## Job Execution Issues

### Problem: Jobs not executing

**Symptoms:**
- Job status stays "Queued"
- No worker logs

**Solutions:**

1. **Check job is active:**
```bash
curl http://localhost:5000/api/v1/jobs/{jobId}
# Verify: "isActive": true
```

2. **Verify next execution time:**
```bash
# Check Redis
docker exec -it milvaion-redis redis-cli
ZRANGE Milvaion:JobScheduler:schedule 0 -1 WITHSCORES
```

3. **Check dispatcher is running:**
```bash
docker logs milvaion-api | grep "Dispatcher"
```

4. **Manually trigger job:**
```bash
curl -X POST http://localhost:5000/api/v1/jobs/job/trigger \
  -H "Content-Type: application/json" \
  -d '{"jobId":"YOUR_JOB_ID","reason":"Manual test"}'
```

### Problem: Jobs always failing

**Solutions:**

1. **Check occurrence details:**
```bash
curl http://localhost:5000/api/v1/occurrences/{occurrenceId}
# Look at: exception, logs, result
```

2. **Review worker logs:**
```bash
docker logs sample-worker | grep -A 20 "Exception"
```

3. **Test job data:**
```csharp
// Validate JSON
var data = JsonSerializer.Deserialize<MyJobData>(jobDataString);
```

4. **Check auto-disable:**
```json
{
  "MilvaionConfig": {
    "JobAutoDisable": {
      "Enabled": true,
      "ConsecutiveFailureThreshold": 5
    }
  }
}
```

### Problem: Jobs stuck in "Running"

**Symptoms:**
- Occurrence status is "Running" for hours
- Worker not found or crashed

**Solutions:**

1. **Check zombie detector:**
```bash
docker logs milvaion-api | grep "Zombie"
```

2. **Verify zombie detector is enabled:**
```json
{
  "MilvaionConfig": {
    "ZombieOccurrenceDetector": {
      "Enabled": true,
      "CheckIntervalSeconds": 300,
      "ZombieTimeoutMinutes": 10
    }
  }
}
```

3. **Manually mark as zombie:**
```sql
UPDATE "JobOccurrences"
SET "Status" = 6
WHERE "Id" = 'occurrence-id' AND "Status" = 1;
```

### Problem: Duplicate job executions

**Solutions:**

1. **Check concurrent execution policy:**
```json
{
  "concurrentExecutionPolicy": 0  // 0=Skip, 1=Queue
}
```

2. **Verify only one dispatcher is running:**
```bash
docker ps | grep milvaion-api
# Should see only one instance with dispatcher lock
```

3. **Check Redis locks:**
```bash
docker exec -it milvaion-redis redis-cli
KEYS Milvaion:JobScheduler:locks:*
```

---

## Database Issues

### Problem: Slow queries

**Solutions:**

1. **Check query execution time in logs:**
```bash
docker logs milvaion-api | grep "CommandExecuted"
```

2. **Verify indexes exist:**
```sql
SELECT
    tablename,
    indexname,
    indexdef
FROM
    pg_indexes
WHERE
    schemaname = 'public'
ORDER BY
    tablename,
    indexname;
```

3. **Add missing indexes:**
```sql
CREATE INDEX IF NOT EXISTS "IX_JobOccurrences_JobId_CreatedAt" 
ON "JobOccurrences"("JobId", "CreatedAt" DESC);
```

4. **Analyze query plans:**
```sql
EXPLAIN ANALYZE
SELECT * FROM "JobOccurrences"
WHERE "JobId" = 'some-id'
ORDER BY "CreatedAt" DESC
LIMIT 20;
```

### Problem: Database connection pool exhausted

**Symptoms:**
```
Npgsql.NpgsqlException: The connection pool has been exhausted
```

**Solutions:**

1. **Increase pool size:**
```
ConnectionString=...;Maximum Pool Size=100;
```

2. **Check for connection leaks:**
```csharp
// Always use using or async using
await using var connection = new NpgsqlConnection(connectionString);
```

3. **Monitor active connections:**
```sql
SELECT count(*) 
FROM pg_stat_activity 
WHERE datname = 'MilvaionDb';
```

### Problem: Migration conflicts

**Symptoms:**
```
Cannot apply migration: column already exists
Pending model changes detected
```

**Solutions:**

1. **Check migration status:**
```bash
dotnet ef migrations list
```

2. **Remove last migration:**
```bash
dotnet ef migrations remove
```

3. **Generate new migration:**
```bash
dotnet ef migrations add FixMigration
```

4. **Apply migrations:**
```bash
dotnet ef database update
```

---

## Performance Issues

### Problem: High API latency

**Solutions:**

1. **Check database query performance:**
```sql
SELECT query, mean_exec_time, calls
FROM pg_stat_statements
ORDER BY mean_exec_time DESC
LIMIT 10;
```

2. **Enable query logging:**
```json
{
  "Logging": {
    "LogLevel": {
      "Microsoft.EntityFrameworkCore.Database.Command": "Information"
    }
  }
}
```

3. **Add caching:**
```csharp
services.AddMemoryCache();
```

4. **Use pagination:**
```bash
# Always use page size limits
GET /api/v1/jobs?pageIndex=1&requestedItemCount=20
```

### Problem: High worker CPU usage

**Solutions:**

1. **Monitor CPU:**
```bash
docker stats
```

2. **Reduce parallelism:**
```json
{
  "Worker": {
    "MaxParallelJobs": 5
  }
}
```

3. **Profile worker code:**
```bash
dotnet trace collect --process-id $(pgrep -f "sample-worker")
```

4. **Check for infinite loops:**
```csharp
// Add timeout/cancellation
while (!cancellationToken.IsCancellationRequested)
{
    await Task.Delay(1000, cancellationToken);
}
```

### Problem: RabbitMQ queue growing

**Solutions:**

1. **Scale workers horizontally:**
```bash
docker compose up -d --scale sample-worker=5
```

2. **Increase worker parallelism:**
```json
{
  "Worker": {
    "MaxParallelJobs": 50
  }
}
```

3. **Optimize job execution:**
```csharp
// Batch operations
var tasks = items.Select(i => ProcessAsync(i));
await Task.WhenAll(tasks);
```

4. **Add more worker instances:**
```yaml
services:
  worker-1:
    image: milvasoft/sample-worker
  worker-2:
    image: milvasoft/sample-worker
  worker-3:
    image: milvasoft/sample-worker
```

---

## Deployment Issues

### Problem: Containers failing health checks

**Solutions:**

1. **Check health check configuration:**
```yaml
healthcheck:
  test: ["CMD", "curl", "-f", "http://localhost:8080/health"]
  interval: 30s
  timeout: 10s
  retries: 3
  start_period: 40s
```

2. **Increase start_period for slow startup:**
```yaml
healthcheck:
  start_period: 60s
```

3. **Check container logs:**
```bash
docker inspect milvaion-api | grep -A 10 "Health"
```

### Problem: Kubernetes pods crashing

**Solutions:**

1. **Check pod logs:**
```bash
kubectl logs -f milvaion-api-xxx
kubectl logs -f milvaion-api-xxx --previous
```

2. **Check resource limits:**
```yaml
resources:
  limits:
    memory: "2Gi"
    cpu: "1000m"
  requests:
    memory: "512Mi"
    cpu: "250m"
```

3. **Check liveness/readiness probes:**
```yaml
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
```

### Problem: Environment variable not applied

**Solutions:**

1. **Verify environment variables:**
```bash
docker exec milvaion-api env | grep MilvaionConfig
```

2. **Check precedence (highest to lowest):**
   - Command line arguments
   - Environment variables
   - appsettings.{Environment}.json
   - appsettings.json

3. **Use correct format for nested config:**
```bash
# Correct
MilvaionConfig__Redis__ConnectionString=redis:6379

# Incorrect
MilvaionConfig.Redis.ConnectionString=redis:6379
```

---

## Getting Help

### Before Asking for Help

1. ? Check this troubleshooting guide
2. ? Review logs (API, Worker, Infrastructure)
3. ? Search existing [GitHub Issues](https://github.com/Milvasoft/milvaion/issues)
4. ? Check [Documentation](../portaldocs/00-guide.md)

### Where to Get Help

| Issue Type | Where to Ask |
|------------|-------------|
| **Bug Report** | [GitHub Issues](https://github.com/Milvasoft/milvaion/issues/new?template=bug_report.md) |
| **Feature Request** | [GitHub Issues](https://github.com/Milvasoft/milvaion/issues/new?template=feature_request.md) |
| **Question** | [GitHub Discussions](https://github.com/Milvasoft/milvaion/discussions) |
| **Security Issue** | [Security Policy](./SECURITY.md) |

### Information to Include

When asking for help, include:

1. **Milvaion version:** `docker image ls | grep milvaion`
2. **Environment:** Development / Production / Docker / Kubernetes
3. **Error messages:** Complete stack traces
4. **Logs:** Relevant portions (sanitize sensitive data)
5. **Configuration:** Relevant config sections (remove secrets)
6. **Steps to reproduce:** Detailed reproduction steps
7. **Expected vs actual behavior**

### Example Bug Report

```markdown
## Environment
- Milvaion Version: 1.0.0
- .NET Version: 10.0
- OS: Ubuntu 22.04
- Deployment: Docker Compose

## Description
Jobs are not executing even though they are active and scheduled.

## Steps to Reproduce
1. Create a job with cron `*/5 * * * *`
2. Set job as active
3. Wait 10 minutes
4. Check occurrence history - no executions

## Expected Behavior
Job should execute every 5 minutes.

## Actual Behavior
Job never executes. Status stays "Queued" if manually triggered.

## Logs
API logs:
```
[2024-01-15 10:00:00] Dispatcher polling...
[2024-01-15 10:00:00] Found 0 due jobs
```

Worker logs:
```
[2024-01-15 10:00:00] Worker started
[2024-01-15 10:00:00] Connected to RabbitMQ
[2024-01-15 10:00:00] Waiting for jobs...
```

## Configuration
```json
{
  "Worker": {
    "WorkerId": "sample-worker-01",
    "MaxParallelJobs": 10
  }
}
```

## Additional Context
- Job was working yesterday
- No recent configuration changes
- Redis shows job in schedule: `ZRANGE Milvaion:JobScheduler:schedule 0 -1`
```

---

## Further Reading

- [Development Guide](./DEVELOPMENT.md)
- [Architecture Guide](./ARCHITECTURE.md)
- [Configuration Reference](../portaldocs/06-configuration.md)
- [Security Guide](./SECURITY.md)
