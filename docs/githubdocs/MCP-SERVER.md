# MCP Server

This document covers the internals of Milvaion's Model Context Protocol server and the api key authentication it depends on. For user-facing setup instructions see the [MCP Server](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/mcp-server) and [Api Keys](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/api-keys) portal docs.

## Table of Contents

- [Overview](#overview)
- [Api Key Authentication](#api-key-authentication)
- [MCP Server Wiring](#mcp-server-wiring)
- [Authorization Inside Tools](#authorization-inside-tools)
- [Adding a Tool](#adding-a-tool)
- [Design Decisions](#design-decisions)
- [Testing](#testing)

---

## Overview

Milvaion is an MCP **server**: it exposes its own data as tools that an MCP client (Claude Code, Cursor, Copilot) can call. Milvaion never talks to a language model and holds no model provider credentials.

```text
MCP client                Milvaion.Api
    │
    │  POST /mcp  (JSON-RPC, X-ApiKey header)
    ▼
ApiKeyAuthenticationHandler ──▶ ApiKeyStore (Redis cache ─▶ Postgres)
    │  ClaimsPrincipal with permissions as role claims
    ▼
MapMcp endpoint  ──▶  tool method  ──▶  McpPermissionGuard.Require(...)
                                            │
                                            ▼
                                        IMediator ──▶ existing CQRS query
```

### Source Layout

| Path | Contains |
|------|----------|
| `src/Milvaion.Api/Mcp/MilvaionJobTools.cs` | Job and occurrence tools, including CRUD |
| `src/Milvaion.Api/Mcp/MilvaionOpsTools.cs` | Worker, workflow and dashboard tools |
| `src/Milvaion.Api/Mcp/MilvaionDiagnosticsTools.cs` | Dead letter failures and the activity log |
| `src/Milvaion.Api/Mcp/MilvaionInsightTools.cs` | Metric reports, infrastructure health, configuration |
| `src/Milvaion.Api/Mcp/MilvaionPrompts.cs` | Prompt templates for diagnosis workflows |
| `src/Milvaion.Api/Mcp/McpPermissionGuard.cs` | Per-tool permission enforcement |
| `src/Milvaion.Api/AppStartup/McpExtensions.cs` | Registration and endpoint mapping |
| `src/Milvaion.Api/Utils/ApiKeyAuthenticationHandler.cs` | Authentication scheme |
| `src/Milvaion.Api/Utils/KeyHelper.cs` | Key generation and signature validation |
| `src/Milvaion.Api/Services/ApiKeyStore.cs` | Cached key lookup and cache invalidation |
| `src/Milvaion.Api/Services/ApiKeyGenerator.cs` | `IApiKeyGenerator` implementation |
| `src/Milvaion.Application/Features/ApiKeys/` | Api key CQRS features |

Dependency: `ModelContextProtocol.AspNetCore`.

---

## Api Key Authentication

### Key Format

A key is a JWT signed with `MilvaionConfig.ApiKey.Secret` (HMAC-SHA256), carrying:

| Claim | Meaning |
|-------|---------|
| `jti` | Id of the `MilvaionApiKey` record |
| `kv` | Version of the signing secret |
| `exp` | Expiry, omitted for keys that never expire |

The key itself is **never persisted**. Only `MaskedKey` — the trailing characters — is stored, purely so a user can match a listed key against the one in their configuration.

### Why a Record Lookup

Signature validation alone would be enough to prove a key is authentic, but a self-contained token cannot be revoked. Authentication therefore always resolves `jti` to a `MilvaionApiKey` row and checks `RevokedAt`, `ExpiresAt` and `KeyVersion` before accepting the request.

That lookup is cached in **Redis**, not in process memory. With several API replicas an in-process cache would leave a revoked key working on every replica that did not happen to handle the revoke request. `RevokeApiKeyCommandHandler` and `DeleteApiKeyCommandHandler` call `IApiKeyCacheInvalidator.InvalidateAsync` so revocation takes effect immediately across the cluster.

If Redis is unavailable, `ApiKeyStore` falls back to the database rather than failing authentication — a cache outage should not lock every integration out.

### Permissions as Role Claims

`AuthAttribute` derives from `AuthorizeAttribute` and matches on `Roles`. `ApiKeyAuthenticationHandler` therefore emits each granted permission as a `ClaimTypes.Role` claim, exactly as the login token does:

```csharp
claims.AddRange(apiKey.Permissions.Select(p => new Claim(ClaimTypes.Role, p)));
claims.Add(new Claim(GlobalConstant.UserTypeClaimName, nameof(UserType.Manager)));
```

The consequence is that **every existing `[Auth(PermissionCatalog...)]` attribute already works for api key callers** with no change. Had this been implemented as an action filter instead of an authentication scheme, authorization would have had to be reimplemented separately for machine callers.

The default authorization policy names both schemes:

```csharp
options.DefaultPolicy = new AuthorizationPolicyBuilder(
        JwtBearerDefaults.AuthenticationScheme,
        ApiKeyAuthenticationDefaults.AuthenticationScheme)
    .RequireAuthenticatedUser()
    .Build();
```

### LastUsedAt Throttling

`LastUsedAt` would otherwise mean a database write on every request. `ApiKeyStore.ShouldWriteLastUsedAsync` uses a Redis `SET ... NX EX` as both throttle and lock: whoever sets the key owns that interval. Default interval is five minutes, configurable through `ApiKeyAuthenticationOptions.LastUsedWriteInterval`.

Failures here are swallowed and logged. Bookkeeping must never fail an otherwise valid request.

### `ApiAuthAttribute`

`[ApiAuth]` restricts an endpoint to api key callers only, for endpoints that should not be reachable from a browser session. `[Auth]` accepts both. All parsing and validation lives in the authentication handler; the attribute only selects the scheme and applies the permission requirement.

---

## MCP Server Wiring

Registration is in `McpExtensions.AddMilvaionMcp`:

```csharp
services.AddMcpServer(options => { /* ServerInfo, ServerInstructions */ })
        .WithHttpTransport(options => options.Stateless = true)
        .WithToolsFromAssembly();
```

`WithToolsFromAssembly` discovers every `[McpServerToolType]` class and registers each `[McpServerTool]` method. Tool classes are resolved from DI per request, so constructor injection of scoped services works.

Mapping is in `McpExtensions.MapMilvaionMcp`:

```csharp
app.MapMcp("/mcp").RequireAuthorization();
```

> **Ordering matters.** `MapMilvaionMcp()` must be called before `MapFallbackToFile("index.html")` in `Program.cs`, or the SPA fallback swallows `/mcp`.

### ServerInstructions

`ServerInstructions` is sent to the client at initialize and shapes how the model uses the server. Milvaion's explains the job/occurrence distinction, steers diagnosis towards `list_failures` rather than `list_occurrences`, and tells the model to confirm before triggering anything. In practice this affects tool selection accuracy more than any individual tool description.

---

## Authorization Inside Tools

MCP tools are not MVC actions, so `[Auth]` never runs for them. Authentication is still enforced at the endpoint, but the per-permission check has to happen in the tool body:

```csharp
_guard.Require(PermissionCatalog.ScheduledJobManagement.List);
```

`McpPermissionGuard` reads `HttpContext.User`, honours `App.SuperAdmin`, and throws `McpException` otherwise. The message names the missing permission on purpose — an agent told exactly what it lacks stops retrying and can report something actionable.

`Has(permission)` is available for trimming optional detail out of a response instead of failing the whole call.

---

## Adding a Tool

1. Add a method to an existing `[McpServerToolType]` class, or create a new one — `WithToolsFromAssembly` finds it either way.
2. Call `_guard.Require(...)` **first**, before any work.
3. Delegate to the existing MediatR query or command. Do not reimplement data access; a change to filtering or projection should show up identically in the dashboard, the REST API and MCP.
4. Write a `[Description]` aimed at a model, not a developer: say when to reach for this tool rather than another one.
5. Add XML docs to match the rest of the codebase.

```csharp
/// <summary>
/// Gets scheduled jobs.
/// </summary>
/// <param name="searchTerm">Free text search over job names and types.</param>
/// <param name="cancellationToken"></param>
/// <returns>Paged job list with the total count.</returns>
[McpServerTool(Name = "list_jobs")]
[Description("Lists scheduled jobs in Milvaion. Use this to find a job's id before calling other tools.")]
public async Task<object> ListJobsAsync(
    [Description("Optional free text search over job names and types.")] string searchTerm = null,
    CancellationToken cancellationToken = default)
{
    _guard.Require(PermissionCatalog.ScheduledJobManagement.List);
    ...
}
```

### Conventions

- **Tool names** are `snake_case` and verb-first: `list_jobs`, `get_occurrence`, `trigger_job`.
- **Annotations are mandatory.** Set `ReadOnly = true` on anything that only reads, `Destructive = true` on anything irreversible, `Idempotent = true` on state setters. Clients use these to decide what to auto-approve; an unannotated destructive tool looks identical to a list call.
- **Filter, do not paginate.** Every list tool should expose the filters a person would reach for - id, status, owner, date bounds - built through `BuildDateRangeCriterias`, `AddEqualityCriteria` and `ToFilterRequest` in `MilvaionJobTools`. Making the model page through thousands of rows to find ten is slow and expensive.
- **Sort explicitly.** The handlers do not all default their sort order, so a query without `Sorting` returns rows in whatever order the plan produces. Match whatever the dashboard sends for the same list, or the model and the user will be looking at different data.
- **Bound the response.** Anything that can grow without limit - logs especially - needs a cap and a note saying it was truncated. Blowing the context window is a silent failure: the answer just gets vague and expensive.
- **Page sizes** are clamped to `_maxPageSize` (100). A model asking for 10,000 rows should get 100, not an error.
- **Not-found** throws `McpException` with a message naming the id, rather than returning null.
- **Write tools** require their own permission and never bypass domain safeguards — `trigger_job` always dispatches with `Force = false`.
- **Attribution**: anything with a side effect stamps the calling key into the reason, so history distinguishes machine from human.

---

## Design Decisions

### Why stateless HTTP transport

Milvaion is routinely deployed with several API replicas behind a load balancer. Stateful mode would require sticky sessions. Stateless rules out server-to-client requests (sampling, elicitation, roots), none of which these tools need.

### Why `update_job` takes plain arguments

`UpdateScheduledJobCommand` uses `UpdateProperty<T>` to separate "not supplied" from "set to null". Exposing that shape to a model would mean asking it to emit `{"value": "...", "isUpdated": true}` per field, which is both awkward to describe and easy to get wrong.

The tool takes nullable plain arguments instead and builds the wrappers in C#: null means unchanged. The cost is that a field cannot be cleared through MCP. That asymmetry is deliberate — a model accidentally blanking a cron expression is a worse outcome than not being able to blank one on purpose.

### Why `set_job_active` exists separately

Pausing a job is the most common intervention during an incident and it should not require a partial update payload. A dedicated tool also gives the description room to steer the model towards pausing rather than deleting, which is what users almost always mean by "stop this job".

### Why workflow authoring is absent

There is no `create_workflow` or `update_workflow`. A workflow is a directed graph with conditions, merge nodes and data mappings between steps; expressing one as tool arguments would be a large nested payload with plenty of ways to produce a graph that is syntactically valid and semantically wrong. Reading, triggering, cancelling and deleting workflows are all available — authoring stays in the visual builder.

### Why the write tools are shaped around intent

Rather than mirroring the REST surface one-to-one, the write tools are named after what a person wants: `set_job_active` rather than a generic update, `resolve_failures` rather than an update with six optional fields. Narrow tools are easier for a model to select correctly and give far less room for it to do something adjacent to what was asked.

### Why tools delegate to MediatR

Every tool sends an existing query. This keeps filtering, projection, permission semantics and localisation identical across all three entry points, and means new tools are cheap — most are a permission check plus a `Send`.

---

## Testing

`/mcp` speaks JSON-RPC over HTTP, so it can be exercised without an MCP client.

```bash
# Expect 401
curl -i -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'

# Tool list
curl -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "X-ApiKey: $MILVAION_API_KEY" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'

# Tool call
curl -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "X-ApiKey: $MILVAION_API_KEY" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_overview","arguments":{}}}'
```

Worth covering explicitly:

- A key **without** `ScheduledJobManagement.Trigger` calling `trigger_job` must fail. This is the guarantee the read-only key story rests on.
- A **revoked** key must stop working immediately, not after the cache expires.
- A key issued under an older `ApiKey.Version` must be rejected with the retired-secret message.

---

## Further Reading

- [MCP C# SDK](https://csharp.sdk.modelcontextprotocol.io/)
- [Model Context Protocol specification](https://modelcontextprotocol.io/specification/)
- [Architecture](ARCHITECTURE.md)
- [Development](DEVELOPMENT.md)
