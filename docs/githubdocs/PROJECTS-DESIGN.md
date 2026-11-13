# Projects (Multi-Tenancy) Design

**Status:** Proposal. Nothing in this document is implemented yet. Implementation starts after the open decisions at the end are settled.

This document describes how several teams can share one Milvaion installation while their jobs, workflows, workers, occurrences, alerts, reports and API keys stay apart. It is written against the code as of November 2026. File references point at the places that will change.

## Table of Contents

- [Goals and Non-Goals](#goals-and-non-goals)
- [Where Milvaion Stands Today](#where-milvaion-stands-today)
- [Concepts](#concepts)
- [Data Model](#data-model)
- [Migration of Existing Data](#migration-of-existing-data)
- [Access: Resolving and Enforcing the Current Project](#access-resolving-and-enforcing-the-current-project)
- [Workers](#workers)
- [Scheduler, Redis and SignalR](#scheduler-redis-and-signalr)
- [Alerts, Reports and Activity Logs](#alerts-reports-and-activity-logs)
- [Dashboard UX](#dashboard-ux)
- [Limits and Quotas](#limits-and-quotas)
- [Security Risks and Tests](#security-risks-and-tests)
- [Rollout Plan](#rollout-plan)
- [Alternatives Considered](#alternatives-considered)
- [Open Decisions](#open-decisions)

---

## Goals and Non-Goals

**Goals**

- One installation, many teams. Each team works in a **project** and, by default, sees nothing that belongs to another project.
- Isolation is enforced on the server, in one place, and fails closed. A forgotten filter should return nothing rather than everything.
- Existing installations upgrade with no manual step. Everything they have today ends up in a project called **Default**, and it behaves exactly as it does now.
- Existing workers keep working without a code or configuration change, in every SDK (.NET, Node, Go, Python, Java, PHP, and the Rust and Ruby SDKs being added in parallel).
- Existing REST clients, the CLI, the Terraform provider and MCP clients keep working against the Default project without sending anything new.

**Non-goals**

- Hard, hostile multi-tenancy, where tenants are untrusted organisations that may attack each other. Projects separate teams inside one organisation that share an operator. For hostile tenants, we recommend a separate deployment (see [Alternatives Considered](#alternatives-considered)).
- Per-project databases or schemas.
- Per-project branding, languages or CMS content. These stay instance-wide.

## Where Milvaion Stands Today

A short survey of the current model, because each item shapes a decision below.

| Area | Today | Consequence for projects |
|---|---|---|
| Core entities | `ScheduledJob`, `JobOccurrence`, `JobOccurrenceLog`, `FailedOccurrence`, `Workflow`, `WorkflowRun`, `MetricReport` live in `src/Sdk/Milvasoft.Milvaion.Sdk/Domain`. None has an owner or scope field. | They need a `ProjectId`. |
| Query path | MediatR handlers use `IMilvaionRepositoryBase<T>` (Milvasoft repository, which applies soft delete). Several handlers and most background services use `MilvaionDbContext` directly or raw SQL through `IMilvaionDbContextAccessor` (`GetScheduledJobList`, `GetJobOccurenceList`, `LogSummary`, `JobDispatcherService`, `StatusTrackerService`). `MilvaionDbContext` has no `HasQueryFilter`. | A repository-only filter is not enough. The filter must live in EF, and the raw SQL paths need explicit handling. |
| Users and permissions | Users get permissions only through roles. The JWT carries the user name and one role claim per permission (`AccountManager.cs`). `[Auth(perm)]` checks `App.SuperAdmin` or that permission. | Permissions become project-dependent, so they cannot all live in a token issued before the project is chosen. |
| Super admin | Permission `App.SuperAdmin`, role `SuperAdmin`, user `rootuser`, all seeded with Id 1 (`DatabaseMigrator.cs`). | This becomes the global admin. |
| OIDC and LDAP | IdP groups become roles. OIDC has `RoleMappings` from IdP role to local role. LDAP has no mapping. Roles are re-evaluated at every sign-in (`ExternalIdentityService.cs`). | Group-to-project mapping fits the same place. |
| API keys | HMAC-signed JWT (`jti`, `kv`), checked by `ApiKeyAuthenticationHandler` from the `X-ApiKey` header. Permissions attach directly to the key. | Keys get a project. |
| MCP | `/mcp`, stateless HTTP, accepts a bearer token or `X-ApiKey`. Each tool calls `McpPermissionGuard.Require(...)` and then a MediatR request. | Tools inherit scoping from the handlers. No per-tool project argument is needed. |
| Workers | Live in Redis only (`workers:{workerId}` and related keys). `WorkerId` is one global namespace. Workers authenticate only with broker credentials, and no SDK holds an API key. | Two teams picking the same `WorkerId` collide. Any worker can claim any `WorkerId`. |
| Queues | `jobs.topic` exchange. Job queues are `scheduled_jobs_queue.{pattern}`, with the pattern defaulting to `{workerid}.{job}.*`. Status, log, heartbeat and registration queues are single shared queues. | The project must reach the routing key for non-Default projects. |
| Redis | Everything is under `KeyPrefix` (default `Milvaion:JobScheduler:`). One global schedule ZSET, global stats counters, one cancellation channel. | Some keys stay global, some get a project segment. |
| SignalR | One hub, `JobsHub`. Every reader joins `occurrence_readers` and receives every occurrence. `SubscribeToOccurrence` checks only the read permission, not which occurrence it is. | Groups must become per project. |
| Alerts | Channels and webhooks in appsettings `MilvaionConfig:Alerting`. Rules per `AlertType` in the single `AppSetting` row. Internal recipients are users whose `AllowedNotifications` match. | Alerts are global today and need a project dimension. |
| Reports | The Reporter worker runs raw Dapper SQL over all occurrences and writes `MetricReports`. | Reports need a project column and per-project SQL. |
| External jobs | Hangfire and Quartz jobs are matched by `ExternalJobId` alone (`ExternalJobTrackerService.cs`). | Must become `(ProjectId, ExternalJobId)`. |

There is no existing tenant, team or project concept. `Domain/ContentManagement/Namespace.cs` belongs to the CMS and is unrelated.

## Concepts

### Project

A project is the unit of isolation. It owns jobs, workflows, workers, occurrences, failures, logs, reports, alert rules and API keys.

| Field | Notes |
|---|---|
| `Id` | `int`. The Default project is `1`. An `int` keeps the column small on the largest tables (occurrences and logs). |
| `Slug` | Lowercase, `[a-z0-9-]{2,40}`, unique, immutable after creation. Used in headers, URLs, routing keys and Redis keys. The Default project's slug is `default`. |
| `Name`, `Description` | Display only. |
| `IsDefault` | Exactly one project. It cannot be deleted or archived. |
| `ArchivedAt` | An archived project is read-only: its schedules are paused and it accepts no new jobs. |
| `Settings` | jsonb: notification rules, quotas, retention overrides. |

### Membership

A user is a member of zero or more projects. A membership is `(UserId, ProjectId, RoleId, Source)`:

- `RoleId` points at a **project role** (see below). One role per membership keeps the model readable. If someone needs more, define a role that combines the permissions.
- `Source` is `Local`, `Oidc` or `Ldap`. External memberships are replaced at each sign-in. Local ones are left alone. This mirrors how external roles behave today.

Users stay global. A person signs in once and switches between their projects.

### Two kinds of permission

The permission catalog (`PermissionCatalog.cs`) is split by scope. A new `[PermissionScope]` attribute on each group in the catalog marks the scope:

| Scope | Groups |
|---|---|
| **Project** | `ScheduledJobManagement`, `WorkflowManagement`, `FailedOccurrenceManagement`, `WorkerManagement`, `ApiKeyManagement`, plus new `ProjectSettings.Update` and `ProjectMembers.{List,Update}` |
| **Instance** | `App.SuperAdmin`, `UserManagement`, `RoleManagement`, `PermissionManagement`, `ContentManagement`, `NamespaceManagement`, `ResourceGroupManagement`, `LanguageManagement`, `SystemAdministration`, `InternalNotificationManagement`, plus new `ProjectManagement.{List,Create,Update,Archive,Delete}` |
| **Both** | `ActivityLogManagement.List`: inside a project it lists that project's activity, at instance level it lists everything. |

### Roles

`Role` gets a `Scope` column (`Instance` or `Project`):

- **Instance roles** hold only instance permissions and attach to users directly, as today (`UserRoleRelation`).
- **Project roles** hold only project permissions and attach through memberships. The same role definition is reused in every project. Project roles are defined by instance admins, not by each project.

Three project roles are seeded:

| Role | Permissions |
|---|---|
| `Project Owner` | Every project permission, including members, settings and API keys. |
| `Project Editor` | Jobs, workflows, failures and workers: everything except members, settings and API keys. |
| `Project Viewer` | `List` and `Detail` only. |

### Global admin

`App.SuperAdmin` stays what it is: it passes every check in every project, and it sees the instance-wide views (all projects at once). The root user keeps it. No other path grants cross-project visibility, with one exception: an optional instance role `Instance Auditor`, with a new `ProjectManagement.ReadAll` permission, gives read-only access to every project without being a member. This is useful for on-call and operations teams.

## Data Model

### New tables

```text
Projects            (Id int PK, Slug unique, Name, Description, IsDefault, ArchivedAt, Settings jsonb, audit columns)
ProjectMemberships  (Id int PK, ProjectId FK, UserId FK, RoleId FK, Source, audit columns)
                    unique (ProjectId, UserId)
```

### Entities that get a `ProjectId`

All of them get `ProjectId int NOT NULL`, with a foreign key to `Projects`, unless noted otherwise.

| Entity | Why it is denormalised rather than joined |
|---|---|
| `ScheduledJob` | Owner. |
| `Workflow` | Owner. |
| `JobOccurrence` | Copied from the job at creation. List and search queries filter on it without a join to the job table. |
| `JobOccurrenceLog` | Copied from the occurrence. `search_logs` and `summarize_logs` scan logs, and a join to occurrences on every scan is too expensive. |
| `FailedOccurrence` | Copied from the job. |
| `WorkflowRun` | Copied from the workflow. |
| `MilvaionApiKey` | **Nullable.** `NULL` means an instance key, which only a super admin can create. It is used by the CLI and Terraform to manage projects themselves. |
| `MetricReport` | **Nullable.** `NULL` means an instance-wide report, visible only at instance level. |
| `ActivityLog` | **Nullable.** `NULL` for instance actions such as user or role changes. |
| `InternalNotification` | **Nullable.** Notifications about project resources carry the project, so the panel can filter. |

These stay global: `User`, `Role`, `Permission`, the relation tables, `UserSession`, `AppSetting` (branding and instance defaults), the CMS and UI tables, and `Language`.

Workers are not a table today, and this design does not add one (see [Workers](#workers)).

All project-scoped entities implement a marker interface in the SDK domain:

```csharp
public interface IProjectScoped
{
    int ProjectId { get; set; }
}
```

This is an additive change to `Milvasoft.Milvaion.Sdk`. Worker code never constructs these entities, so it does not affect workers.

### Indexes

Every list screen filters by project first, so the existing list indexes get `ProjectId` as their leading column instead of gaining a separate index:

| Table | Index |
|---|---|
| `ScheduledJobs` | `(ProjectId, IsActive)`; `(ProjectId, WorkerId, JobNameInWorker)`, which serves routing sync and worker detail |
| `JobOccurrences` | `(ProjectId, CreatedAt DESC)`; `(ProjectId, Status)` |
| `JobOccurrenceLogs` | `(ProjectId, Timestamp DESC)` |
| `FailedOccurrences` | `(ProjectId, Resolved, FailedAt DESC)` replaces `(Resolved, FailedAt)`; `(ProjectId, FailureType, Resolved)` replaces `(FailureType, Resolved)` |
| `Workflows` | `(ProjectId, IsActive)` |
| `WorkflowRuns` | `(ProjectId, CreatedAt DESC)`; `(ProjectId, Status)` |
| `MetricReports` | `(ProjectId, MetricType, GeneratedAt DESC)` |
| `ScheduledJobs` (external) | unique `(ProjectId, ExternalJobId)` where `IsExternal` |
| `MilvaionApiKeys` | `(ProjectId)` |
| `ActivityLogs` | `(ProjectId, ActivityDate DESC)` |

Lookups by primary key (job by id, occurrence by id) do not need `ProjectId` in the index. The filter is applied to the single row found.

On the large tables, indexes are created with `CREATE INDEX CONCURRENTLY` in a separate migration, so the upgrade does not lock writes. EF migrations run in a transaction, so this migration uses `migrationBuilder.Sql(..., suppressTransaction: true)`.

### The EF query filter

The filter lives in `MilvaionDbContext`, not in the repository, because a large share of reads go through the context directly.

```csharp
public class MilvaionDbContext(DbContextOptions options, IProjectContext projectContext) : MilvaBulkDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(IProjectScoped).IsAssignableFrom(t.ClrType)))
        {
            // Named filter (EF Core 10+), so soft delete and other filters stay independent.
            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(ProjectFilter.Name, BuildFilter(entityType.ClrType));
            //  e => projectContext.Bypass || e.ProjectId == projectContext.ProjectId
        }
        ...
    }
}
```

The rules:

1. **`IProjectContext` has three states.**
   - `Resolved(projectId)`: a normal request.
   - `System`: entered only through an explicit `using var _ = projectContext.EnterSystemScope("reason");`. Used by background services and by the super admin's instance-wide views.
   - `Unresolved`: the default. In this state `ProjectId` is `-1`, so the filter matches nothing. **Forgetting to resolve a project returns empty results, not all results.**
2. **Writes.** A `SaveChanges` interceptor:
   - stamps `ProjectId` on added `IProjectScoped` entities from the context;
   - rejects an add in the `Unresolved` state;
   - rejects any modification that changes `ProjectId` (moving resources between projects is a separate, explicit admin operation; see [Open Decisions](#open-decisions));
   - rejects an add or update whose `ProjectId` differs from the resolved project, except in the `System` state.
3. **`ExecuteUpdate` and `ExecuteDelete`** respect query filters, so bulk operations such as `delete_occurrences` are covered.
4. **Raw SQL and Dapper** are not covered. Each such path gets an explicit `"ProjectId" = @projectId` clause. A unit test lists every raw SQL call site (via a `[RawSqlProjectScoped]` attribute on the method, checked by a reflection test against a known list) so a new raw query cannot appear without someone deciding its scope.
5. **Cross-references are validated.** A workflow definition references jobs by id inside jsonb. The workflow command handlers load the referenced jobs through the filtered context and reject ids that come back missing. The same applies to `FailedOccurrence` retry, `JobId` on occurrence queries, and `WorkerId` on job create or update (the worker must belong to the project).

Background services such as the dispatcher, the status tracker, the zombie detector, the workflow engine and the log collector run in `System` scope. They operate on all projects by design. When they **write**, they copy `ProjectId` from the parent row (job, occurrence or run), never from a message.

## Migration of Existing Data

One EF migration, owned by this feature, run after the scheduling feature's migration has merged:

1. Create `Projects` and insert `Default` with `Id = 1`, `Slug = default`, `IsDefault = true`.
2. Add `ProjectId int NOT NULL DEFAULT 1` to every table in the list above (nullable on the four nullable ones, filled with `1` for API keys, reports, activity logs and notifications that concern project resources).

   In PostgreSQL 11 and later, adding a column with a constant default is a catalog-only change. Existing rows are not rewritten, so even a very large `JobOccurrenceLogs` table upgrades in milliseconds. A later step drops the `DEFAULT` so new rows must set the value explicitly; existing rows keep `1`.
3. Add the foreign keys with `NOT VALID`, then `VALIDATE CONSTRAINT` in a separate statement. Validation does not block writes.
4. Create the project-prefixed indexes `CONCURRENTLY` and drop the ones they replace.
5. Data:
   - Add `Scope` to `Roles`. Every existing role becomes `Instance` scope.
   - Seed the three project roles.
   - **Split mixed roles.** Every existing non-SuperAdmin role that holds project permissions is split. Its instance permissions stay on the instance role. Its project permissions move to a new project role named `{RoleName} (project)`. Every user who held the original role gets a Default-project membership with the new project role.
   - Result: every existing user can do exactly what they could before, inside the Default project.
   - Every existing API key gets `ProjectId = 1`.
6. Redis: the dispatcher rebuilds the job cache on startup, and the worker registry repopulates from heartbeats within one heartbeat interval. The only Redis migration is the one-off renaming of stats counters described under [Redis](#redis).

There is no downtime beyond the usual rolling restart. Old API instances cannot run against the migrated schema, because EF would fail on the `NOT NULL` column once the default is dropped. That is why the default is dropped in a **follow-up** release, not in the same one.

## Access: Resolving and Enforcing the Current Project

### How a request picks its project

In order, first match wins:

1. **API key bound to a project.** The key's project is the request's project. If the request also sends a project header naming a different project, it is rejected with `403`. A project key cannot be pointed at another project.
2. **Header `X-Milvaion-Project: <slug>`.** Used by the dashboard, the CLI, Terraform and MCP clients.
3. **Query string `?project=<slug>`.** Only for the SignalR handshake and file downloads, where a browser cannot set headers.
4. **Nothing sent.** The request falls back to the Default project. This keeps every existing client, script and MCP configuration working unchanged.

   An instance option, `MilvaionConfig:Projects:RequireExplicitProject`, turns this fallback off once all clients have been updated.

**Why a header and not a route segment** (`/api/v1/projects/{slug}/jobs`): a route segment changes every URL, breaks every existing client, the OpenAPI document, the CLI and the Terraform provider, and still needs the same middleware. A header is additive. It also matches how MCP clients are configured: a fixed URL plus a set of headers.

**Why not a claim in the token:** a user's project changes when they switch in the dashboard, and re-issuing tokens on every switch complicates refresh and multi-tab use. Claims stay for identity. The project is request context.

### How permissions are evaluated

`[Auth(perm)]` already works by checking role claims (`AuthAttribute.cs`). The design keeps that attribute and every controller unchanged, and adds an `IClaimsTransformation` that runs after authentication:

1. Read the current user's id. A `NameIdentifier` claim is added to the JWT, which today carries only the user name. API keys already carry `akid`.
2. Resolve the project as described above. Check the project exists and is not archived (archived projects allow `GET` only).
3. **Users:** load the membership map from Redis, `{prefix}p:membership:{userId}` → `{projectId: [permissions]}`, falling back to the database. Strip any project-scoped permission claims from the token, then add the permissions of the membership in the current project.

   Super admins get every permission. Instance auditors get `List` and `Detail` permissions. A user who is not a member of the project gets `403 Not a member of project 'x'`. It is a `403`, not a `404`, because the slug space is not secret inside an organisation.
4. **API keys:** a project key keeps its own permissions, which are already all project permissions. An instance key can act on a project only if it holds `App.SuperAdmin`, which today cannot be granted to keys, or the new `ProjectManagement.ReadAll`.
5. Set `IProjectContext` to `Resolved(projectId)` for the rest of the request.

The membership cache entry is deleted whenever a membership or role changes. The existing security stamp (`AddUserSecurityStamp` migration) covers sign-out on role changes. Membership changes take effect on the next request, because the map is read per request rather than baked into the token.

**OIDC users** authenticate with the IdP's token directly, and their permission claims are built in `ExternalIdentityService.GetOrBuildClaimsAsync`. That method also builds the membership map, and the claims transformation then treats OIDC and local users the same way.

### OIDC and LDAP group mapping

`OidcOptions` and `LdapOptions` gain the same two settings:

```json
"Oidc": {
  "ProjectMappings": {
    "payments-devs": "payments:Project Editor",
    "payments-leads": "payments:Project Owner",
    "sre": "*:Project Viewer"
  },
  "ProjectGroupPattern": "milvaion-{project}-{role}"
}
```

- `ProjectMappings` maps an IdP group to `project-slug:role-name`. The project `*` means every project.
- `ProjectGroupPattern` is an optional convention. A group `milvaion-payments-editor` becomes a membership in `payments` with the role whose slug is `editor` (`owner`, `editor` and `viewer` are the seeded role slugs).
- Memberships derived this way get `Source = Oidc/Ldap` and are replaced at each sign-in, exactly as external roles are today. A group that names a project that does not exist is logged and ignored. Projects are never auto-created from groups.
- LDAP gains `ProjectMappings` even though it has no `RoleMappings` today. The pattern option is the natural fit for LDAP group naming.

### API keys

- A key is created **inside a project** by a user with `ApiKeyManagement.Create` in that project. `PrivilegeGuard` already ensures a caller can only grant permissions it holds, and that check now runs against the caller's permissions in that project.
- The key's JWT does not need a project claim. The project is read from the database row through `ApiKeyStore`, which caches it in Redis. Changing a key's project is not supported. Create a new key instead.
- Instance keys (`ProjectId = NULL`) are created only by super admins, from the instance settings page. They are for the CLI and Terraform managing projects and memberships. They carry instance permissions only.
- The API key list endpoint shows only the current project's keys. Instance keys appear only in instance settings.

### MCP

The MCP tools need no per-tool change for scoping. Every tool calls a MediatR handler, and the handler runs under the request's `IProjectContext`, so the EF filter applies. The work in this area is:

- **Configuration:** an MCP client connects with a project API key, which is the recommended setup and pins the connection to that project, or with a user token plus `X-Milvaion-Project`. The portal page gets an example of each.
- **Two new tools.** `list_projects` returns the projects the caller can see. `get_current_project` returns the project the connection is bound to. Without these, an assistant cannot tell which project its answers describe.
- **Tool output** for overview and stats tools includes the project slug, so answers are never ambiguous.
- **Instance-level tools** stay instance-level: `get_system_health`, `get_resource_usage`, `get_queue_stats`, `get_database_statistics` and `get_configuration`. They keep their current `SystemAdministration` permission checks. A project key does not hold those permissions, so they return `403` to project-scoped callers. A separate, project-filtered queue view is listed under [Open Decisions](#open-decisions).
- `McpPermissionGuard` already reads role claims, so it sees the project permissions added by the claims transformation without change.

## Workers

### Recommendation: workers belong to one project

Each worker registration belongs to exactly one project. The worker key becomes `(ProjectId, WorkerId)`, so two teams can both run a worker called `email-worker`.

Shared workers (one deployment serving many projects) are attractive for the built-in HTTP, SQL, Email and Command workers. They are also the easiest path to a cross-project privilege escalation. A SQL worker holds connection strings and a Command worker holds an allow-list of programs, so letting project B schedule jobs on project A's SQL worker hands B whatever A's connection string can reach. Shared workers are therefore **not part of the first version**. A team that wants the HTTP worker runs its own instance of the same image with its own project setting. The images are small, and this is how the built-in workers are deployed today anyway. If shared workers are added later, sharing is granted **by an instance admin on the server side**, never declared by the worker (see [Open Decisions](#open-decisions)).

### How a worker declares its project

Every SDK gains one optional setting:

| SDK | Setting |
|---|---|
| .NET | `Worker:Project` |
| Node | `project` |
| Go | `Project` |
| Python | `project` |
| Java | `project` |
| PHP | `project` |
| Rust, Ruby | `project` |
| All | environment variable `MILVAION_PROJECT` |

It is sent as a new optional top-level field, `Project` (the slug), in `WorkerDiscoveryRequest`, and in `ExternalJobRegistrationMessage` and `ExternalJobOccurrenceMessage`. The API already deserialises case-insensitively and ignores unknown fields, and a missing field reads as `null`:

- **An old worker** sends no field and is placed in **Default**. Its queue names, routing keys and Redis channel are unchanged. Nothing about it changes.
- **A new worker against an old API** sends a field the old API ignores, so it lands in the only project there is. This is harmless.
- **A new worker with no project set** behaves exactly like an old worker.

The protocol version in `WORKER-PROTOCOL.md` gets a minor bump, and the new field is documented there.

### How a worker proves its project

Today a worker proves nothing beyond broker access. Any worker can claim any `WorkerId`. With projects, a worker claiming `Project: payments` is a claim the server must check, or a team could register a worker into another team's project and receive its job payloads.

**Recommendation: a project worker token.**

- Project owners create **worker tokens** in project settings. A worker token is a project API key with a fixed, non-editable scope, `Worker.Register`, that grants no REST permissions.
- The worker sends it as `ProjectToken` in the registration message. The SDK setting is `Worker:ProjectToken`, and the environment variable is `MILVAION_PROJECT_TOKEN`.
- `WorkerAutoDiscoveryService` validates it with the same code as `ApiKeyAuthenticationHandler`: signature, revocation, expiry, version and project match. Then it records the registration under that project.
- A registration that names a non-Default project without a valid token is rejected and logged. Its jobs are not routed. The dashboard shows the rejection under the project's workers page as "rejected registrations", so the cause is visible.
- **Default project registrations need no token**, which keeps old workers working. An option, `MilvaionConfig:Projects:RequireWorkerTokenForDefault`, closes that door once all workers are updated.

**Status, log and heartbeat messages** stay on the shared queues without a token in each message. The API trusts them by checking that the occurrence named in a status or log message was dispatched to a worker in the same project as the occurrence. It already loads the occurrence row to apply a status change. The occurrence id is a GUIDv7 that only the consumer of that job's queue ever sees. A worker outside the project cannot learn ids it could forge updates for, because it cannot bind to the project's queues (see the broker section below).

### Queue names and routing keys

| | Default project | Other projects |
|---|---|---|
| Default routing pattern | `{workerid}.{job}.*` (unchanged) | `{slug}.{workerid}.{job}.*` |
| Queue | `scheduled_jobs_queue.{pattern}` (unchanged) | `scheduled_jobs_queue.{slug}.{workerid}.{job}.wildcard` |
| Routing key | from `ScheduledJob.RoutingPattern` (unchanged) | from `ScheduledJob.RoutingPattern`, which now starts with `{slug}.` |
| Exchange | `jobs.topic` | `jobs.topic` |
| Status, log, heartbeat, registration queues | shared | shared |

The SDK computes the prefixed pattern when a project is set, exactly as it computes the default pattern today (`WorkerServiceCollectionExtensions.cs`, `JobConsumer.cs` and their equivalents in each SDK). The server never invents a routing key on its own. It uses the pattern the worker registered, so it does not care which SDK version computed it. `SyncRoutingPatternsAsync` matches on `(ProjectId, WorkerId, JobNameInWorker)` instead of `(WorkerId, JobNameInWorker)`.

A user-supplied routing pattern override (`JobConsumers:*:RoutingPattern`) in a non-Default project must start with `{slug}.`. The API rejects the registration otherwise, so one project's override cannot capture another project's routing keys.

### Broker-level isolation

The routing-key prefix makes per-project broker permissions possible without being required:

- **Recommended for teams that do not trust each other's workers:** give each project its own RabbitMQ user, with permission regexes limited to its own queues (`^scheduled_jobs_queue\.payments\..*`) plus write access to the shared status, log, heartbeat and registration queues. Add RabbitMQ topic authorisation on `jobs.topic` limited to `^payments\..*`. The broker then enforces that a payments worker can neither bind to nor read another project's jobs. The portal docs give the exact `rabbitmqctl set_permissions` and `set_topic_permissions` commands. Milvaion does not manage broker users itself. That stays with the operator, as broker credentials do today.
- **Per-project virtual hosts** were considered and rejected for the default design. The API would need one connection and one set of consumers per vhost, and creating a project would require the RabbitMQ management API. They remain available to operators who run a separate deployment per tenant.

### Redis access by workers

Workers connect to Redis to receive cancellations. Today they use the same Redis credentials as the API, which can read every key, including the cached API keys. This is already a weakness, and projects make it matter more. The portal docs will recommend a Redis ACL user for workers that can only `SUBSCRIBE` to the cancellation channels (`&{prefix}*cancellation_channel`) and has no key access (`-@all +subscribe +psubscribe +ping`). This needs no code change.

## Scheduler, Redis and SignalR

### Scheduler

The scheduler stays **one global loop over one schedule**. Splitting the dispatcher per project would multiply Redis round trips and locks for no isolation gain, because the dispatcher is server code that all projects trust.

- `JobDispatcherService` keeps reading the single `scheduled_jobs` ZSET. The cached job hash (`job:{jobId}`) gains `projectId`, which the dispatcher copies onto the occurrence it creates.
- Worker capacity keys are keyed by the project-qualified worker id (see below).
- **Fairness:** a due batch is currently taken in score order. One project with 10,000 jobs due at the same second can delay another project's single job. After the batch is read, the dispatcher groups it by project and interleaves it round-robin before publishing. This is cheap, and it is in phase 2.
- **Quotas** (if enabled) are checked here. See [Limits and Quotas](#limits-and-quotas).
- **Emergency stop** stays global (super admin). A per-project pause is added as an archive-like project flag, `SchedulingPaused`, which the dispatcher checks from the job hash.
- The **misfire monitor, zombie detector and workflow engine** stay global, in `System` scope. They copy `ProjectId` from parent rows. Their alerts carry the project (see [Alerts](#alerts-reports-and-activity-logs)).
- **External jobs:** `ExternalJobTrackerService` matches on `(ProjectId, ExternalJobId)` and resolves the project from the message's `Project` field, validated against the registered worker's project. The Redis mapping `external:{externalJobId}` becomes `external:{projectId}:{externalJobId}`.

### Redis

The instance-wide `KeyPrefix` stays as the outer prefix. Keys split into three groups:

| Keys | Change |
|---|---|
| `scheduled_jobs`, `job:{jobId}`, `lock:{jobId}`, `running:{occurrenceId}`, `running_jobs`, `global_lock:{name}`, `dispatcher:emergency_stop`, `apikey:{id}`, `misfire-alert:*`, `apikey-alert:*` | **Unchanged.** Keyed by globally unique ids or truly global. |
| `workers:index`, `workers:{workerId}`, `workers:{workerId}:instances*`, `workers:{workerId}:job_counts`, `running_jobs_by_worker:{workerId}`, `heartbeat:{workerId}`, `external:{externalJobId}` | **Project-qualified.** The worker id component becomes `{projectId}:{workerId}`, e.g. `workers:3:email-worker`. The Default project uses `1:`. The registry has a 7-day TTL and is rebuilt from heartbeats, so the upgrade re-registers workers within one heartbeat interval instead of renaming keys. During that window the dashboard shows workers as "re-registering". |
| `stats:occurrences:*`, `stats:timeline` | **Per project plus an instance total.** `stats:p:{projectId}:occurrences:*` and `stats:p:{projectId}:timeline`, plus the existing global keys for the instance view. The status tracker increments both, in one pipeline. |
| new | `p:membership:{userId}` (membership cache), `p:quota:{projectId}:*` (quota counters), `p:project:{slug}` (slug-to-id cache) |

`RedisStatSyncJob` currently corrects counters from `pg_class.reltuples` estimates, which cannot be split by project. The per-project correction runs an exact `COUNT(*) ... GROUP BY "ProjectId", "Status"` on the `(ProjectId, Status)` index at a lower frequency (hourly by default). The global estimate keeps its current schedule.

The **cancellation channel** stays `{KeyPrefix}cancellation_channel` for the Default project. Workers in other projects subscribe to `{KeyPrefix}p:{slug}:cancellation_channel`. The SDK derives that name when a project is set, and the API publishes to the occurrence's project channel. Cancellation messages carry only ids and a reason, so the main gain is less noise, plus the ability to scope the worker Redis ACL above to `&{prefix}p:{slug}:*`.

The Maintenance worker's `RedisCleanupJob` scans `running_jobs_by_worker:*`. Its pattern changes with the key format.

### SignalR

- The client connects with `?project=<slug>`. `JobsHub.OnConnectedAsync` resolves the project through the same claims transformation and joins `p:{projectId}:occurrence_readers` instead of the global `occurrence_readers`.
- Super admins viewing "All projects" join `occurrence_readers:all`.
- `SubscribeToOccurrence(occurrenceId)` **loads the occurrence's project and checks the caller can read it** before joining `occurrence_{id}`. Today it only checks the permission, so any reader could subscribe to any occurrence id it guessed or saw in a link. This check is needed even before projects exist. It is cheap, so it goes into phase 1.
- `SignalRJobOccurrenceEventPublisher` sends `OccurrenceCreated` and `OccurrenceUpdated` to `p:{projectId}:occurrence_readers` and to `occurrence_readers:all`. The event payload already contains the occurrence, which now carries its `ProjectId`.
- Switching project in the dashboard reconnects the hub with the new query parameter. A connection never belongs to two projects.

## Alerts, Reports and Activity Logs

### Alerts

- **Channels** (Slack, Teams, Google Chat, email, Crewhall), meaning the webhooks and SMTP settings, stay in appsettings as the instance default. Each project can add **its own channel definitions** in project settings. A project's Slack webhook is a secret, so the setting stores a **secret reference**, using the secret-reference feature being built in parallel, not the raw URL.
- **Rules** (`NotificationRule`: alert type, enabled, channels) move from the single `AppSetting` row to `Project.Settings.Notifications`. The migration copies the current rules to the Default project. The instance-level rules remain for instance alerts (API key expiry for instance keys, system health).
- **Routing:** `AlertNotifier` resolves the occurrence's or job's project, then its rule, then the project's channels, then the instance defaults. Every alert message includes the project name.
- **Internal notifications** go only to users who are members of the alert's project **and** have the alert type in `AllowedNotifications`. Super admins get them only if they opt in to "all projects" in their profile.

### Reports

- `MetricReport.ProjectId` is set by the Reporter worker. Each report job runs its SQL with `GROUP BY "ProjectId"` and writes one report per project, plus one instance report with `ProjectId = NULL`.
- The Reporter worker is a system worker. It is the one documented case where a worker reads across projects, because it connects to the database directly and not through the job queues. It runs in the Default project and is operated by the instance admin.
- The reports page shows the current project's reports. Super admins viewing "All projects" see the instance reports.

### Activity logs

`ActivityLog.ProjectId` is set by `UserActivityLogInterceptor` from `IProjectContext` when it is `Resolved`, and left `NULL` for instance actions. Project owners see their project's log. Super admins see everything.

## Dashboard UX

- **Project switcher** at the top of the sidebar, above the menu. It shows the current project's name and a dropdown of the user's projects with a search box. Super admins and instance auditors also get **"All projects"**, a read-only aggregate view, at the top of the list.
- The current project lives in the URL as a prefix, `/p/{slug}/jobs`, so links can be shared and opened in another tab without confusion. It is also kept in `localStorage` as the last-used project. The API client (`services/api.js`) sends `X-Milvaion-Project` from the URL on every request.
- Users with exactly one project never see a switcher. The Default-only installation looks exactly as it does today.
- **Project settings** page (Project Owner):
  - General: name, description, archive, pause scheduling.
  - Members: add a user with a role. External members are shown as read-only with their source.
  - API keys: the existing API key page, moved here.
  - Worker tokens: create, revoke, last used, and the rejected registrations list.
  - Notifications: rules and project channels.
  - Quotas: read-only for project owners, editable by super admins.
- **Instance settings** (super admin) gains a **Projects** page: list, create, archive, delete (only when empty), and member counts. Users, roles, branding and system administration stay where they are.
- **"All projects" view:** the job, occurrence and failure lists gain a Project column and filter. Create and edit actions are disabled in this view, because a write must happen inside a project.
- The **Workers** page shows only the current project's workers, and its "how to connect" snippet includes `MILVAION_PROJECT` and `MILVAION_PROJECT_TOKEN`.
- The **job form** worker dropdown lists only the current project's workers. That falls out of the scoped worker query.

## Limits and Quotas

Quotas are optional. Each is off by default and set per project by a super admin. All of them are enforced on the server:

| Quota | Where it is checked |
|---|---|
| Max scheduled jobs | Create job handler. Counted from the database. |
| Max workflows | Create workflow handler. |
| Max concurrent running occurrences | Dispatcher, via `p:quota:{projectId}:running`, incremented on dispatch and decremented on a terminal status. Over the limit, due jobs are skipped this tick and retried, the same way the existing `ConcurrentExecutionPolicy.Skip` behaves per job. |
| Max dispatches per minute | Dispatcher, using a sliding window counter. |
| Max registered worker instances | Registration. Excess registrations are rejected with a visible reason. |
| Occurrence and log retention days | Maintenance worker cleanup jobs read per-project overrides from `Project.Settings`. Otherwise the instance default applies. |
| Max API keys | Create key handler. |

The dashboard shows usage against each quota on the project settings page. Phase 3 delivers these. Fairness in dispatching (phase 2) matters more than hard limits for most installations.

## Security Risks and Tests

### Risks

| Risk | Mitigation |
|---|---|
| A query path bypasses the EF filter (raw SQL, Dapper, a new `DbContext` instance created without the context) | Fail-closed `Unresolved` state. Raw SQL call sites registered and tested. The cross-project test suite below. |
| A background service writes a child row with the wrong project | Background services copy `ProjectId` from the parent row, and the save interceptor rejects writes where a child's `ProjectId` differs from its loaded parent. |
| A request carries a header naming another project | Membership check in the claims transformation. Project API keys reject a mismatching header. |
| Guessing ids across projects | The filter turns another project's id into `404`, never `403`, so existence is not confirmed. |
| Workflow definition references another project's job | Explicit validation through the filtered context. |
| A worker registers into another project | Worker tokens. Optional broker permissions per project. |
| A routing pattern override captures another project's keys | Prefix enforcement at registration. |
| A worker uses Redis to read API keys or schedules | Worker Redis ACL limited to subscribe (docs). |
| SignalR subscription to a foreign occurrence | Project check in `SubscribeToOccurrence`. |
| Alert sends project A's failure to project B's channel | Alerts resolve channels from the occurrence's project only, and the tests below include alert routing. |
| Cache keys without a project component serve one project's data to another | `[Cache]` attribute keys (`dashboard`, `db_stats` in `CacheConstant.cs`) gain the project id. A test enumerates every cache key builder. |
| Privilege escalation through role editing | Project roles are defined by instance admins only. Project owners assign them but cannot change them. |
| Instance API key misuse | Instance keys are created by super admins only and hold instance permissions only. |

### Tests

**Unit tests** (`tests/Milvaion.UnitTests`):

- `ProjectContext` state machine. Unresolved means the filter value is `-1`, and `System` requires an explicit scope.
- Claims transformation: member, non-member, super admin, auditor, project API key with matching and mismatching header, instance key, archived project.
- The save interceptor: stamps on add, rejects a project change, rejects a mismatch.
- OIDC and LDAP project mapping: explicit map, pattern, `*`, an unknown project ignored, external memberships replaced and local ones kept.
- Routing-pattern prefix enforcement and the pattern computation in the .NET SDK.
- A reflection test: every entity that has a `ProjectId` property implements `IProjectScoped`, and every `IProjectScoped` entity has the named filter.
- A reflection test: every method marked as raw SQL is in the reviewed list.

**Integration tests** (`tests/Milvaion.IntegrationTests`, Testcontainers). One fixture seeds **two projects, A and B**, each with jobs, a workflow, occurrences with logs, failures, a report, an API key, a worker registration and an activity log entry. It also creates a user who is a member of A only. Following the repository's rule, no test builds a second host with `WithWebHostBuilder`. Everything runs in the shared host, and switching project is just a header.

- **Generated leakage test:** enumerate every `GET` endpoint from the API's endpoint data source and every MCP tool from the MCP server's tool list. Call each as the A-only user, and as A's API key. Assert that no id seeded for B appears anywhere in the response body. A new endpoint or tool is covered automatically. One that cannot be called generically must be added to an explicit exclusion list with a reason, which a reviewer will see.
- **Direct access:** `GET`, `PUT`, `DELETE`, trigger and cancel on B's resources by id as an A member return `404`, and B's rows are unchanged afterwards.
- **Bulk operations:** `delete_occurrences` and `resolve_failures` with a mix of A and B ids affect only A's.
- **Header tampering:** an A key with `X-Milvaion-Project: b` gets `403`. A user who is not a member of B gets `403` with that header.
- **Workflows:** creating a workflow in A that references a B job is rejected.
- **Workers:** a registration naming B without a token is rejected. With A's token naming B it is rejected. With B's token it is accepted. A status update for a B occurrence sent by an A worker is ignored.
- **SignalR:** an A member receives no events for B occurrences, and `SubscribeToOccurrence(bOccurrenceId)` fails.
- **Alerts:** a failure in B routes only to B's channel, using a test transport that records sends.
- **Dispatch:** with fairness enabled, a flood of due jobs in A does not starve a single due job in B beyond one batch.
- **Migration test:** apply the migrations to a database seeded with the pre-projects schema and data. Assert that every row lands in Default and every user's effective permissions are unchanged.

## Rollout Plan

Sizes are rough, for one developer who knows the codebase. S is up to 2 days, M is 3 to 5 days, L is 1 to 2 weeks.

**Phase 0: preparation, no behaviour change (S)**

- Add the user id claim to the JWT.
- Add the occurrence ownership check to `SubscribeToOccurrence`.
- Document the worker Redis ACL.
- These are worth doing even if projects are never built.

**Phase 1: data model and enforcement, Default only (L)**

- `Projects`, `ProjectMemberships`, role scope, permission scope attribute.
- `ProjectId` columns, migration, indexes.
- `IProjectContext`, EF named filter, save interceptor, raw SQL audit.
- Claims transformation, header resolution, Default fallback.
- Project-qualified worker and stats Redis keys.
- Cross-project test suite, run with two projects created directly in the test fixture.
- Ships with project creation **disabled** behind `MilvaionConfig:Projects:Enabled=false`. Production installations run the new code on the Default project only. This is the riskiest phase, and it is shipped with no visible change.

**Phase 2: projects visible (L)**

- Project API: CRUD, members, worker tokens.
- Dashboard switcher, project settings, instance projects page.
- SDK `project` and `projectToken` options in all SDKs, with routing prefix and cancellation channel. This is about S per SDK, so M to L across all of them, and parallelisable.
- Per-project alerts and notification rules.
- SignalR project groups.
- MCP `list_projects` and `get_current_project`.
- OIDC and LDAP project mapping.
- Dispatcher fairness.
- Portal docs: concepts, setup, broker permissions, migration notes.
- Flip `Projects:Enabled` to `true` by default.

**Phase 3: reports, quotas and polish (M)**

- Per-project reports in the Reporter worker.
- Quotas and usage display.
- Per-project retention in the Maintenance worker.
- "All projects" view.
- CLI and Terraform provider support: a `project` setting and `milvaion_project` and `milvaion_project_member` resources. This needs the CLI and Terraform work happening in parallel to land first.

**Phase 4: optional**

- Shared workers granted by admins.
- Moving resources between projects.
- Per-project vhost support for operators who want it.

Total: roughly 6 to 8 weeks of work, with phase 1 as the single biggest risk.

**Ordering with parallel work:** this feature's migration must come **after** the scheduling session's migration (time zone, catch-up, priority, calendars), because both touch `ScheduledJobs`. Calendars, if they are stored as their own entity, will need a `ProjectId` too. Secret references should exist before phase 2, so project alert channels can use them. The Rust and Ruby SDKs should get the `project` option in the same pass as the others.

## Alternatives Considered

### A separate deployment per tenant

Each team gets its own API, database (or schema), Redis prefix and broker vhost. The Helm chart being built in parallel makes this a one-command operation.

- **For:**
  - Total isolation, with no code change.
  - Independent upgrades and blast radius.
  - Noisy neighbours are impossible.
  - Already supported today: `KeyPrefix` was made all-covering for exactly this reason, as `ARCHITECTURE.md` explains.
- **Against:**
  - N copies of the API, dispatcher and dashboard to run and upgrade.
  - N sets of users and role mappings.
  - No cross-team view for operations.
  - Built-in workers deployed N times anyway.
  - Each deployment's background services poll the database and Redis on their own, so cost grows linearly with teams.
- **Verdict:** keep it as the documented answer for **hostile or regulated tenants** (different customers, different compliance zones). Build projects for **teams inside one organisation**, which is the request. The two compose: a large organisation can run several deployments, each with several projects.

### A schema per project in one database

- **For:** strong data isolation, and simple per-project backup and drop.
- **Against:**
  - EF Core model caching per schema is awkward.
  - Migrations multiply by the number of projects.
  - Every background service would have to loop over schemas.
  - Cross-project admin views need unions.
- **Verdict:** rejected. The cost lands on the scheduler, which is the part that must stay simple and fast.

### PostgreSQL row-level security instead of an EF filter

- **For:** enforcement in the database also covers raw SQL and Dapper.
- **Against:**
  - Every connection must `SET app.project_id` per request, which fights connection pooling and the bulk extensions.
  - Background services need a bypass role.
  - Failures surface as empty results that are hard to debug.
- **Verdict:** not in the first version. It is a good **second layer** to add later, on the occurrence and log tables only, once the EF approach is stable. It is listed as an open decision.

### Project as a route segment

Rejected for the reasons in [How a request picks its project](#how-a-request-picks-its-project): it breaks every client and gains nothing over a header with a Default fallback.

### Project as a JWT claim

Rejected. It forces token re-issue on every project switch and makes membership changes lag until refresh.

### Tags as a lightweight separation

Jobs already have tags, and a "team:payments" tag with permission checks on tags would be cheaper. Tags do not cover workers, keys, alerts or queues, and tag-based permission checks would be a second, weaker access model. Rejected.

## Open Decisions

These need an answer from the maintainer before implementation starts. Each has a recommendation.

1. **Do global roles with project permissions apply to all projects?** The recommendation is **no**. The migration splits them into instance roles and Default-project memberships. The alternative is simpler for small teams but makes "who can see project X" impossible to answer from the membership list.
2. **Shared workers.** Do we need workers that serve several projects (the built-in HTTP, Email and SQL workers) in the first version? The recommendation is **no**: one worker per project, with admin-granted sharing in phase 4.
3. **Worker token for the Default project.** Should registration into Default stay token-free by default for backward compatibility? The recommendation is **yes**, with `RequireWorkerTokenForDefault` for operators who want to close it.
4. **Can project owners define their own roles?** The recommendation is **no** in the first version. Instance admins define project roles, and project owners assign them.
5. **No project header.** Should the API fall back to Default when no project is sent? The recommendation is **yes**, with `RequireExplicitProject` to turn it off later.
6. **Moving jobs or workflows between projects.** Is this needed? The recommendation is to leave it out of the first version. Moving means rewriting occurrences, logs and failures, so export and import is the safer path.
7. **Row-level security as a second layer** on occurrences and logs (phase 4)? Yes or no.
8. **Per-project queue visibility.** Should project users see the depth of their own job queues? This needs a per-queue RabbitMQ management API call filtered by the project prefix. The recommendation is yes, in phase 3.
9. **The Reporter worker reads across projects.** Is that acceptable, given that it is a system worker run by the instance admin, or should reports move into the API? The recommendation is to accept it.
10. **Project ids.** `int`, as recommended for column size on the large tables, or `Guid` to match jobs and occurrences?
11. **Instance Auditor role.** Do we seed a read-all role for operations and on-call, or keep cross-project reading to super admins only?
