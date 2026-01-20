---
id: security
title: Security
sidebar_position: 12
description: Security best practices and hardening guidelines for Milvaion deployments.
---

# Security Guide

This guide covers security best practices for deploying and operating Milvaion in production environments. All security configurations are managed through `appsettings.json`, environment variables, and infrastructure settings.

---

## Authentication Configuration

### JWT Token Settings

Configure token security in `appsettings.json`:

```json
{
  "Milvasoft": {
    "Identity": {
      "Token": {
        "UseUtcForDateTimes": true,
        "ExpirationMinute": 90,
        "TokenValidationParameters": {
          "ValidateIssuer": true,
          "ValidateAudience": true,
          "ValidIssuer": "https://your-domain.com",
          "ValidAudience": "milvaion-clients"
        },
        "SecurityKeyType": 0,
        "SymmetricPublicKey": "your-256-bit-secret-key-here"
      }
    }
  }
}
```

#### Recommended Settings

| Setting | Development | Production |
|---------|-------------|------------|
| `ExpirationMinute` | 90 | 15-30 |
| `ValidateIssuer` | false | **true** |
| `ValidateAudience` | false | **true** |
| `SymmetricPublicKey` | Any | **Random 256-bit key** |

#### Generate a Secure Key

```bash
# PowerShell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))

# Linux/macOS
openssl rand -hex 32
```

> ⚠️ **Important:** Never commit your production JWT key to version control. Use environment variables instead.

### Token Expiration Behavior

Milvaion handles token states with specific HTTP status codes:

| Status Code | Meaning | Client Action |
|-------------|---------|---------------|
| 401 | Invalid or missing token | Redirect to login |
| 419 | Token expired | Refresh token or re-login |
| 403 | Insufficient permissions | Show access denied |

---

## Password Security

### Password Policy Configuration

Configure password requirements in `appsettings.json`:

```json
{
  "Milvasoft": {
    "Identity": {
      "Password": {
        "Hasher": {
          "IterationCount": 10000
        },
        "RequiredLength": 8,
        "RequiredUniqueChars": 2,
        "RequireNonAlphanumeric": true,
        "RequireLowercase": true,
        "RequireUppercase": true,
        "RequireDigit": true
      }
    }
  }
}
```

#### Recommended Values

| Setting | Minimum | High Security |
|---------|---------|---------------|
| `RequiredLength` | 8 | 12+ |
| `IterationCount` | 10000 | 100000+ |
| `RequiredUniqueChars` | 2 | 4 |

### Account Lockout Protection

Protect against brute-force attacks:

```json
{
  "Milvasoft": {
    "Identity": {
      "Lockout": {
        "AllowedForNewUsers": true,
        "MaxFailedAccessAttempts": 5,
        "DefaultLockoutTimeSpan": 15
      }
    }
  }
}
```

**Behavior:**
- After 5 failed attempts → Account locked for 15 minutes
- Lockout duration shown to user
- Successful login resets the counter

---

## Infrastructure Security

### PostgreSQL

#### Secure Connection String

```json
{
  "ConnectionStrings": {
    "DefaultConnectionString": "Host=postgres;Port=5432;Database=MilvaionDb;Username=milvaion_app;Password=SECURE_PASSWORD;SSL Mode=Require;Trust Server Certificate=false;Pooling=true;Maximum Pool Size=100"
  }
}
```

#### Best Practices

| Practice | Implementation |
|----------|----------------|
| Dedicated user | Create `milvaion_app` with minimum required permissions |
| SSL/TLS | Enable `SSL Mode=Require` |
| Network isolation | Use private network, no public IP |
| Connection limits | Set `Maximum Pool Size` appropriately |

#### Database User Permissions

```sql
-- Create application user with minimal permissions
CREATE USER milvaion_app WITH PASSWORD 'secure_password';

-- Grant only necessary permissions
GRANT CONNECT ON DATABASE MilvaionDb TO milvaion_app;
GRANT USAGE ON SCHEMA public TO milvaion_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO milvaion_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO milvaion_app;

-- For migrations (use a separate admin user)
CREATE USER milvaion_admin WITH PASSWORD 'admin_password';
GRANT CREATE ON SCHEMA public TO milvaion_admin;
```

