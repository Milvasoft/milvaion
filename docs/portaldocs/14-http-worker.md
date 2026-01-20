---
id: http-worker
title: HTTP Worker
sidebar_position: 14
description: Pre-built workers included with Milvaion for common use cases.
---

The HTTP Worker is a powerful, general-purpose worker that can make HTTP requests to any endpoint. It supports all HTTP methods, multiple authentication types, retry policies, and response validation.

### Features

- All HTTP methods (GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS)
- Multiple authentication types (Basic, Bearer, API Key, OAuth2)
- Request body support (JSON, XML, Form, Multipart, Binary)
- Path and query parameters
- Custom headers and cookies
- Retry policies with exponential backoff
- Response validation (status codes, body content, JSONPath)
- Proxy support
- Client certificates (mTLS)
- SSL/TLS configuration

### Use Cases

| Scenario | Example |
|----------|---------|
| **Webhook Triggers** | Call external APIs on schedule |
| **Health Checks** | Monitor external service availability |
| **Data Sync** | Fetch data from REST APIs periodically |
| **Notifications** | Send HTTP-based notifications (Slack, Teams, Discord) |
| **Report Generation** | Trigger report generation endpoints |
| **Cache Warming** | Pre-populate caches by calling endpoints |

### Job Data Schema

When creating a job with the HTTP Worker, you provide configuration through the Job Data JSON:

```json
{
  "url": "https://api.example.com/users/{userId}",
  "method": "POST",
  "headers": {
    "X-Custom-Header": "value"
  },
  "queryParameters": {
    "include": "details"
  },
  "pathParameters": {
    "userId": "123"
  },
  "body": {
    "type": "Json",
    "content": {
      "name": "John Doe",
      "email": "john@example.com"
    }
  },
  "authentication": {
    "type": "Bearer",
    "credential": "your-api-token"
  },
  "timeoutSeconds": 30,
  "retryPolicy": {
    "maxRetries": 3,
    "initialDelayMs": 1000,
    "backoffMultiplier": 2.0
  },
  "validation": {
    "expectedStatusCodes": [200, 201],
    "bodyContains": "success"
  }
}
```

### Configuration Reference

#### Main Properties

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| `url` | string | ✅ | - | Target URL. Supports path parameters like `{userId}` |
| `method` | enum | - | `GET` | HTTP method: GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS |
| `headers` | object | - | - | Custom request headers as key-value pairs |
| `queryParameters` | object | - | - | Query string parameters to append to URL |
| `pathParameters` | object | - | - | Values to replace URL placeholders like `{id}` |
| `body` | object | - | - | Request body configuration |
| `authentication` | object | - | - | Authentication settings |
| `timeoutSeconds` | number | - | `30` | Request timeout in seconds |
| `followRedirects` | boolean | - | `true` | Whether to follow HTTP redirects |
| `maxRedirects` | number | - | `5` | Maximum redirects to follow |
| `ignoreSslErrors` | boolean | - | `false` | Skip SSL certificate validation (use with caution!) |
| `retryPolicy` | object | - | - | Retry configuration for failed requests |
| `validation` | object | - | - | Response validation rules |
| `proxy` | object | - | - | HTTP proxy configuration |
| `clientCertificate` | object | - | - | Client certificate for mTLS |
| `userAgent` | string | - | `Milvaion-HttpWorker/1.0` | Custom User-Agent header |
| `cookies` | object | - | - | Cookies to send with request |

#### Request Body Configuration

