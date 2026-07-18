---
id: alerting
title: Alerting System
sidebar_position: 99
description: Multi-channel alerting system for Milvaion with Google Chat, Slack, Email, and Internal Notifications.
---

# Alerting System

Milvaion includes a powerful multi-channel alerting system that can send notifications through various channels when important events occur in your job scheduling infrastructure.

## Overview

The alerting system provides:

- **Multi-channel delivery**: Google Chat, Slack, Email, and Internal Notifications
- **Configurable routing**: Route specific alert types to specific channels
- **Fire-and-forget pattern**: Non-blocking alert delivery
- **Thread support**: Group related alerts in Google Chat threads
- **Production-only mode**: Optionally suppress alerts in development

## Configuration

Configure alerting in `appsettings.json` under `MilvaionConfig:Alerting`:

```json
{
  "MilvaionConfig": {
    "Alerting": {
      "MilvaionAppUrl": "https://your-milvaion-domain.com",
      "DefaultChannel": "InternalNotification",
      "SendOnlyInProduction": true,
      "Channels": {
        "GoogleChat": {
          "Enabled": true,
          "SendOnlyInProduction": true,
          "DefaultSpace": "monitoring-alerts",
          "Spaces": [
            {
              "Space": "monitoring-alerts",
              "WebhookUrl": "https://chat.googleapis.com/v1/spaces/..."
            }
          ]
        },
        "Slack": {
          "Enabled": true,
          "SendOnlyInProduction": true,
          "DefaultChannel": "alerts",
          "Channels": [
            {
              "Channel": "alerts",
              "WebhookUrl": "https://hooks.slack.com/services/..."
            }
          ]
        },
        "Email": {
          "Enabled": true,
          "SendOnlyInProduction": true,
          "DisplayName": "Milvaion Alerts",
          "From": "alerts@yourdomain.com",
          "SenderEmail": "smtp-user@yourdomain.com",
          "SenderPassword": "your-smtp-password",
          "SmtpHost": "smtp.yourdomain.com",
          "SmtpPort": 587,
          "UseSsl": true,
          "DefaultRecipients": ["admin@yourdomain.com"]
        },
        "InternalNotification": {
          "Enabled": true,
          "SendOnlyInProduction": false
        }
      },
      "Alerts": {
        "JobAutoDisabled": {
          "Enabled": true,
          "Routes": ["GoogleChat", "InternalNotification", "Email"]
        },
        "ZombieOccurrenceDetected": {
          "Enabled": true,
          "Routes": ["GoogleChat", "InternalNotification"]
        }
      }
    }
  }
}
```

### Global Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `MilvaionAppUrl` | string | - | Base URL for action links in alerts |
| `DefaultChannel` | string | `InternalNotification` | Default channel when no routes configured |
| `SendOnlyInProduction` | bool | `true` | Global production-only setting |

### Channel Options