### Redis

#### API Configuration

```json
{
  "MilvaionConfig": {
    "Redis": {
      "ConnectionString": "redis:6379,ssl=true",
      "Password": "STRONG_REDIS_PASSWORD",
      "Database": 0,
      "ConnectTimeout": 5000,
      "SyncTimeout": 5000
    }
  }
}
```

#### Worker Configuration

```json
{
  "Worker": {
    "Redis": {
      "ConnectionString": "redis:6379",
      "Password": "SAME_REDIS_PASSWORD",
      "Database": 0,
      "CancellationChannel": "Milvaion:JobScheduler:cancellation_channel"
    }
  }
}
```

#### Redis Server Hardening

```bash
# redis.conf
requirepass YOUR_STRONG_PASSWORD
bind 127.0.0.1  # Or private network IP only
protected-mode yes
rename-command FLUSHALL ""
rename-command FLUSHDB ""
rename-command DEBUG ""
```

### RabbitMQ

#### API Configuration

```json
{
  "MilvaionConfig": {
    "RabbitMQ": {
      "Host": "rabbitmq",
      "Port": 5672,
      "Username": "milvaion_producer",
      "Password": "SECURE_RABBITMQ_PASSWORD",
      "VirtualHost": "/milvaion"
    }
  }
}
```

#### Worker Configuration

```json
{
  "Worker": {
    "RabbitMQ": {
      "Host": "rabbitmq",
      "Port": 5672,
      "Username": "milvaion_consumer",
      "Password": "SECURE_RABBITMQ_PASSWORD",
      "VirtualHost": "/milvaion"
    }
  }
}
```

#### Virtual Host Isolation

Create a dedicated virtual host for Milvaion:

```bash
# Create virtual host
rabbitmqctl add_vhost /milvaion

# Create users with specific permissions
rabbitmqctl add_user milvaion_producer SECURE_PASSWORD
rabbitmqctl set_permissions -p /milvaion milvaion_producer ".*" ".*" ".*"

rabbitmqctl add_user milvaion_consumer SECURE_PASSWORD
rabbitmqctl set_permissions -p /milvaion milvaion_consumer ".*" ".*" ".*"

# Remove default guest user
rabbitmqctl delete_user guest
```

#### Disable Management UI in Production

```bash
# Disable management plugin entirely
rabbitmq-plugins disable rabbitmq_management

# Or restrict via environment variable
RABBITMQ_MANAGEMENT_LISTENER_IP=127.0.0.1
```

---

## Secrets Management

### Environment Variables

Override sensitive configuration with environment variables:

```yaml
# docker-compose.yml
services:
  milvaion-api:
    environment:
      - ConnectionStrings__DefaultConnectionString=Host=postgres;...;Password=${DB_PASSWORD}
      - Milvasoft__Identity__Token__SymmetricPublicKey=${JWT_SECRET}
      - MilvaionConfig__Redis__Password=${REDIS_PASSWORD}
      - MilvaionConfig__RabbitMQ__Password=${RABBITMQ_PASSWORD}
      - MILVAION_ROOT_PASSWORD=${ROOT_PASSWORD}

  worker:
    environment:
      - Worker__Redis__Password=${REDIS_PASSWORD}
      - Worker__RabbitMQ__Password=${RABBITMQ_PASSWORD}
```

### Docker Secrets

For Docker Swarm deployments:

```yaml
services:
  milvaion-api:
    secrets:
      - db_password
      - jwt_secret
      - redis_password
      - rabbitmq_password

secrets:
  db_password:
    file: ./secrets/db_password.txt
  jwt_secret:
    file: ./secrets/jwt_secret.txt
  redis_password:
    file: ./secrets/redis_password.txt
  rabbitmq_password:
    file: ./secrets/rabbitmq_password.txt
```

### Kubernetes Secrets

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: milvaion-secrets
  namespace: milvaion
type: Opaque
stringData:
  db-password: "your-secure-db-password"
  jwt-secret: "your-256-bit-jwt-secret"
  redis-password: "your-redis-password"
  rabbitmq-password: "your-rabbitmq-password"
```

Reference in deployment:

```yaml
env:
  - name: MilvaionConfig__Redis__Password
    valueFrom:
      secretKeyRef:
        name: milvaion-secrets
        key: redis-password
