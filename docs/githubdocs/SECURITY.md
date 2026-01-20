# Security Policy

## Supported Versions

The following versions of Milvaion are currently being supported with security updates:

| Version | Supported          |
| ------- | ------------------ |
| 1.0.x   | :white_check_mark: |
| < 1.0   | :x:                |

## Reporting a Vulnerability

We take the security of Milvaion seriously. If you believe you have found a security vulnerability, please report it to us as described below.

### Where to Report

**Please do NOT report security vulnerabilities through public GitHub issues.**

Instead, please report them via email to [info@milvasoft.com](mailto:info@milvasoft.com).

Include the following information in your report:

- Type of issue (e.g., buffer overflow, SQL injection, cross-site scripting, etc.)
- Full paths of source file(s) related to the manifestation of the issue
- The location of the affected source code (tag/branch/commit or direct URL)
- Any special configuration required to reproduce the issue
- Step-by-step instructions to reproduce the issue
- Proof-of-concept or exploit code (if possible)
- Impact of the issue, including how an attacker might exploit it

### What to Expect

You should receive a response within **96 hours** acknowledging your report. We will keep you informed about the progress towards a fix and announcement.

After the initial reply to your report, our security team will:

1. **Confirm the vulnerability** and determine its severity
2. **Develop a fix** for all supported versions
3. **Prepare a security advisory** for publication
4. **Release patched versions** as soon as possible
5. **Publicly disclose the vulnerability** after patches are available

### Responsible Disclosure

We kindly ask you to:

- Give us reasonable time to fix the issue before public disclosure
- Avoid exploiting the vulnerability
- Demonstrate good faith by not accessing or modifying other users' data
- Not perform actions that could negatively affect Milvaion users or infrastructure

### Bug Bounty Program

At this time, Milvaion does not have a paid bug bounty program. However, we deeply appreciate security researchers who responsibly disclose vulnerabilities and will publicly acknowledge contributors (with their permission) in:

- Security advisories
- Release notes
- CONTRIBUTORS.md file

## Security Best Practices for Users

### Production Deployments

When deploying Milvaion to production, follow these security best practices:

#### 1. Authentication & Authorization

- **Never use default credentials** - Change the root password immediately
- **Use strong passwords** - Minimum 12 characters with complexity requirements
- **Enable JWT token validation** - Set `ValidateIssuer` and `ValidateAudience` to `true`
- **Use short token expiration** - Set `ExpirationMinute` to 15-30 minutes in production
- **Rotate JWT secrets regularly** - Use environment variables, never commit secrets

```bash
# Generate a secure JWT secret
openssl rand -hex 32
```

#### 2. Network Security

- **Use HTTPS** - Always enable TLS in production with valid certificates
- **Enable CORS properly** - Restrict origins to trusted domains only
- **Use private networks** - Keep Redis and RabbitMQ on internal networks
- **Configure firewall rules** - Allow only necessary ports

Recommended port access:

| Service    | Port  | Access      |
|------------|-------|-------------|
| API        | 443   | Public      |
| PostgreSQL | 5432  | Internal    |
| Redis      | 6379  | Internal    |
| RabbitMQ   | 5672  | Internal    |
| RabbitMQ Management | 15672 | Admin VPN only |

#### 3. Database Security

- **Use strong passwords** - For PostgreSQL user accounts
- **Enable SSL/TLS** - For database connections
- **Restrict network access** - Use PostgreSQL `pg_hba.conf` to limit connections
- **Regular backups** - Encrypt backup files
- **Connection pooling** - Use connection limits to prevent exhaustion

Example connection string with SSL:

```
Host=postgres.example.com;Port=5432;Database=MilvaionDb;Username=milvaion;Password=***;SSL Mode=Require;Trust Server Certificate=false
```

#### 4. Redis Security

- **Set a strong password** - Use `requirepass` in redis.conf
- **Disable dangerous commands** - Rename `FLUSHDB`, `FLUSHALL`, `CONFIG`, `SHUTDOWN`
- **Bind to specific interfaces** - Don't expose to public internet
- **Use TLS encryption** - For Redis 6.0+

```bash
# In redis.conf
requirepass your_strong_password_here
rename-command CONFIG ""
rename-command FLUSHDB ""
rename-command FLUSHALL ""
bind 127.0.0.1 ::1
```

#### 5. RabbitMQ Security

- **Change default credentials** - Never use guest/guest in production
- **Use TLS** - Enable SSL/TLS for AMQP connections
- **Create dedicated users** - Separate users for API and Workers with minimal permissions
- **Enable access control** - Configure vhost permissions properly
- **Limit management UI access** - Use VPN or IP whitelist