Each channel has common options:

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Enabled` | bool | `false` | Enable/disable the channel |
| `SendOnlyInProduction` | bool | `true` | Override global production setting |

## Alert Types

Milvaion supports the following built-in alert types:

| Value | Alert Type | Description | Severity |
|-------|------------|-------------|------------------|
| 0 | `All` | Receive all alert types | - |
| 1 | `JobDispatcherMemoryUsageCritical` | Dispatcher memory usage high | Critical |
| 2 | `DatabaseConnectionFailed` | Database connection lost | Critical |
| 3 | `ZombieOccurrenceDetected` | Job stuck in running state | Warning |
| 4 | `JobAutoDisabled` | Job disabled after consecutive failures | Warning |
| 5 | `QueueDepthCritical` | Message queue depth exceeded threshold | Critical |
| 6 | `WorkerDisconnected` | Worker unexpectedly disconnected | Warning |
| 7 | `RedisConnectionFailed` | Redis connection lost | Critical |
| 8 | `RabbitMQConnectionFailed` | RabbitMQ connection lost | Critical |
| 9 | `JobExecutionFailed` | Job execution failed | Error |
| 10 | `UnknownException` | Unhandled exception occurred | Error |

### Alert Routing

Configure which channels receive which alerts:

```json
{
  "Alerts": {
    "JobAutoDisabled": {
      "Enabled": true,
      "Routes": ["GoogleChat", "InternalNotification", "Email", "Slack"]
    },
    "ZombieOccurrenceDetected": {
      "Enabled": true,
      "Routes": ["GoogleChat", "InternalNotification"]
    },
    "UnknownException": {
      "Enabled": true,
      "Routes": ["Email"]
    }
  }
}
```

## Channel Configuration

### Google Chat

Google Chat integration uses incoming webhooks with card message support.

```json
{
  "GoogleChat": {
    "Enabled": true,
    "SendOnlyInProduction": true,
    "DefaultSpace": "monitoring-alerts",
    "Spaces": [
      {
        "Space": "hub-notifications",
        "WebhookUrl": "https://chat.googleapis.com/v1/spaces/AAA/messages?key=..."
      },
      {
        "Space": "monitoring-alerts",
        "WebhookUrl": "https://chat.googleapis.com/v1/spaces/BBB/messages?key=..."
      }
    ]
  }
}
```

**Features:**
- Card-based messages with headers and sections
- Thread support for grouping related alerts
- Severity-based color coding
- Action buttons with links

**Getting a Webhook URL:**
1. Open Google Chat space
2. Click space name > Manage webhooks
3. Create webhook and copy URL

### Slack

Slack integration uses incoming webhooks.

```json
{
  "Slack": {
    "Enabled": true,
    "SendOnlyInProduction": true,
    "DefaultChannel": "alerts",
    "Channels": [
      {
        "Channel": "alerts",
        "WebhookUrl": "https://hooks.slack.com/services/T.../B.../..."
      }
    ]
  }
}
```

**Features:**
- Block Kit message formatting
- Severity-based emoji indicators
- Action buttons

**Getting a Webhook URL:**
1. Go to https://api.slack.com/apps
2. Create or select an app
3. Enable Incoming Webhooks
4. Add to Workspace and copy URL

### Email

SMTP-based email alerts with HTML formatting.

```json
{
  "Email": {
    "Enabled": true,
    "SendOnlyInProduction": true,
    "DisplayName": "Milvaion Alerts",
    "From": "alerts@yourdomain.com",
    "SenderEmail": "smtp-user@yourdomain.com",
    "SenderPassword": "your-smtp-password",
    "SmtpHost": "smtp.yourdomain.com",
    "SmtpPort": 587,
    "UseSsl": true,
    "DefaultRecipients": ["admin@yourdomain.com", "ops@yourdomain.com"]
  }
}
```

| Option | Description |
|--------|-------------|
| `DisplayName` | Sender display name |
| `From` | From email address |
| `SenderEmail` | SMTP authentication email |
| `SenderPassword` | SMTP authentication password |
| `SmtpHost` | SMTP server hostname |
| `SmtpPort` | SMTP server port (typically 587 or 465) |
| `UseSsl` | Enable SSL/TLS |
| `DefaultRecipients` | List of recipient email addresses |

### Internal Notification

:::info Note
The internal notification infrastructure exists but dashboard not integrated yet. For now, you can view notifications via api.
:::

Database-stored notifications visible in the Milvaion dashboard.

```json
{
  "InternalNotification": {
    "Enabled": true,
    "SendOnlyInProduction": false
  }
}
```

**Features:**
- Stored in database for persistence
- Visible in user dashboard
- User-specific notification preferences
- Mark as read functionality

## Docker Compose Configuration

Configure alerting using environment variables in your `docker-compose.yml`:

```yaml
services:
  milvaion-api:
    image: milvasoft/milvaion-api:latest
    environment:
      # Base URL for action links
      - MilvaionConfig__Alerting__MilvaionAppUrl=https://milvaion.example.com
      - MilvaionConfig__Alerting__SendOnlyInProduction=true
      
      # Google Chat Configuration
      - MilvaionConfig__Alerting__Channels__GoogleChat__Enabled=true
      - MilvaionConfig__Alerting__Channels__GoogleChat__DefaultSpace=monitoring-alerts
      - MilvaionConfig__Alerting__Channels__GoogleChat__Spaces__0__Space=monitoring-alerts
      - MilvaionConfig__Alerting__Channels__GoogleChat__Spaces__0__WebhookUrl=${GOOGLE_CHAT_WEBHOOK_URL}
      
      # Slack Configuration
      - MilvaionConfig__Alerting__Channels__Slack__Enabled=true
      - MilvaionConfig__Alerting__Channels__Slack__DefaultChannel=alerts
      - MilvaionConfig__Alerting__Channels__Slack__Channels__0__Channel=alerts
      - MilvaionConfig__Alerting__Channels__Slack__Channels__0__WebhookUrl=${SLACK_WEBHOOK_URL}
      
      # Email Configuration
      - MilvaionConfig__Alerting__Channels__Email__Enabled=true
      - MilvaionConfig__Alerting__Channels__Email__DisplayName=Milvaion Alerts
      - MilvaionConfig__Alerting__Channels__Email__From=alerts@example.com
      - MilvaionConfig__Alerting__Channels__Email__SenderEmail=${SMTP_USER}
      - MilvaionConfig__Alerting__Channels__Email__SenderPassword=${SMTP_PASSWORD}
      - MilvaionConfig__Alerting__Channels__Email__SmtpHost=smtp.example.com
      - MilvaionConfig__Alerting__Channels__Email__SmtpPort=587
      - MilvaionConfig__Alerting__Channels__Email__UseSsl=true
      - MilvaionConfig__Alerting__Channels__Email__DefaultRecipients__0=admin@example.com
      
      # Internal Notification (always enabled)
      - MilvaionConfig__Alerting__Channels__InternalNotification__Enabled=true
      - MilvaionConfig__Alerting__Channels__InternalNotification__SendOnlyInProduction=false
