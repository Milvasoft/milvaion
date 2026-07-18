---
id: api-keys
title: "Api Keys"
sidebar_label: "Api Keys"
sidebar_position: 121
description: "Create and manage api keys so CI pipelines, scripts and MCP clients can call the Milvaion API without an interactive login."
keywords: [milvaion api key, api authentication, service account, ci integration]
---

# Api Keys

An api key is a non-interactive credential. It lets something that cannot log in through a browser — a CI pipeline, a deployment script, an MCP client — call the Milvaion API on its own.

Keys carry the same permissions as users, drawn from the same catalog. A key is therefore never "admin by default": it can do exactly what you granted it and nothing else.

## Creating a Key

Go to **User Management → Api Keys → Create Api Key**.

| Field | Notes |
|-------|-------|
| **Name** | How you will recognise it later. Name it after the thing that will hold it: `CI pipeline`, `Claude Code - Bugra`. |
| **Description** | Optional. Useful when somebody finds the key in six months and wonders what breaks if they revoke it. |
| **Expires** | 30 days, 90 days, 1 year, or never. Fixed at creation — see [Rotating a Key](#rotating-a-key). |
| **Permissions** | The same permission catalog used by roles. Grant the minimum. |

:::danger The key is shown once

Milvaion does not store the key, only a signed record of it. The create dialog is the only place it ever appears. If you lose it, revoke it and create another — there is no way to recover it.

:::

## Using a Key

Send it in the `X-ApiKey` header:

```bash
curl -X PATCH http://localhost:5000/api/v1/jobs \
  -H "Content-Type: application/json" \
  -H "X-ApiKey: $MILVAION_API_KEY" \
  -d '{"pageNumber": 1, "rowCount": 20}'
```

Every endpoint that accepts an interactive session also accepts an api key. The permission requirements are identical — a key without `ScheduledJobManagement.List` gets the same 403 a user without it would.

The one exception is the api key endpoints themselves. Those require an interactive session, so a leaked key cannot mint replacements for itself and survive being revoked.

## Choosing Permissions

Grant the narrowest set that does the job. Two common shapes:

**Read-only monitoring** — a dashboard, an alerting script, an [MCP client](./25-mcp-server.md) you want to be safe by construction:

- `ScheduledJobManagement.List`, `ScheduledJobManagement.Detail`
- `FailedOccurrenceManagement.List`
- `WorkerManagement.List`
- `WorkflowManagement.List`

**CI pipeline that triggers a job after deploy** — the above plus:

- `ScheduledJobManagement.Trigger`

Avoid granting `App.SuperAdmin` to a key. It bypasses every other check, which defeats the point of scoping the key at all.

## Revoking and Deleting

**Revoke** stops the key working immediately while keeping the record, so the audit trail still explains what the key was and who created it. This is what you want in almost every case, including a suspected leak.

**Delete** removes the record entirely. Reserve it for housekeeping — keys created by mistake, or long-dead integrations.

Revocation takes effect at once across every API replica. Authentication caches key lookups, but revoking invalidates that cache explicitly rather than waiting for it to expire.

## Rotating a Key

Expiry is fixed at creation, and the key material cannot be regenerated for an existing record. To rotate:

1. Create a new key with the same permissions.
2. Deploy it wherever the old one is used.
3. Revoke the old key.

The **Last Used** column tells you whether step 2 actually landed everywhere. If the old key is still being used, something you forgot is still holding it.

## Rotating the Signing Secret

All keys are signed with `MilvaionConfig.ApiKey.Secret`. Rotating that secret invalidates **every** key at once, so it is a deliberate, disruptive operation — do it if you believe the secret itself leaked.

```json
{
  "MilvaionConfig": {
    "ApiKey": {
      "Secret": "your-new-secret",
      "Version": 2
    }
  }
}
```

Increment `Version` at the same time. Keys issued under an older version are then rejected with a clear message about a retired signing secret, instead of failing signature validation with no explanation. After rotating, re-create every key that is still in use.

:::warning Keep the secret out of source control

Anyone holding `ApiKey.Secret` can mint valid keys for any permission set. Supply it through an environment variable or a secret store in production:

```bash
MilvaionConfig__ApiKey__Secret=...
```

:::

## Auditing

Key creation, update, revocation and deletion are recorded as user activities, visible under **User Management → Activity Logs**.

Each key also tracks **Last Used**, written at most once every few minutes to avoid a database write per request. Treat it as "used recently", not as a precise access log.

## Next Steps

- **[MCP Server](./25-mcp-server.md)** — point an AI coding assistant at Milvaion using a read-only key
- **[Enterprise Features](./22-enterprise-features.md)** — roles, permissions and activity tracking
- **[Security](./12-security.md)** — hardening guidance for the whole deployment
