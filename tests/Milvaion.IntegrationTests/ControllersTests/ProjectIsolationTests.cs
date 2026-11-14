using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Application.Utils.Projects;
using Milvaion.Domain;
using Milvaion.Domain.Enums;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Core.Helpers;
using Milvasoft.Identity.Abstract;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

/// <summary>
/// Cross-project leakage tests (docs/githubdocs/PROJECTS-DESIGN.md, "Security Risks and Tests").
/// </summary>
/// <remarks>
/// Two projects hold the same kinds of data, each tagged with a marker string that appears nowhere else: the Default
/// project (<see cref="_alphaMarker"/>) and a second project, beta (<see cref="_betaMarker"/>). Every read endpoint
/// and every read-only MCP tool is called as a caller of one project, and the response must never contain the other
/// project's marker. Each test also checks that the caller does see its own data, so an empty response cannot pass
/// for isolation.
/// <list type="bullet">
/// <item><c>alice</c> has the read permissions through a global role: they apply in the Default project only.</item>
/// <item><c>bob</c> has no global permissions and is a member of beta through a project role.</item>
/// </list>
/// </remarks>
[Collection(nameof(ProjectsTestCollection))]
[Trait("Controller Integration Tests", "Cross-project isolation.")]
public class ProjectIsolationTests(ProjectsWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0";
    private const string _alphaMarker = "ALPHAMARK";
    private const string _betaMarker = "BETAMARK";
    private const string _betaSlug = "beta";

    private static readonly string[] _readPermissions =
    [
        PermissionCatalog.ScheduledJobManagement.List,
        PermissionCatalog.ScheduledJobManagement.Detail,
        PermissionCatalog.ScheduledJobManagement.Trigger,
        PermissionCatalog.ScheduledJobManagement.Delete,
        PermissionCatalog.WorkflowManagement.List,
        PermissionCatalog.WorkflowManagement.Detail,
        PermissionCatalog.WorkflowManagement.Create,
        PermissionCatalog.FailedOccurrenceManagement.List,
        PermissionCatalog.FailedOccurrenceManagement.Detail,
        PermissionCatalog.ExclusionCalendarManagement.List,
        PermissionCatalog.ExclusionCalendarManagement.Detail,
        PermissionCatalog.ExclusionCalendarManagement.Create,
        PermissionCatalog.ApiKeyManagement.List,
        PermissionCatalog.ApiKeyManagement.Detail,
        PermissionCatalog.ApiKeyManagement.Create,
        PermissionCatalog.ActivityLogManagement.List,
        PermissionCatalog.WorkerManagement.List,
    ];

    private sealed record ProjectData(int ProjectId, Guid JobId, Guid OccurrenceId, Guid FailedOccurrenceId, Guid WorkflowId, Guid RunId, Guid CalendarId, Guid ReportId, int ApiKeyId)
    {
        public IEnumerable<string> Ids => [JobId.ToString(), OccurrenceId.ToString(), FailedOccurrenceId.ToString(), WorkflowId.ToString(), RunId.ToString(), CalendarId.ToString(), ReportId.ToString()];
    }

    private ProjectData _alpha;
    private ProjectData _beta;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var db = GetDbContext();

        var beta = new Project { Slug = _betaSlug, Name = "Beta", CreationDate = DateTime.UtcNow, CreatorUserName = GlobalConstant.SystemUsername };
        db.Projects.Add(beta);
        await db.SaveChangesAsync();

        _alpha = await SeedProjectDataAsync(db, ProjectDefaults.DefaultProjectId, _alphaMarker);
        _beta = await SeedProjectDataAsync(db, beta.Id, _betaMarker);

        await SeedCallersAsync(db, beta.Id);
    }

    #region Generated leakage checks

    /// <summary>
    /// Every read endpoint of project data. Detail endpoints are called with the ids of both projects.
    /// </summary>
    private static IEnumerable<(string Method, string Path)> ReadRequests(ProjectData own, ProjectData other)
    {
        yield return ("PATCH", "jobs");
        yield return ("PATCH", "jobs/occurrences/list");
        yield return ("PATCH", "jobs/occurrences");
        yield return ("GET", "jobs/tags");
        yield return ("GET", "jobs/upcoming");
        yield return ("GET", "jobs/logs");
        yield return ("GET", "jobs/logs/summary");
        yield return ("PATCH", "jobs/occurrences/failed");
        yield return ("PATCH", "workflows");
        yield return ("PATCH", "workflows/runs");
        yield return ("PATCH", "exclusioncalendars");
        yield return ("PATCH", "metricreports");
        yield return ("GET", "metricreports/latest?MetricType=FailureRate");
        yield return ("PATCH", "apikeys");
        yield return ("PATCH", "activitylogs");
        yield return ("GET", "dashboard");
        yield return ("PATCH", "workers");

        foreach (var data in new[] { own, other })
        {
            yield return ("GET", $"jobs/job?JobId={data.JobId}");
            yield return ("GET", $"jobs/occurrences/occurrence?OccurrenceId={data.OccurrenceId}");
            yield return ("GET", $"jobs/logs?OccurrenceId={data.OccurrenceId}");
            yield return ("GET", $"jobs/logs?JobId={data.JobId}");
            yield return ("GET", $"jobs/logs/summary?JobId={data.JobId}");
            yield return ("GET", $"jobs/occurrences/occurrence/failed?FailedOccurrenceId={data.FailedOccurrenceId}");
            yield return ("GET", $"workflows/workflow?WorkflowId={data.WorkflowId}");
            yield return ("GET", $"workflows/runs/run?RunId={data.RunId}");
            yield return ("GET", $"exclusioncalendars/calendar?CalendarId={data.CalendarId}");
            yield return ("GET", $"metricreports?Id={data.ReportId}");
            yield return ("GET", $"apikeys/apiKey?ApiKeyId={data.ApiKeyId}");
        }
    }

    [Fact]
    public async Task MemberOfBeta_SeesNothingOfTheDefaultProject_OnAnyReadEndpoint()
    {
        var bob = await LoginInProjectAsync("bob", _betaSlug);

        var combined = await AssertNoForeignMarkerAsync(bob, ReadRequests(_beta, _alpha), _alphaMarker);

        combined.Should().Contain(_betaMarker, "bob must see beta's data, or the check above proves nothing");
    }

    [Fact]
    public async Task DefaultProjectUser_SeesNothingOfBeta_OnAnyReadEndpoint()
    {
        var alice = await _factory.CreateClient().LoginAsync("alice");

        var combined = await AssertNoForeignMarkerAsync(alice, ReadRequests(_alpha, _beta), _betaMarker);

        combined.Should().Contain(_alphaMarker);
    }

    [Fact]
    public async Task BetaApiKey_SeesNothingOfTheDefaultProject_OnAnyReadEndpoint()
    {
        var key = await CreateApiKeyInBetaAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(MessageConstant.ApiKey, key);

        var combined = await AssertNoForeignMarkerAsync(client, ReadRequests(_beta, _alpha), _alphaMarker);

        combined.Should().Contain(_betaMarker);
    }

    [Fact]
    public async Task McpReadTools_ForBeta_ReturnNothingOfTheDefaultProject()
    {
        var key = await CreateApiKeyInBetaAsync();

        var toolsResponse = await PostMcpAsync(key, """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
        var tools = ParseTools(toolsResponse);

        tools.Should().NotBeEmpty();

        var readTools = tools.Where(t => Regex.IsMatch(t.Name, "^(get|list|search|summarize)_")).ToList();
        var combined = new StringBuilder();
        var id = 2;

        foreach (var (name, required) in readTools)
        {
            // Required arguments are filled with ids of the Default project: a beta caller must not reach them.
            foreach (var arguments in ArgumentSets(required, _alpha))
            {
                var call = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id++, method = "tools/call", @params = new { name, arguments } });
                var body = await PostMcpAsync(key, call);

                body.Should().NotContain(_alphaMarker, $"MCP tool {name} with {JsonSerializer.Serialize(arguments)} must not return the Default project's data");
                combined.Append(body);
            }

            // And once with beta's own ids, so the run proves the tools do return data.
            foreach (var arguments in ArgumentSets(required, _beta))
                combined.Append(await PostMcpAsync(key, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id++, method = "tools/call", @params = new { name, arguments } })));
        }

        combined.ToString().Should().Contain(_betaMarker);
    }

    #endregion

    #region Direct access, writes and tampering

    [Fact]
    public async Task MemberOfBeta_CannotChangeOrDeleteTheDefaultProjectsData()
    {
        var bob = await LoginInProjectAsync("bob", _betaSlug);

        var trigger = await bob.PostAsJsonAsync($"{_baseUrl}/jobs/job/trigger", new { JobId = _alpha.JobId, Reason = "cross-project" });
        var delete = await bob.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/jobs/job") { Content = JsonContent.Create(new { JobId = _alpha.JobId }) });
        var deleteOccurrences = await bob.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/jobs/occurrences/occurrence")
        {
            Content = JsonContent.Create(new { OccurrenceIdList = new[] { _alpha.OccurrenceId, _beta.OccurrenceId } })
        });

        (await IsSuccessAsync(trigger)).Should().BeFalse();
        (await IsSuccessAsync(delete)).Should().BeFalse();
        deleteOccurrences.StatusCode.Should().Be(HttpStatusCode.OK);

        var db = GetDbContext();

        (await db.ScheduledJobs.AnyAsync(j => j.Id == _alpha.JobId)).Should().BeTrue("bob cannot delete another project's job");
        (await db.JobOccurrences.CountAsync(o => o.JobId == _alpha.JobId)).Should().Be(1, "bob cannot trigger another project's job");
        (await db.JobOccurrences.AnyAsync(o => o.Id == _alpha.OccurrenceId)).Should().BeTrue("a bulk delete only touches the caller's project");
        (await db.JobOccurrences.AnyAsync(o => o.Id == _beta.OccurrenceId)).Should().BeFalse("bob's own occurrence is deleted");
    }

    [Fact]
    public async Task Writes_InBeta_LandInBeta()
    {
        var bob = await LoginInProjectAsync("bob", _betaSlug);

        var calendar = await bob.PostAsJsonAsync($"{_baseUrl}/exclusioncalendars/calendar", new { Name = "Weekends", Weekdays = new[] { 0, 6 } });
        var trigger = await bob.PostAsJsonAsync($"{_baseUrl}/jobs/job/trigger", new { JobId = _beta.JobId, Reason = "own job" });

        (await IsSuccessAsync(calendar)).Should().BeTrue(await calendar.Content.ReadAsStringAsync());
        (await IsSuccessAsync(trigger)).Should().BeTrue(await trigger.Content.ReadAsStringAsync());

        var db = GetDbContext();

        (await db.ExclusionCalendars.SingleAsync(c => c.Name == "Weekends")).ProjectId.Should().Be(_beta.ProjectId);

        var triggered = await db.JobOccurrences.Include(o => o.Logs).Where(o => o.JobId == _beta.JobId && o.Id != _beta.OccurrenceId).SingleAsync();
        triggered.ProjectId.Should().Be(_beta.ProjectId);
        triggered.Logs.Should().NotBeEmpty().And.OnlyContain(l => l.ProjectId == _beta.ProjectId);

        (await db.ActivityLogs.Where(a => a.ProjectId == _beta.ProjectId).CountAsync()).Should().BeGreaterThan(1, "activity is recorded in the project it happened in");
    }

    [Fact]
    public async Task CalendarNames_AreUniquePerProject_NotPerInstance()
    {
        var bob = await LoginInProjectAsync("bob", _betaSlug);

        // The Default project already has a calendar with this name.
        var response = await bob.PostAsJsonAsync($"{_baseUrl}/exclusioncalendars/calendar", new { Name = $"{_alphaMarker}-calendar", Weekdays = new[] { 0 } });

        (await IsSuccessAsync(response)).Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Workflow_InBeta_CannotReferenceAJobOfTheDefaultProject()
    {
        var bob = await LoginInProjectAsync("bob", _betaSlug);

        var response = await bob.PostAsJsonAsync($"{_baseUrl}/workflows/workflow", new
        {
            Name = "cross-project",
            Steps = new[] { new { TempId = "s1", StepName = "s1", JobId = _alpha.JobId, NodeType = 0, Order = 0 } }
        });

        (await IsSuccessAsync(response)).Should().BeFalse();
        (await GetDbContext().Workflows.AnyAsync(w => w.Name == "cross-project")).Should().BeFalse();
    }

    [Fact]
    public async Task ApiKeyCreated_InBeta_BelongsToBeta()
    {
        await CreateApiKeyInBetaAsync();

        (await GetDbContext().ApiKeys.SingleAsync(k => k.Name == "beta-key")).ProjectId.Should().Be(_beta.ProjectId);
    }

    [Fact]
    public async Task BetaApiKey_PointedAtAnotherProject_IsRejected()
    {
        var key = await CreateApiKeyInBetaAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(MessageConstant.ApiKey, key);
        client.DefaultRequestHeaders.Add(ProjectResolution.HeaderName, ProjectDefaults.DefaultProjectSlug);

        var response = await SendAsync(client, "PATCH", "jobs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(_alphaMarker).And.NotContain(_betaMarker);
    }

    [Fact]
    public async Task NonMember_NamingBeta_IsRejected()
    {
        var alice = await LoginInProjectAsync("alice", _betaSlug);

        var response = await SendAsync(alice, "PATCH", "jobs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(_betaMarker);
    }

    [Fact]
    public async Task UnknownProject_IsNotFound()
    {
        var alice = await LoginInProjectAsync("alice", "no-such-project");

        var response = await SendAsync(alice, "PATCH", "jobs");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MemberOfBeta_HasNoProjectPermissionsInTheDefaultProject()
    {
        // bob's role is a project role: it grants nothing outside beta, and his global roles grant nothing at all.
        var bob = await _factory.CreateClient().LoginAsync("bob");

        var response = await SendAsync(bob, "PATCH", "jobs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SuperAdmin_EntersBeta_WithoutMembership()
    {
        await SeedRootUserAndSuperAdminRoleAsync();
        var root = await LoginInProjectAsync(GlobalConstant.RootUsername, _betaSlug);

        var body = await (await SendAsync(root, "PATCH", "jobs")).Content.ReadAsStringAsync();

        body.Should().Contain(_betaMarker).And.NotContain(_alphaMarker);
    }

    #endregion

    #region Helpers

    private async Task<string> AssertNoForeignMarkerAsync(HttpClient client, IEnumerable<(string Method, string Path)> requests, string foreignMarker)
    {
        var combined = new StringBuilder();

        foreach (var (method, path) in requests)
        {
            var response = await SendAsync(client, method, path);
            var body = await response.Content.ReadAsStringAsync();

            body.Should().NotContain(foreignMarker, $"{method} {path} answered {(int)response.StatusCode} with another project's data");

            combined.Append(body);
        }

        return combined.ToString();
    }

    private async Task<HttpClient> LoginInProjectAsync(string userName, string projectSlug)
    {
        var client = await _factory.CreateClient().LoginAsync(userName);

        client.DefaultRequestHeaders.Add(ProjectResolution.HeaderName, projectSlug);

        return client;
    }

    private async Task<string> CreateApiKeyInBetaAsync()
    {
        var bob = await LoginInProjectAsync("bob", _betaSlug);

        // Permission ids as seeded by SeedCallersAsync; a key cannot manage keys.
        var permissionIds = _readPermissions.Select((permission, index) => (permission, id: 300 + index))
                                            .Where(p => !p.permission.StartsWith(nameof(PermissionCatalog.ApiKeyManagement)))
                                            .Select(p => p.id)
                                            .ToList();

        var response = await bob.PostAsJsonAsync($"{_baseUrl}/apikeys/apiKey", new { Name = "beta-key", PermissionIdList = permissionIds });
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);

        var data = GetProperty(document.RootElement, "data");

        return GetProperty(data, "key").GetString() ?? throw new InvalidOperationException($"No key in {body}");
    }

    private static JsonElement GetProperty(JsonElement element, string name)
        => element.EnumerateObject().First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static async Task<bool> IsSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            return false;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return GetProperty(document.RootElement, "isSuccess").GetBoolean();
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), $"{_baseUrl}/{path}");

        if (method != "GET")
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        return client.SendAsync(request);
    }

    private async Task<string> PostMcpAsync(string apiKey, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.Add(MessageConstant.ApiKey, apiKey);

        var response = await _factory.CreateClient().SendAsync(request);

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Reads tool names and their required arguments from a tools/list answer (plain JSON or a server-sent event).
    /// </summary>
    private static List<(string Name, List<(string Name, string Type)> Required)> ParseTools(string body)
    {
        var json = body.TrimStart().StartsWith('{') ? body : string.Join('\n', body.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..]));

        using var document = JsonDocument.Parse(json);

        var tools = new List<(string, List<(string, string)>)>();

        foreach (var tool in document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            var schema = tool.GetProperty("inputSchema");
            var required = new List<(string, string)>();

            if (schema.TryGetProperty("required", out var requiredNames))
            {
                foreach (var requiredName in requiredNames.EnumerateArray().Select(r => r.GetString()))
                {
                    var type = schema.GetProperty("properties").GetProperty(requiredName).TryGetProperty("type", out var t) ? t.ToString() : "string";
                    required.Add((requiredName, type));
                }
            }

            tools.Add((tool.GetProperty("name").GetString(), required));
        }

        return tools;
    }

    /// <summary>
    /// Argument sets for a tool: every id of <paramref name="data"/> for an id argument, a neutral value otherwise.
    /// </summary>
    private static IEnumerable<Dictionary<string, object>> ArgumentSets(List<(string Name, string Type)> required, ProjectData data)
    {
        var idArguments = required.Where(r => r.Name.EndsWith("id", StringComparison.OrdinalIgnoreCase) || r.Name.EndsWith("ids", StringComparison.OrdinalIgnoreCase)).ToList();

        object Neutral((string Name, string Type) argument) => argument.Type switch
        {
            var t when t.Contains("integer") || t.Contains("number") => 1,
            var t when t.Contains("boolean") => false,
            var t when t.Contains("array") => Array.Empty<string>(),
            _ => argument.Name.Contains("type", StringComparison.OrdinalIgnoreCase) ? "FailureRate" : "x"
        };

        if (idArguments.Count == 0)
        {
            yield return required.ToDictionary(r => r.Name, Neutral);
            yield break;
        }

        foreach (var id in data.Ids.Append(data.ApiKeyId.ToString()))
        {
            yield return required.ToDictionary(r => r.Name, r => idArguments.Contains(r)
                ? r.Type.Contains("array") ? new[] { id } : r.Type.Contains("integer") ? int.TryParse(id, out var n) ? n : 0 : id
                : Neutral(r));
        }
    }

    private static async Task<ProjectData> SeedProjectDataAsync(MilvaionDbContext db, int projectId, string marker)
    {
        var now = DateTime.UtcNow;

        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            DisplayName = $"{marker}-job",
            Description = $"{marker}-description",
            Tags = marker,
            JobNameInWorker = $"{marker}Job",
            WorkerId = $"{marker}-worker",
            JobData = "{}",
            ExecuteAt = now.AddDays(1),
            CronExpression = "0 0 12 * * *",
            IsActive = true,
            CreationDate = now,
            CreatorUserName = GlobalConstant.SystemUsername
        };

        var occurrence = new JobOccurrence
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            JobId = job.Id,
            JobName = $"{marker}Job",
            JobDisplayName = $"{marker}-job",
            CorrelationId = Guid.CreateVersion7(),
            WorkerId = $"{marker}-worker",
            Status = JobOccurrenceStatus.Failed,
            Exception = $"{marker}-exception",
            CreatedAt = now,
            StartTime = now,
            EndTime = now,
            DurationMs = 10
        };

        var log = new JobOccurrenceLog
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            OccurrenceId = occurrence.Id,
            Timestamp = now,
            Level = "Error",
            Message = $"{marker}-log",
            Category = "Leak"
        };

        var failed = new FailedOccurrence
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            JobId = job.Id,
            OccurrenceId = occurrence.Id,
            CorrelationId = occurrence.CorrelationId,
            JobDisplayName = $"{marker}-job",
            JobNameInWorker = $"{marker}Job",
            WorkerId = $"{marker}-worker",
            JobData = "{}",
            Exception = $"{marker}-exception",
            FailedAt = now
        };

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Name = $"{marker}-workflow",
            Description = $"{marker}-workflow",
            Definition = new() { Steps = [new() { Id = Guid.CreateVersion7(), JobId = job.Id, StepName = $"{marker}-step" }] },
            CreationDate = now
        };

        var run = new WorkflowRun
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            WorkflowId = workflow.Id,
            CorrelationId = Guid.CreateVersion7(),
            Status = WorkflowStatus.Completed,
            TriggerReason = $"{marker}-run",
            Error = $"{marker}-error",
            CreatedAt = now
        };

        var report = new MetricReport
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            MetricType = "FailureRate",
            DisplayName = $"{marker}-report",
            Description = $"{marker}-report",
            Data = $$"""{"note":"{{marker}}"}""",
            PeriodStartTime = now.AddDays(-1),
            PeriodEndTime = now,
            GeneratedAt = now
        };

        var calendar = new ExclusionCalendar { Id = Guid.CreateVersion7(), ProjectId = projectId, Name = $"{marker}-calendar", Description = $"{marker}-calendar", Weekdays = [0] };

        var apiKey = new MilvaionApiKey { ProjectId = projectId, Name = $"{marker}-key", Description = $"{marker}-key", MaskedKey = "mk_****", KeyVersion = 1 };

        var activity = new ActivityLog { ProjectId = projectId, UserName = $"{marker}-user", Activity = UserActivity.CreateUser, ActivityDate = DateTimeOffset.UtcNow };

        db.AddRange(job, occurrence, log, failed, workflow, run, report, calendar, apiKey, activity);

        await db.SaveChangesAsync();

        return new ProjectData(projectId, job.Id, occurrence.Id, failed.Id, workflow.Id, run.Id, calendar.Id, report.Id, apiKey.Id);
    }

    /// <summary>
    /// alice: the read permissions through a global role. bob: no global permissions, a member of beta through a project role.
    /// </summary>
    private async Task SeedCallersAsync(MilvaionDbContext db, int betaId)
    {
        var permissions = _readPermissions.Select((permission, index) => new Permission
        {
            Id = 300 + index,
            Name = permission.Split('.')[1],
            NormalizedName = permission.Split('.')[1].MilvaNormalize(),
            Description = permission,
            PermissionGroup = permission.Split('.')[0],
            PermissionGroupDescription = permission
        }).ToList();

        db.Permissions.AddRange(permissions);

        var globalRole = new Role
        {
            Name = "AliceRole",
            Scope = RoleScope.Instance,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername,
            RolePermissionRelations = [.. permissions.Select(p => new RolePermissionRelation { PermissionId = p.Id })]
        };

        var projectRole = new Role
        {
            Name = "Beta Reader",
            Scope = RoleScope.Project,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername,
            RolePermissionRelations = [.. permissions.Select(p => new RolePermissionRelation { PermissionId = p.Id })]
        };

        db.Roles.AddRange(globalRole, projectRole);
        await db.SaveChangesAsync();

        var userManager = db.ServiceProvider.GetRequiredService<IMilvaUserManager<User, int>>();

        User NewUser(string name) => new()
        {
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email = $"{name}@milvasoft.com",
            NormalizedEmail = $"{name}@milvasoft.com".ToUpperInvariant(),
            Name = name,
            Surname = "User",
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername,
            EmailConfirmed = true
        };

        var alice = NewUser("alice");
        alice.RoleRelations = [new() { RoleId = globalRole.Id }];
        userManager.SetPasswordHash(alice, "defaultpass");

        var bob = NewUser("bob");
        userManager.SetPasswordHash(bob, "defaultpass");

        db.Users.AddRange(alice, bob);
        await db.SaveChangesAsync();

        db.ProjectMemberships.Add(new ProjectMembership { ProjectId = betaId, UserId = bob.Id, RoleId = projectRole.Id, CreationDate = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    #endregion
}