```bash
# Create users with limited permissions
rabbitmqctl add_user milvaion_api secure_password_1
rabbitmqctl add_user milvaion_worker secure_password_2
rabbitmqctl set_permissions -p / milvaion_api ".*" ".*" ".*"
rabbitmqctl set_permissions -p / milvaion_worker ".*" ".*" ".*"
```

#### 6. Container Security

- **Use non-root users** - Run containers as non-privileged users
- **Keep images updated** - Regularly update base images
- **Scan for vulnerabilities** - Use tools like Trivy or Snyk
- **Use read-only file systems** - Where possible
- **Limit resource usage** - Set memory and CPU limits

```yaml
# Docker Compose example
services:
  milvaion-api:
    image: milvasoft/milvaion-api:latest
    user: "1000:1000"
    read_only: true
    security_opt:
      - no-new-privileges:true
    deploy:
      resources:
        limits:
          cpus: '2'
          memory: 2G
```

#### 7. Secrets Management

**Never hardcode secrets** in configuration files. Use one of these approaches:

**Option 1: Environment Variables**

```bash
export ConnectionStrings__DefaultConnectionString="Host=..."
export MilvaionConfig__Redis__Password="..."
export Milvasoft__Identity__Token__SymmetricPublicKey="..."
```

**Option 2: Docker Secrets (Docker Swarm)**

```yaml
secrets:
  db_password:
    external: true
  jwt_key:
    external: true
```

**Option 3: Kubernetes Secrets**

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: milvaion-secrets
type: Opaque
data:
  db-password: <base64-encoded>
  jwt-key: <base64-encoded>
```

**Option 4: Azure Key Vault / AWS Secrets Manager**

#### 8. Monitoring & Logging

- **Enable audit logging** - Log all authentication attempts
- **Monitor failed login attempts** - Set up alerts for suspicious activity
- **Send logs to SIEM** - Centralized security monitoring
- **Redact sensitive data** - Don't log passwords, tokens, or PII
- **Set up alerts** - For security events

Example: Configure Seq with retention and access control

#### 9. Worker Security

- **Isolate workers** - Run workers in separate networks/containers
- **Validate job data** - Never trust job payloads
- **Limit external access** - Workers should not be directly accessible
- **Use timeouts** - Prevent infinite loops or DoS
- **Sandbox execution** - Consider containerizing individual job execution

#### 10. API Security

- **Rate limiting** - Prevent brute force and DoS attacks
- **Input validation** - Validate all user inputs
- **Output encoding** - Prevent XSS attacks
- **CSRF protection** - Use anti-forgery tokens
- **Security headers** - Set proper HTTP security headers

```csharp
// Example security headers in ASP.NET Core
app.Use(async (context, next) =>
{
    context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Add("X-Frame-Options", "DENY");
    context.Response.Headers.Add("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Add("Referrer-Policy", "no-referrer");
    context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'");
    await next();
});
```

## Security Advisories

Security advisories will be published on:

- GitHub Security Advisories: https://github.com/Milvasoft/milvaion/security/advisories
- Release notes for patched versions
- Project discussions

## Security Updates

To stay informed about security updates:

1. **Watch the repository** - Enable "Releases only" or "All activity"
2. **Subscribe to notifications** - GitHub will email you about security advisories
3. **Check regularly** - Review the CHANGELOG.md for security fixes
4. **Keep updated** - Always run the latest supported version

## Compliance

Milvaion is designed to help you meet compliance requirements, but proper configuration is your responsibility:

- **GDPR** - Implement proper data retention and deletion policies
- **HIPAA** - Encrypt data at rest and in transit, implement access controls
- **SOC 2** - Enable comprehensive logging and monitoring
- **PCI DSS** - Follow security best practices for payment-related jobs

## Third-Party Dependencies

We regularly update dependencies to address security vulnerabilities. You can review our dependencies in:

- Backend: `src/*/Directory.Packages.props`
- Frontend: `src/MilvaionUI/package.json`

We use automated tools (Dependabot) to detect vulnerable dependencies.

## Security Hardening Checklist

Before going to production, verify:

- Changed all default passwords
- Generated and configured strong JWT secret
- Enabled HTTPS with valid certificate
- Configured CORS for specific origins
- Database connections use SSL/TLS
- Redis requires password authentication
- RabbitMQ uses non-default credentials
- Secrets stored in secure vault (not in code)
- Containers run as non-root users
- Security headers configured
- Rate limiting enabled
- Logging configured with sensitive data redaction
- Monitoring and alerting set up
- Backup strategy implemented
- Disaster recovery plan documented

## Contact

For security concerns, contact us at:

- **Email**: milvasoft@milvasoft.com
- **Security Advisory Page**: https://github.com/Milvasoft/milvaion/security/advisories

---

**Thank you for helping keep Milvaion and our users safe!**
