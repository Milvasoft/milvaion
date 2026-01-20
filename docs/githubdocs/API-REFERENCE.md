# API Reference

This document provides a comprehensive reference for the Milvaion REST API.

## Table of Contents

- [Overview](#overview)
- [Authentication](#authentication)
- [Error Handling](#error-handling)
- [Jobs API](#jobs-api)
- [Occurrences API](#occurrences-api)
- [Workers API](#workers-api)
- [Users API](#users-api)
- [Health API](#health-api)

---

## Overview

### Base URL

```
http://localhost:5000/api/v1
```

### API Documentation

Interactive API documentation is available at:
- **Scalar UI**: `http://localhost:5000/api/documentation/index.html`

### Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes* | Bearer token for authenticated endpoints |
| `Content-Type` | Yes | `application/json` for POST/PUT requests |
| `Accept-Language` | No | Language code (e.g., `en-US`, `tr-TR`) |

### Response Format

All responses follow this structure:

```json
{
  "isSuccess": true,
  "messages": [],
  "statusCode": 200,
  "data": { ... }
}
```

**Error Response:**
```json
{
  "isSuccess": false,
  "messages": [
    {
      "message": "Validation failed",
      "type": 2
    }
  ],
  "statusCode": 400,
  "data": null
}
```

---

## Authentication

### Login

Authenticate and receive access tokens.

```http
POST /api/v1/auth/login
```

**Request Body:**
```json
{
  "userName": "rootuser",
  "password": "your-password"
}
```

**Response:**
```json
{
  "isSuccess": true,
  "data": {
    "accessToken": "eyJhbGciOiJIUzI1NiIs...",
    "refreshToken": "dGhpcyBpcyBhIHJlZnJlc2g...",
    "expiresAt": "2024-01-15T12:00:00Z"
  }
}
```

### Refresh Token

```http
POST /api/v1/auth/refresh
```

**Request Body:**
```json
{
  "refreshToken": "dGhpcyBpcyBhIHJlZnJlc2g..."
}
```

### Using Authentication

Include the access token in requests:

```http
Authorization: Bearer eyJhbGciOiJIUzI1NiIs...
```

### Token Status Codes

| Code | Meaning | Action |
|------|---------|--------|
| 401 | Invalid/missing token | Re-authenticate |
| 419 | Token expired | Refresh or re-authenticate |
| 403 | Insufficient permissions | Check user roles |

---

## Error Handling

### HTTP Status Codes

| Code | Meaning |
|------|---------|
| 200 | Success |
| 201 | Created |
| 400 | Bad Request (validation error) |
| 401 | Unauthorized |
| 403 | Forbidden |
| 404 | Not Found |
| 409 | Conflict |
| 419 | Token Expired |
| 500 | Internal Server Error |

### Error Message Types

| Type | Value | Description |
|------|-------|-------------|
| Information | 0 | Informational message |
| Warning | 1 | Warning message |
| Error | 2 | Error message |

---

## Jobs API

### List Jobs

Retrieve a paginated list of jobs.

```http
PATCH /api/v1/jobs
```

**Request Body:**
```json
{
  "pageNumber": 1,
  "rowCount": 10,
  "filtering": {
    "criterias": [
      {
        "filterBy": "Id",
        "value": 1,
        "otherValue": null,
        "type": 5
      }
    ]
  },
  "sorting": {
    "sortBy": "Id",
    "type": 0
  },
  "aggregation": {
    "criterias": [
      {
        "aggregateBy": "Id",
        "type": 3
      }
    ]
  }
}
```

**Example:**
```http
GET /api/v1/jobs?pageIndex=1&requestedItemCount=20&isActive=true
```

**Response:**
```json
{
  "isSuccess": true,
  "statusCode": 200,
  "messages": [
    {
      "key": null,
      "message": "İşlem başarıyla gerçekleşti!",
      "type": 0
    }
  ],
  "currentPageNumber": 1,
  "totalPageCount": 0,
  "totalDataCount": 0,
  "data": [...],
  "aggregationResults": [
    {
      "aggregatedBy": "Id",
      "type": 3,
      "result": null
    }
  ],
  "metadatas": []
}
```

### Get Job

Retrieve a single job by ID.

```http
GET /api/v1/jobs/{id}
```

**Response:**
```json
{
  "isSuccess": true,
  "data": {
    "id": "019b66dd-9a70-7c0b-957b-f93d2ab083c9",
    "displayName": "Daily Report",
    "description": "Generates daily sales report",
    "tags": "reports,daily",
    "workerId": "report-worker",
    "jobType": "GenerateReportJob",
    "jobData": "{\"reportType\": \"sales\"}",
    "cronExpression": "0 9 * * *",
    "executeAt": null,
    "isActive": true,
    "concurrentExecutionPolicy": 0,
    "timeoutMinutes": 30,
    "version": 1,
    "auditInfo": {
      "creationDate": "2024-01-01T10:00:00Z",
      "creatorUserName": "admin"
    }
  }
}
```

### Create Job

Create a new scheduled job.

```http
POST /api/v1/jobs/job
```

**Request Body:**
```json
{
  "displayName": "My New Job",
  "description": "Job description",
  "tags": "tag1,tag2",
  "workerId": "sample-worker-01",
  "selectedJobName": "SampleJob",
  "cronExpression": "*/5 * * * *",
  "executeAt": null,
  "isActive": true,
  "concurrentExecutionPolicy": 0,
  "timeoutMinutes": 10,
  "jobData": "{\"key\": \"value\"}"
}
```

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `displayName` | string | Yes | Human-readable job name |
| `description` | string | No | Job description |
| `tags` | string | No | Comma-separated tags |
| `workerId` | string | Yes | Target worker ID |
| `selectedJobName` | string | Yes | Job class name in worker |
| `cronExpression` | string | Conditional | Cron schedule (or use `executeAt`) |
| `executeAt` | datetime | Conditional | One-time execution time |
| `isActive` | bool | No | Active status (default: true) |
| `concurrentExecutionPolicy` | int | No | 0=Skip, 1=Queue |
| `timeoutMinutes` | int | No | Execution timeout |
| `jobData` | string | No | JSON payload for job |

**Response:**
```json
{
  "isSuccess": true,
  "statusCode": 201,
  "data": {
    "id": "019b66dd-9a70-7c0b-957b-f93d2ab083c9"
  }
}
```

### Update Job

Update an existing job.

```http
PUT /api/v1/jobs/{id}
```

**Request Body:**
```json
{
  "displayName": { "value": "Updated Name", "isUpdated": true },
  "cronExpression": { "value": "0 */10 * * *", "isUpdated": true },
  "isActive": { "value": false, "isUpdated": true }
}
```

> **Note:** Use the `isUpdated` pattern to specify which fields to update.

### Delete Job

Delete a job.

```http
DELETE /api/v1/jobs/{id}
```

### Trigger Job

Manually trigger a job execution.

```http
POST /api/v1/jobs/job/trigger
```

**Request Body:**
```json
{
  "jobId": "019b66dd-9a70-7c0b-957b-f93d2ab083c9",
  "reason": "Manual trigger for testing"
}
```

### Toggle Job Status

Activate or deactivate a job.

```http
PATCH /api/v1/jobs/{id}/toggle
```

---

## Occurrences API

### List Occurrences

Retrieve job execution history.

```http
GET /api/v1/occurrences
```

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `pageIndex` | int | Page number |
| `requestedItemCount` | int | Items per page |
| `jobId` | guid | Filter by job ID |
| `status` | int | Filter by status |
| `workerId` | string | Filter by worker ID |
| `startDate` | datetime | Filter by start date |
| `endDate` | datetime | Filter by end date |

**Status Values:**

| Value | Status |
|-------|--------|
| 0 | Queued |
| 1 | Running |
| 2 | Completed |
| 3 | Failed |
| 4 | Cancelled |
| 5 | TimedOut |
| 6 | Unknown |

**Response:**
```json
{
  "isSuccess": true,
  "data": {
    "dtoList": [
      {
        "id": "019b76ce-d6f8-789b-aff1-bb417921776b",
        "jobId": "019b66dd-9a70-7c0b-957b-f93d2ab083c9",
        "jobName": "SampleJob",
        "workerId": "sample-worker-01-abc123",
        "status": 2,
        "startTime": "2024-01-15T10:00:00Z",
        "endTime": "2024-01-15T10:00:05Z",
        "durationMs": 5000,
        "result": "Job completed successfully",
        "retryCount": 0
      }
    ],
    "totalDataCount": 1000
  }
}
```

### Get Occurrence

Retrieve a single occurrence with full details.

```http
GET /api/v1/occurrences/{id}
```

**Response:**
```json
{
  "isSuccess": true,
  "data": {
    "id": "019b76ce-d6f8-789b-aff1-bb417921776b",
    "jobId": "019b66dd-9a70-7c0b-957b-f93d2ab083c9",
    "jobName": "SampleJob",
    "correlationId": "019b76ce-d6f8-789b-aff1-bb417921776b",
    "workerId": "sample-worker-01-abc123",
    "status": 2,
    "startTime": "2024-01-15T10:00:00Z",
    "endTime": "2024-01-15T10:00:05Z",
    "durationMs": 5000,
    "result": "Job completed successfully",
    "exception": null,
    "logs": [
      {
        "timestamp": "2024-01-15T10:00:01Z",
        "level": "Information",
        "message": "Starting job execution..."
      },
      {
        "timestamp": "2024-01-15T10:00:05Z",
        "level": "Information",
        "message": "Job completed successfully"
      }
    ],
    "statusChangeLogs": [
      {
        "timestamp": "2024-01-15T10:00:00Z",
        "from": 0,
        "to": 1
      },
      {
        "timestamp": "2024-01-15T10:00:05Z",
        "from": 1,
        "to": 2
      }
    ],
    "retryCount": 0,
    "lastHeartbeat": "2024-01-15T10:00:05Z",
    "jobVersion": 1
  }
}
```

### Cancel Occurrence

Cancel a running or queued occurrence.

```http
POST /api/v1/occurrences/{id}/cancel
```

### Get Occurrence Statistics

Get aggregated statistics.

```http
GET /api/v1/occurrences/statistics
```

**Query Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `jobId` | guid | Filter by job ID |
| `startDate` | datetime | Start of period |
| `endDate` | datetime | End of period |

**Response:**
```json
{
  "isSuccess": true,
  "data": {
    "totalExecutions": 10000,
    "successfulExecutions": 9850,
    "failedExecutions": 150,
    "averageDurationMs": 5230,
    "successRate": 98.5
  }
}
```

---

## Workers API

### List Workers

Retrieve registered workers.

```http
GET /api/v1/workers
```

**Response:**
```json
{
  "isSuccess": true,
  "data": {
    "dtoList": [
      {
        "workerId": "email-worker",
        "instanceId": "email-worker-abc123",
        "status": "Online",
        "lastHeartbeat": "2024-01-15T10:00:00Z",
        "registeredJobs": ["SendEmailJob", "SendBulkEmailJob"],
        "runningJobsCount": 5,
        "version": "1.0.0"
      }
    ]
  }
}
```

### Get Worker Details

```http
GET /api/v1/workers/{workerId}
```

### Get Worker Jobs

Get available job types for a worker.

```http
GET /api/v1/workers/{workerId}/jobs
```

**Response:**
```json
{
  "isSuccess": true,
  "data": [
    {
      "jobName": "SendEmailJob",
      "description": "Sends email via SMTP"
    },
    {
      "jobName": "SendBulkEmailJob",
      "description": "Sends bulk emails"
    }
  ]
}
```

---

## Users API

### List Users

```http
GET /api/v1/users
```

### Get Current User

```http
GET /api/v1/users/me
```

### Create User

```http
POST /api/v1/users
```

**Request Body:**
```json
{
  "userName": "newuser",
  "email": "newuser@example.com",
  "password": "SecurePassword123!",
  "roles": ["Operator"]
}
```

### Update User

```http
PUT /api/v1/users/{id}
```

### Delete User

```http
DELETE /api/v1/users/{id}
```

### Change Password

```http
POST /api/v1/users/change-password
```

**Request Body:**
```json
{
  "currentPassword": "OldPassword123!",
  "newPassword": "NewPassword456!",
  "confirmPassword": "NewPassword456!"
}
```

---

## Health API

### Liveness Check

Basic health check to verify the service is running.

```http
GET /api/v1/healthcheck/live
```

**Response:**
```json
{
  "status": "Healthy",
  "timestamp": "2024-01-15T10:00:00Z",
  "uptime": "5.12:30:45"
}
```

### Readiness Check

Comprehensive health check including dependencies.

```http
GET /api/v1/healthcheck/ready
```

**Response:**
```json
{
  "status": "Healthy",
  "duration": "00:00:00.0150000",
  "timestamp": "2024-01-15T10:00:00Z",
  "checks": [
    {
      "name": "PostgreSQL",
      "status": "Healthy",
      "description": "Database connection is healthy",
      "duration": "00:00:00.0100000"
    },
    {
      "name": "Redis",
      "status": "Healthy",
      "description": "Redis connection is healthy",
      "duration": "00:00:00.0030000"
    },
    {
      "name": "RabbitMQ",
      "status": "Healthy",
      "description": "RabbitMQ connection is healthy",
      "duration": "00:00:00.0020000"
    }
  ]
}
```

---

## Failed Occurrences API

### List Failed Occurrences

```http
GET /api/v1/failed-occurrences
```

### Get Failed Occurrence

```http
GET /api/v1/failed-occurrences/{id}
```

### Resolve Failed Occurrence

```http
PUT /api/v1/failed-occurrences/{id}
```

**Request Body:**
```json
{
  "resolutionNote": { "value": "Fixed data and requeued", "isUpdated": true },
  "resolutionAction": { "value": "ManualRetry", "isUpdated": true }
}
```

### Retry Failed Occurrence

```http
POST /api/v1/failed-occurrences/{id}/retry
```

---

## Pagination

All list endpoints support pagination with these parameters:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `pageIndex` | 1 | Page number (1-based) |
| `requestedItemCount` | 20 | Items per page (max: 100) |

**Response includes:**
```json
{
  "dtoList": [...],
  "totalDataCount": 500,
  "totalPageCount": 25
}
```

---

## Rate Limiting

API requests may be rate-limited. When rate limited, you'll receive:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 60
```

---

## Versioning

The API uses URL versioning:

```
/api/v1/...   # Current version
/api/v2/...   # Future version
```

---

## WebSocket (SignalR)

Real-time updates are available via SignalR:

**Hub URL:** `/hubs/dashboard`

**Events:**
- `OccurrenceStatusChanged` - Job status updates
- `WorkerStatusChanged` - Worker online/offline
- `JobCreated` - New job created
- `JobUpdated` - Job modified

**JavaScript Example:**
```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/dashboard")
    .build();

connection.on("OccurrenceStatusChanged", (data) => {
    console.log("Status changed:", data);
});

await connection.start();
```

---

## Further Reading

- [Configuration Reference](../portaldocs/06-configuration.md)
- [Worker SDK Guide](./WORKER-SDK.md)
- [Security Guide](../portaldocs/12-security.md)
