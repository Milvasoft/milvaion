---
id: secret-references
title: Secret References
sidebar_position: 28
description: Keep passwords and tokens out of job data. Reference them with {{secret:name}} and Milvaion resolves them only when it sends the job to a worker.
keywords: [milvaion secrets, job data secrets, hashicorp vault, azure key vault, secret reference, credentials in jobs]
---

# Secret References

Job data often needs a credential: an SMTP password, an API token, a database connection string. Writing it into the job data in plain text puts it in the database, in every API response, on the dashboard, in exports and in the MCP tools.

A **secret reference** keeps it out of all of those. You write a reference in the job data, and Milvaion swaps in the real value only in the message it sends to the worker.

```json
{
  "to": "ops@example.com",
  "smtpUser": "mailer",
  "smtpPassword": "{{secret:smtp-password}}",
  "authorization": "Bearer {{secret:billing-api-token}}"
}
```

The worker receives `"smtpPassword": "the-real-password"`. Everyone else, including the database, sees `{{secret:smtp-password}}`.

## How it works

1. You save a job, a workflow step or a manual trigger with references in its data. The references are stored as written.
2. When the job is due (or triggered), the API builds the RabbitMQ message. **Just before publishing**, it resolves each reference through the configured providers and puts the values into that message only.
3. The worker gets plain job data. No SDK changes are needed, so this works for workers in every language.

If any reference cannot be resolved, **nothing is published**. The occurrence is marked `Failed` with a message that names the missing secrets, for example:

```
Secret resolution failed, so the job was not published to a worker. Job data references secret(s) not found: smtp-password.
```

The message names secrets, never values. The dispatcher does not retry it, because retrying does not make a missing secret appear. Fix the secret and trigger the job again, or let the next scheduled run pick it up.

## Syntax

| Form | Meaning |
|---|---|
| `{{secret:name}}` | Look `name` up in each provider in the configured order and use the first match. |
| `{{secret:provider:name}}` | Ask one provider only, for example `{{secret:vault:apps/mail#password}}`. |
| `{{ secret:name }}` | Spaces inside the braces are allowed. |

- A reference can be a whole JSON string (`"password": "{{secret:db}}"`) or part of one (`"url": "https://{{secret:db-user}}@host"`).
- Names can hold letters, digits and `- _ . / # :`. They cannot hold spaces, quotes, backslashes or braces.
- Values are JSON-escaped when they are inserted, so a password with quotes or backslashes does not break the job data.
- References work in a job's data, in a workflow step's job data override, in run-time step data passed when a workflow is triggered, and in custom job data passed to a manual trigger.
- Provider names in references are case-insensitive. Aliases: `env`/`environment`, `config`/`configuration`, `vault`/`hashicorpvault`, `azurekeyvault`/`azure`/`keyvault`.

## Providers

Providers are consulted in the order listed in `MilvaionConfig:Secrets:Providers`. The default order is environment variables, then configuration. Vault and Azure Key Vault are used when you add them to the list or name them in a reference.

```json
{
  "MilvaionConfig": {
    "Secrets": {
      "Enabled": true,
      "Providers": [ "env", "config", "vault", "azurekeyvault" ],
      "CacheTtlSeconds": 60
    }
  }
}
```

### Environment variables (`env`)

`{{secret:smtp-password}}` reads `MILVAION_SECRET_SMTP_PASSWORD`. The name is upper-cased and every character that is not a letter or digit becomes `_`. Change the prefix with `MilvaionConfig:Secrets:Environment:Prefix`.

```yaml
# docker-compose.yml
services:
  milvaion-api:
    environment:
      - MILVAION_SECRET_SMTP_PASSWORD=${SMTP_PASSWORD}
```

Variables are read when the job is dispatched, so the API process must have them. They do not need to exist on workers.

### Configuration (`config`)

Values under `MilvaionConfig:Secrets:Values`, from any configuration source. Do not put real values in `appsettings.json` in source control; use environment variables, Docker or Kubernetes secrets mounted as configuration, or user secrets in development.

```bash
MilvaionConfig__Secrets__Values__smtp-password=the-real-password
```

### HashiCorp Vault KV v2 (`vault`)

```json
"Secrets": {
  "Vault": {
    "Address": "https://vault.example.com:8200",
    "TokenFile": "/vault/secrets/token",
    "MountPath": "secret",
    "Namespace": "",
    "DefaultKey": "value",
    "TimeoutSeconds": 10
  }
}
```