```

---

## Logging Security

### Log Level Configuration

Reduce sensitive information in logs for production:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore.Authentication": "Warning",
        "Microsoft.AspNetCore.Authorization": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
      }
    }
  }
}
```

### Seq Log Server Security

```yaml
# docker-compose.yml
seq:
  environment:
    - SEQ_FIRSTRUN_ADMINPASSWORD=${SEQ_ADMIN_PASSWORD}
    - ACCEPT_EULA=Y
```

> ⚠️ **Important:** Change the Seq admin password immediately after first deployment.

---

## Worker Security

### Resource Limits

Prevent resource exhaustion attacks:

```yaml
# docker-compose.yml
worker:
  deploy:
    resources:
      limits:
        cpus: '2'
        memory: 2048M
      reservations:
        cpus: '0.5'
        memory: 512M
```

### Worker Configuration

```json
{
  "Worker": {
    "WorkerId": "worker-01",
    "MaxParallelJobs": 32,
    "ExecutionTimeoutSeconds": 300
  }
}
```

| Setting | Description | Security Impact |
|---------|-------------|-----------------|
| `MaxParallelJobs` | Limit concurrent executions | Prevents resource exhaustion |
| `ExecutionTimeoutSeconds` | Kill long-running jobs | Prevents hung processes |

---

## Network Security

### Reverse Proxy Configuration (nginx)

```nginx
server {
    listen 443 ssl http2;
    server_name milvaion.your-domain.com;

    ssl_certificate /etc/ssl/certs/milvaion.crt;
    ssl_certificate_key /etc/ssl/private/milvaion.key;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256;
    ssl_prefer_server_ciphers off;

    # Security headers
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header X-XSS-Protection "1; mode=block" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;

    location / {
        proxy_pass http://milvaion-api:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

### Firewall Rules

Only expose necessary ports:

| Service | Port | Exposure |
|---------|------|----------|
| Milvaion API | 5000 | Via reverse proxy only |
| PostgreSQL | 5432 | Private network only |
| Redis | 6379 | Private network only |
| RabbitMQ | 5672 | Private network only |
| RabbitMQ Management | 15672 | Disabled or internal only |
| Seq | 5341 | Internal only |

---

## Security Checklist

### Pre-Production

- [ ] Change all default passwords (PostgreSQL, Redis, RabbitMQ, Seq)
- [ ] Generate new JWT signing key (256-bit random)
- [ ] Enable SSL/TLS for database connections
- [ ] Set strong password policies
- [ ] Configure account lockout
- [ ] Set `MILVA_ENV=prod` environment variable

### Infrastructure

- [ ] Place databases on private networks
- [ ] Configure reverse proxy with TLS termination
- [ ] Set up firewall rules
- [ ] Disable RabbitMQ management UI or restrict access
- [ ] Enable Redis protected mode
- [ ] Remove default `guest` user from RabbitMQ

### Monitoring

- [ ] Set up alerts for failed login attempts
- [ ] Monitor worker health anomalies
- [ ] Configure log retention policies
- [ ] Enable database backup monitoring

### Regular Maintenance
 
- [ ] Rotate secrets quarterly
- [ ] Update container images for security patches
- [ ] Review activity logs regularly
- [ ] Audit user permissions
- [ ] Test backup restoration procedures

---

## Incident Response

### Signs of Compromise

| Indicator | Action |
|-----------|--------|
| Multiple failed logins from same IP | Review logs, consider IP blocking |
| Unexpected admin user creation | Disable account, investigate |
| Unusual job execution patterns | Review job logs and payloads |
| Database connection spikes | Check for injection attempts |
| Worker heartbeat anomalies | Investigate worker health |

### Response Steps

1. **Contain** - Isolate affected systems
2. **Identify** - Review logs in Seq/Grafana
3. **Eradicate** - Remove threat, rotate all credentials
4. **Recover** - Restore from known-good backups
5. **Document** - Record incident details and lessons learned

---

## Related Documentation

- [Configuration Reference](./06-configuration.md)
- [Deployment Guide](./07-deployment.md)
- [Monitoring Guide](./10-monitoring.md)
- [Maintenance Guide](./11-maintenance.md)