```

### Using .env File

Create a `.env` file for sensitive values:

```bash
# .env
GOOGLE_CHAT_WEBHOOK_URL=https://chat.googleapis.com/v1/spaces/AAA/messages?key=...
SLACK_WEBHOOK_URL=https://hooks.slack.com/services/T.../B.../...
SMTP_USER=smtp-user@example.com
SMTP_PASSWORD=your-smtp-password
```

Then reference in docker-compose:

```yaml
services:
  milvaion-api:
    env_file:
      - .env
```

## Kubernetes Configuration

### Using ConfigMap for Non-Sensitive Settings

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: milvaion-alerting-config
data:
  MilvaionConfig__Alerting__MilvaionAppUrl: "https://milvaion.example.com"
  MilvaionConfig__Alerting__SendOnlyInProduction: "true"
  MilvaionConfig__Alerting__DefaultChannel: "InternalNotification"
  MilvaionConfig__Alerting__Channels__GoogleChat__Enabled: "true"
  MilvaionConfig__Alerting__Channels__GoogleChat__DefaultSpace: "monitoring-alerts"
  MilvaionConfig__Alerting__Channels__Slack__Enabled: "true"
  MilvaionConfig__Alerting__Channels__Slack__DefaultChannel: "alerts"
  MilvaionConfig__Alerting__Channels__Email__Enabled: "true"
  MilvaionConfig__Alerting__Channels__Email__DisplayName: "Milvaion Alerts"
  MilvaionConfig__Alerting__Channels__Email__SmtpHost: "smtp.example.com"
  MilvaionConfig__Alerting__Channels__Email__SmtpPort: "587"
  MilvaionConfig__Alerting__Channels__Email__UseSsl: "true"
  MilvaionConfig__Alerting__Channels__InternalNotification__Enabled: "true"
```

