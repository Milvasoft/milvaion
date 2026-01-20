---
id: email-worker
title: Email Worker
sidebar_position: 16
description: Pre-built Email Worker for sending emails via SMTP with full attachment and HTML support.
---

The Email Worker is a production-ready worker that sends emails via SMTP. It supports multiple SMTP configurations, HTML and plain text emails, attachments, CC/BCC, and all standard email features.

## Features

* Multiple SMTP configurations (Default, Marketing, Transactional, etc.)
* HTML and plain text email body
* File attachments (regular and inline for HTML)
* CC and BCC recipients
* Custom email headers
* Email priority levels (High, Normal, Low)
* Read and delivery receipts
* Reply-To address
* Automatic retry for transient errors
* Permanent failure detection (no retry for invalid recipients)

## Use Cases

| Scenario                 | Example                                       |
| ------------------------ | --------------------------------------------- |
| **Transactional Emails** | Order confirmations, password resets          |
| **Notifications**        | Alert emails, status updates                  |
| **Marketing Campaigns**  | Scheduled newsletter sends                    |
| **Reports**              | Daily/weekly report delivery with attachments |
| **System Alerts**        | Error notifications, monitoring alerts        |
| **Welcome Emails**       | New user onboarding sequences                 |

## Security Model

Like the SQL Worker, SMTP credentials are stored securely in the worker configuration, not in job data.

```text
Worker (appsettings.json)

EmailConfig
└─ SmtpConfigs
   └─ Default
      ├─ Host: smtp.gmail.com
      ├─ Username: ********
      └─ Password: ********

(secrets stay here)
```

Only the **configuration name (alias)** is referenced in job data:

```json
{
  "configName": "Default",
  "to": ["user@example.com"],
  "subject": "Welcome!",
  "body": "<h1>Hello!</h1>"
}
```

This design ensures that sensitive credentials never leave the worker environment.

## Worker Configuration

Configure SMTP servers in the worker's `appsettings.json`:

```json
{
  "EmailConfig": {
    "DefaultConfigName": "Default",
    "SmtpConfigs": {
      "Default": {
        "Host": "smtp.gmail.com",
        "Port": 587,
        "Username": "your-email@gmail.com",
        "Password": "your-app-password",
        "UseSsl": true,
        "DefaultFromEmail": "noreply@yourcompany.com",
        "DefaultFromName": "Your Company",
        "TimeoutSeconds": 30,
        "IgnoreCertificateErrors": false
      },
      "Marketing": {
        "Host": "smtp.sendgrid.net",
        "Port": 587,
        "Username": "apikey",
        "Password": "your-sendgrid-api-key",
        "UseSsl": true,
        "DefaultFromEmail": "marketing@yourcompany.com",
        "DefaultFromName": "Your Company Marketing",
        "TimeoutSeconds": 30
      },
      "Transactional": {
        "Host": "smtp.mailgun.org",
        "Port": 587,
        "Username": "postmaster@mg.yourcompany.com",
        "Password": "your-mailgun-password",
        "UseSsl": true,
        "DefaultFromEmail": "noreply@yourcompany.com",
        "DefaultFromName": "Your Company"
      }
    }
  }
}
```

### SMTP Configuration Properties

| Property                  | Type    | Required | Default | Description                     |
| ------------------------- | ------- | -------- | ------- | ------------------------------- |
| `Host`                    | string  | ✓        | -       | SMTP server hostname            |
| `Port`                    | number  | -        | `587`   | SMTP port (25, 465, or 587)     |
| `Username`                | string  | -        | -       | SMTP authentication username    |
| `Password`                | string  | -        | -       | SMTP authentication password    |
| `UseSsl`                  | boolean | -        | `true`  | Enable SSL/TLS encryption       |
| `DefaultFromEmail`        | string  | ✓        | -       | Default sender email address    |
| `DefaultFromName`         | string  | -        | -       | Default sender display name     |
| `TimeoutSeconds`          | number  | -        | `30`    | Connection timeout              |
| `IgnoreCertificateErrors` | boolean | -        | `false` | Skip SSL certificate validation |

## Job Data Schema

When creating an email job, provide the email details through Job Data JSON:

```json
{
  "configName": "Default",
  "to": ["recipient@example.com"],
  "cc": ["manager@example.com"],
  "bcc": ["archive@example.com"],
  "subject": "Monthly Report - January 2024",
  "body": "<h1>Monthly Report</h1><p>Please find attached...</p>",
  "isHtml": true,
  "fromEmail": "reports@yourcompany.com",
  "fromName": "Reports System",
  "replyTo": "support@yourcompany.com",
  "priority": "High",
  "attachments": [
    {
      "fileName": "report.pdf",
      "contentBase64": "JVBERi0xLjQK...",
      "contentType": "application/pdf"
    }
  ],
  "requestReadReceipt": false,
  "requestDeliveryReceipt": true
}
```

## Configuration Reference

### Main Properties

| Property                 | Type    | Required | Default        | Description                 |
| ------------------------ | ------- | -------- | -------------- | --------------------------- |
| `configName`             | string  | -        | Default config | SMTP configuration alias    |
| `to`                     | array   | ✓        | -              | Recipient email addresses   |
| `cc`                     | array   | -        | -              | CC recipients               |
| `bcc`                    | array   | -        | -              | BCC recipients (hidden)     |
| `subject`                | string  | ✓        | -              | Email subject line          |
| `body`                   | string  | ✓        | -              | Email body content          |
| `isHtml`                 | boolean | -        | `false`        | Whether body is HTML        |
| `fromEmail`              | string  | -        | From config    | Override sender email       |
| `fromName`               | string  | -        | From config    | Override sender name        |
| `replyTo`                | string  | -        | -              | Reply-To address            |
| `priority`               | enum    | -        | `Normal`       | Priority: Low, Normal, High |
| `attachments`            | array   | -        | -              | File attachments            |
| `customHeaders`          | object  | -        | -              | Custom email headers        |
| `requestReadReceipt`     | boolean | -        | `false`        | Request read receipt        |
| `requestDeliveryReceipt` | boolean | -        | `false`        | Request delivery receipt    |

### Attachment Properties

| Property        | Type    | Required | Description                                |
| --------------- | ------- | -------- | ------------------------------------------ |
| `fileName`      | string  | ✓        | File name with extension                   |
| `contentBase64` | string  | ✓        | Base64-encoded file content                |
| `contentType`   | string  | -        | MIME type (auto-detected if omitted)       |
| `isInline`      | boolean | -        | Inline attachment for HTML                 |
| `contentId`     | string  | -        | Content ID for inline (use as `cid:value`) |

---

*For custom workers, see [Your First Worker](04-your-first-worker.md) and [Implementing Jobs](05-implementing-jobs.md).*