```json
{
  "body": {
    "type": "Json",
    "content": { "key": "value" },
    "encoding": "utf-8",
    "contentTypeOverride": "application/json"
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `type` | enum | `Json` | Body type: Json, Xml, Text, FormUrlEncoded, Multipart, Binary, GraphQL, Html, None |
| `content` | any | - | Body content (JSON object, string, or base64 for binary) |
| `formData` | object | - | Form fields for FormUrlEncoded or Multipart |
| `files` | array | - | File uploads for Multipart requests |
| `encoding` | string | `utf-8` | Character encoding |
| `contentTypeOverride` | string | - | Override default Content-Type header |

##### Multipart File Upload

```json
{
  "body": {
    "type": "Multipart",
    "formData": {
      "description": "My file upload"
    },
    "files": [
      {
        "fieldName": "file",
        "fileName": "document.pdf",
        "contentBase64": "JVBERi0xLjQK...",
        "contentType": "application/pdf"
      }
    ]
  }
}
```

#### Authentication Types

##### Bearer Token

```json
{
  "authentication": {
    "type": "Bearer",
    "credential": "your-jwt-token"
  }
}
```

##### Basic Authentication

```json
{
  "authentication": {
    "type": "Basic",
    "credential": "username",
    "secret": "password"
  }
}
```

##### API Key

```json
{
  "authentication": {
    "type": "ApiKey",
    "credential": "your-api-key",
    "keyName": "X-API-Key",
    "keyLocation": "Header"
  }
}
```

| `keyLocation` | Description |
|---------------|-------------|
| `Header` | Send as HTTP header (default) |
| `Query` | Send as query parameter |
| `Cookie` | Send as cookie |

##### OAuth2 (Client Credentials)

```json
{
  "authentication": {
    "type": "OAuth2",
    "tokenUrl": "https://auth.example.com/oauth/token",
    "clientId": "your-client-id",
    "clientSecret": "your-client-secret",
    "scopes": ["read", "write"],
    "grantType": "ClientCredentials"
  }
}
```

| `grantType` | Description |
|-------------|-------------|
| `ClientCredentials` | Server-to-server authentication (default) |
| `Password` | Resource owner password credentials |
| `AuthorizationCode` | Authorization code flow |
| `RefreshToken` | Refresh an existing token |

#### Retry Policy

```json
{
  "retryPolicy": {
    "maxRetries": 3,
    "initialDelayMs": 1000,
    "maxDelayMs": 30000,
    "backoffMultiplier": 2.0,
    "retryOnStatusCodes": [408, 429, 500, 502, 503, 504],
    "retryOnTimeout": true,
    "retryOnConnectionError": true
  }
}
```

| Property | Default | Description |
|----------|---------|-------------|
| `maxRetries` | `3` | Maximum retry attempts |
| `initialDelayMs` | `1000` | Initial delay between retries (ms) |
| `maxDelayMs` | `30000` | Maximum delay between retries (ms) |
| `backoffMultiplier` | `2.0` | Multiplier for exponential backoff |
| `retryOnStatusCodes` | `[408, 429, 500, 502, 503, 504]` | Status codes that trigger retry |
| `retryOnTimeout` | `true` | Retry on request timeout |
| `retryOnConnectionError` | `true` | Retry on connection failures |

#### Response Validation

```json
{
  "validation": {
    "expectedStatusCodes": [200, 201, 204],
    "bodyContains": "success",
    "bodyNotContains": "error",
    "jsonPathExpression": "$.data.id",
    "jsonPathExpectedValue": "123",
    "requiredHeaders": {
      "Content-Type": "application/json"
    },
    "maxResponseSizeBytes": 1048576
  }
}
```

| Property | Description |
|----------|-------------|
| `expectedStatusCodes` | Array of acceptable status codes |
| `bodyContains` | Fail if body doesn't contain this text |
| `bodyNotContains` | Fail if body contains this text |
| `jsonPathExpression` | JSONPath to extract value (e.g., `$.data.id`) |
| `jsonPathExpectedValue` | Expected value at JSONPath location |
| `requiredHeaders` | Headers that must be present in response |
| `maxResponseSizeBytes` | Maximum allowed response size |

#### Proxy Configuration

```json
{
  "proxy": {
    "url": "http://proxy.company.com:8080",
    "username": "proxyuser",
    "password": "proxypass",
    "bypassList": ["localhost", "*.internal.com"]
  }
}
```

#### Client Certificate (mTLS)

```json
{
  "clientCertificate": {
    "certificateBase64": "MIIE...",
    "password": "cert-password"
  }
}
```

Or using Windows certificate store:

```json
{
  "clientCertificate": {
    "thumbprint": "1234567890ABCDEF...",
    "storeLocation": "CurrentUser"
  }
}
```

### Practical Examples

#### Example 1: Simple GET Request

```json
{
  "url": "https://api.github.com/users/octocat",
  "method": "GET",
  "headers": {
    "Accept": "application/vnd.github.v3+json"
  }
}
```

#### Example 2: POST with JSON Body

```json
{
  "url": "https://api.example.com/orders",
  "method": "POST",
  "authentication": {
    "type": "Bearer",
    "credential": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
  },
  "body": {
    "type": "Json",
    "content": {
      "productId": "PROD-001",
      "quantity": 5,
      "customerId": "CUST-123"
    }
  },
  "validation": {
    "expectedStatusCodes": [201],
    "jsonPathExpression": "$.orderId"
  }
}
```

#### Example 3: Slack Notification

```json
{
  "url": "https://hooks.slack.com/services/your_hook_url",
  "method": "POST",
  "body": {
    "type": "Json",
    "content": {
      "text": "Daily report generated successfully! 📊",
      "channel": "#reports"
    }
  }
}
```

#### Example 4: Health Check with Validation

```json
{
  "url": "https://api.myservice.com/health",
  "method": "GET",
  "timeoutSeconds": 10,
  "validation": {
    "expectedStatusCodes": [200],
    "jsonPathExpression": "$.status",
    "jsonPathExpectedValue": "healthy"
  },
  "retryPolicy": {
    "maxRetries": 2,
    "initialDelayMs": 500
  }
}
```

#### Example 5: File Upload

```json
{
  "url": "https://api.example.com/upload",
  "method": "POST",
  "authentication": {
    "type": "ApiKey",
    "credential": "api-key-here",
    "keyName": "X-API-Key"
  },
  "body": {
    "type": "Multipart",
    "formData": {
      "description": "Monthly report",
      "category": "reports"
    },
    "files": [
      {
        "fieldName": "file",
        "fileName": "report-2024-01.pdf",
        "contentUrl": "https://internal.mycompany.com/reports/latest.pdf",
        "contentType": "application/pdf"
      }
    ]
  }
}
```

### Error Handling

The HTTP Worker distinguishes between **permanent** and **transient** errors:

#### Permanent Errors (No Retry)

These errors go directly to the Dead Letter Queue (DLQ):

| Error | Description |
|-------|-------------|
| HTTP 400 | Bad Request - Invalid request format |
| HTTP 401 | Unauthorized - Authentication failed |
| HTTP 403 | Forbidden - Access denied |
| HTTP 404 | Not Found - Resource doesn't exist |
| HTTP 405 | Method Not Allowed |
| HTTP 422 | Unprocessable Entity |
| Invalid Job Data | Missing required fields, malformed JSON |
| Configuration Errors | Missing OAuth2 TokenUrl, invalid certificate |

#### Transient Errors (Retryable)

These errors are retried according to the retry policy:

| Error | Description |
|-------|-------------|
| HTTP 408 | Request Timeout |
| HTTP 429 | Too Many Requests (Rate Limited) |
| HTTP 500 | Internal Server Error |
| HTTP 502 | Bad Gateway |
| HTTP 503 | Service Unavailable |
| HTTP 504 | Gateway Timeout |
| Connection Errors | Network failures, DNS resolution errors |
| Timeouts | Request exceeded timeout |

### Job Result

After successful execution, the job stores a result summary:

```json
{
  "statusCode": 200,
  "status": "OK",
  "contentLength": 1234,
  "body": "{\"success\": true, \"data\": {...}}"
}
```

> **Note:** Response bodies larger than 2000 characters are truncated in the result.

### Deployment

The HTTP Worker is included in the Milvaion distribution and can be deployed as a Docker container:

```yaml
# docker-compose.yml
services:
  http-worker:
    image: milvasoft/milvaion-http-worker:latest
    environment:
      - Milvaion__WorkerId=http-worker
      - Milvaion__DisplayName=HTTP Worker
      - Milvaion__RabbitMQ__Host=rabbitmq
      - Milvaion__RabbitMQ__Port=5672
      - Milvaion__RabbitMQ__Username=guest
      - Milvaion__RabbitMQ__Password=guest
      - Milvaion__MaxParallelJobs=10
```

### Best Practices

1. **Use Environment Variables for Secrets**
   - Store API keys, tokens, and passwords in environment variables
   - Reference them in job data using variable substitution (if supported)

2. **Set Appropriate Timeouts**
   - Match timeout to expected response time
   - Consider network latency for external APIs

3. **Configure Retry Policies**
   - Set reasonable retry counts (3-5 is usually sufficient)
   - Use exponential backoff to avoid overwhelming services

4. **Validate Responses**
   - Always validate expected status codes
   - Use JSONPath to verify response structure

5. **Monitor Execution**
   - Check execution logs for debugging
   - Set up alerts for repeated failures

---

## Coming Soon

Future built-in workers planned for Milvaion:

- **Email Worker** - Send emails via SMTP or email service providers
- **Database Worker** - Execute SQL queries and stored procedures
- **File Worker** - File operations (copy, move, compress, upload to cloud storage)
- **Message Queue Worker** - Send messages to other queues (Kafka, Azure Service Bus)

---

*For custom workers, see [Your First Worker](04-your-first-worker.md) and [Implementing Jobs](05-implementing-jobs.md).*