### Using Secret for Sensitive Settings

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: milvaion-alerting-secrets
type: Opaque
stringData:
  MilvaionConfig__Alerting__Channels__GoogleChat__Spaces__0__WebhookUrl: "https://chat.googleapis.com/v1/spaces/..."
  MilvaionConfig__Alerting__Channels__Slack__Channels__0__WebhookUrl: "https://hooks.slack.com/services/..."
  MilvaionConfig__Alerting__Channels__Email__SenderEmail: "smtp-user@example.com"
  MilvaionConfig__Alerting__Channels__Email__SenderPassword: "your-smtp-password"
```

### Deployment Example

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: milvaion-api
spec:
  template:
    spec:
      containers:
        - name: milvaion-api
          image: milvasoft/milvaion-api:latest
          envFrom:
            - configMapRef:
                name: milvaion-alerting-config
            - secretRef:
                name: milvaion-alerting-secrets
```

## Built-in Alerts

Milvaion automatically sends alerts for the following events:

| Event | Alert Type | Trigger |
|-------|------------|---------|
| Job Auto-Disabled | `JobAutoDisabled` | Job disabled after consecutive failures |
| Zombie Job Detected | `ZombieOccurrenceDetected` | Job running longer than timeout |
| Connection Failures | `Redis/RabbitMQ/DatabaseConnectionFailed` | Infrastructure connection lost |

### Example Alert Messages

When a job is auto-disabled, you'll receive a card like this:

#### Google Chat
![Dashboard Overview](./src/googlechatalert.png)

#### Slack
![Dashboard Overview](./src/slackalert.png)

#### Email
![Dashboard Overview](./src/emailalert.png)

#### Internal
:::info Coming Soon
Coming soon to the dashboard...
:::


## User Notification Preferences

:::info Note
The user notification preferences infrastructure exists but dashboard not integrated yet. For now, you can make this via api.  
:::


Users can configure which alert types they receive via the dashboard:

| Setting | Description |
|---------|-------------|
| **All** | Receive all notification types |
| **Specific Types** | Select individual alert types |


## Performance Considerations

- **10-second timeout**: All channel operations timeout after 10 seconds
- **Parallel delivery**: Alerts sent to multiple channels simultaneously
- **Error isolation**: One channel failure doesn't affect others
- **Fire-and-forget**: Built-in alerts don't block main processing

## Troubleshooting

### Alerts Not Sending

1. **Check channel is enabled**: `"Enabled": true`
2. **Check production mode**: If `SendOnlyInProduction: true`, alerts only send when `ASPNETCORE_ENVIRONMENT=Production`
3. **Check alert routing**: Ensure alert type has routes configured
4. **Check logs**: Look for `AlertNotifier` or channel-specific log entries in Seq

### Google Chat Webhook Errors

```
Webhook error (403): ...
```

- Verify webhook URL is correct
- Check if webhook was deleted/regenerated
- Ensure space allows incoming webhooks

### Email Not Sending

```
SMTP authentication failed
```

- Verify `SenderEmail` and `SenderPassword`
- For Gmail: Use App Password (not regular password)
- Verify SMTP host and port

### Slack Webhook Errors

```
invalid_payload
```

- Verify webhook URL format
- Check if app is still installed in workspace
- Verify channel exists

## Security Best Practices

1. **Use Kubernetes Secrets**: Store webhook URLs and passwords securely
   ```yaml
   apiVersion: v1
   kind: Secret
   metadata:
     name: alerting-secrets
   type: Opaque
   stringData:
     SLACK_WEBHOOK: "https://hooks.slack.com/..."
   ```

2. **Rotate webhook URLs**: Periodically regenerate webhooks

3. **Limit recipients**: Only add necessary email recipients

4. **Use production-only mode**: Enable `SendOnlyInProduction` for external channels