- A reference is a path and, after `#`, a key: `{{secret:vault:apps/mail#password}}` reads key `password` from `secret/data/apps/mail`. Without `#key`, `DefaultKey` is read.
- The token comes from `Token`, else from `TokenFile` (read on each use, so a token renewed by Vault Agent is picked up), else from the `VAULT_TOKEN` environment variable.
- Give the token a policy that can only `read` the paths jobs use.
- Only the KV version 2 engine is supported. Other auth methods (AppRole, Kubernetes) are not built in; run Vault Agent next to the API to log in and write a token file.

### Azure Key Vault (`azurekeyvault`)

```json
"Secrets": {
  "AzureKeyVault": {
    "VaultUri": "https://my-vault.vault.azure.net/",
    "TenantId": "",
    "ClientId": "",
    "ClientSecret": ""
  }
}
```

- `{{secret:azurekeyvault:smtp-password}}` reads the latest version; `{{secret:azurekeyvault:smtp-password/<version>}}` reads one version. Key Vault names allow only letters, digits and dashes.
- With `TenantId`, `ClientId` and `ClientSecret` set, a service principal is used. Otherwise `DefaultAzureCredential` is used (managed identity, workload identity, environment variables, Azure CLI); set only `ClientId` to pick a user-assigned managed identity.
- The identity needs the **Key Vault Secrets User** role (or a `get` secret access policy).

### Your own provider

Implement `ISecretProvider` (namespace `Milvaion.Application.Interfaces.Secrets`) and register it in the API's service collection, then add its `Name` to `Providers`:

```csharp
public class MySecretProvider : ISecretProvider
{
    public string Name => "mystore";

    public Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        // Return null when the secret does not exist, so the next provider is tried.
        // Throw only for real failures, and keep values out of exception messages.
    }
}

services.AddSingleton<ISecretProvider, MySecretProvider>();
```

## Caching

Resolved values are kept in the API's memory for `CacheTtlSeconds` (default 60) per provider and name. A rotated secret is picked up within that time. Set it to `0` to ask the provider on every dispatch. Misses are never cached, so a secret you add is found on the next dispatch.

## What never contains a value

| Place | What it holds |
|---|---|
| Jobs, workflow steps and workflow runs in the database | The reference |
| Job cache in Redis | The reference |
| Occurrence records and their logs | The reference |
| Failed occurrence records (dead letter queue) | The reference (see below) |
| API responses, the dashboard, exports, MCP tools | The reference |
| API logs | Secret names only |

**Values sent back by workers.** A worker sees the resolved data and can echo it: in a log line, an exception message, a result, or the job data it dead-letters after its last retry. Before storing any of those, the API puts the reference back in place of every value it resolved in the last `MaskRetentionMinutes` (default 24 hours). For the failed occurrence record, the API also keeps the template of each dispatched occurrence in Redis for `TemplateRetentionHours` (default 7 days, templates only), and stores that template rather than the worker's copy.

Masking is a safety net, not a guarantee. It only knows values this API instance resolved, it skips values shorter than `MinimumMaskLength` (default 4 characters), and it cannot recognise a value the worker has transformed (encoded, split, hashed). Do not log secrets in your jobs.

## Dashboard

References are shown as references. In the job data viewer each one appears as a chip with a lock icon, and the toolbar says how many the document contains. Hovering over a reference explains that it is resolved only at dispatch.

## Settings reference

| Setting | Default | Description |
|---|---|---|
| `Enabled` | `true` | When `false`, references are sent to workers as literal text. |
| `Providers` | `["env", "config"]` | Lookup order for references without a provider. |
| `CacheTtlSeconds` | `60` | How long a resolved value is cached. `0` disables the cache. |
| `MaskRetentionMinutes` | `1440` | How long a value is remembered for masking worker output. |
| `TemplateRetentionHours` | `168` | How long a dispatched occurrence's template is kept in Redis for the failed occurrence record. |
| `MinimumMaskLength` | `4` | Shorter values are not masked. |
| `Environment:Prefix` | `MILVAION_SECRET_` | Prefix of environment variables. |
| `Values` | empty | Secrets for the `config` provider. |
| `Vault:*` | | See [HashiCorp Vault](#hashicorp-vault-kv-v2-vault). |
| `AzureKeyVault:*` | | See [Azure Key Vault](#azure-key-vault-azurekeyvault). |

All settings live under `MilvaionConfig:Secrets` and can be changed without a restart where the configuration source supports reloading.

## Limits

- Resolution happens on the API. Workers need no access to the secret store, and the value travels to them inside the RabbitMQ message. Use TLS for RabbitMQ and restrict who can read the job queues.
- External jobs (Hangfire and Quartz.NET) are not dispatched by Milvaion, so their job data is not resolved.
- The worker itself can still see and leak a value. Treat worker logs accordingly.
