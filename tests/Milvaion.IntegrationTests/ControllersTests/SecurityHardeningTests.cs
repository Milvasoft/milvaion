using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Features.ApiKeys.CreateApiKey;
using Milvaion.Application.Features.Roles.UpdateRole;
using Milvaion.Application.Features.Users.UpdateUser;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Dtos.ApiKeyDtos;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain;
using Milvaion.Domain.Enums;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Helpers;
using Milvasoft.DataAccess.EfCore.Utils.LookupModels;
using Milvasoft.Identity.Abstract;
using Milvasoft.Types.Structs;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Authentication and authorization boundaries.")]
public class SecurityHardeningTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _root = $"{GlobalConstant.RoutePrefix}/v1.0";
    private const string _limitedUserName = "limited.user";
    private const string _limitedPassword = "Limited-Pass-1";

    [Theory]
    [InlineData("database/reset")]
    [InlineData("database/seed")]
    [InlineData("database/seed/fake")]
    [InlineData("database/reset/ui")]
    public async Task DeveloperEndpoints_WithoutToken_ShouldReturnUnauthorized(string path)
    {
        var response = await _factory.CreateClient().PostAsync($"{_root}/developer/{path}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeveloperEndpoints_WithoutSuperAdmin_ShouldReturnForbidden()
    {
        var client = await LoginLimitedUserAsync(PermissionCatalog.UserManagement.List);

        var response = await client.PostAsync($"{_root}/developer/database/reset", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task JobsHub_Negotiate_WithoutToken_ShouldReturnUnauthorized()
    {
        var response = await _factory.CreateClient().PostAsync("/hubs/jobs/negotiate?negotiateVersion=1", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task JobsHub_Negotiate_WithToken_ShouldSucceed()
    {
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        var response = await client.PostAsync("/hubs/jobs/negotiate?negotiateVersion=1", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateRole_AddingSuperAdminWithoutHoldingIt_ShouldReturnForbidden()
    {
        var client = await LoginLimitedUserAsync(PermissionCatalog.RoleManagement.Update);
        var role = await AddRoleAsync("target-role");

        var response = await client.PutAsJsonAsync($"{_root}/roles/role", new UpdateRoleCommand
        {
            Id = role.Id,
            PermissionIdList = new UpdateProperty<List<int>>([1])
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateRole_KeepingPermissionsTheCallerLacks_ShouldSucceed()
    {
        var client = await LoginLimitedUserAsync(PermissionCatalog.RoleManagement.Update);
        var otherPermission = await AddPermissionAsync(PermissionCatalog.WorkerManagement.List);
        var role = await AddRoleAsync("target-role", otherPermission.Id);

        var response = await client.PutAsJsonAsync($"{_root}/roles/role", new UpdateRoleCommand
        {
            Id = role.Id,
            PermissionIdList = new UpdateProperty<List<int>>([otherPermission.Id])
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateUser_ResettingSuperAdminPassword_ShouldReturnForbidden()
    {
        var client = await LoginLimitedUserAsync(PermissionCatalog.UserManagement.Update);
        var dbContext = GetDbContext();
        var root = dbContext.Users.First(u => u.UserName == GlobalConstant.RootUsername);

        var response = await client.PutAsJsonAsync($"{_root}/users/user", new UpdateUserCommand
        {
            Id = root.Id,
            NewPassword = new UpdateProperty<string>("Taken-Over-1")
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateUser_AssigningSuperAdminRole_ShouldReturnForbidden()
    {
        var client = await LoginLimitedUserAsync(PermissionCatalog.UserManagement.Update);
        var dbContext = GetDbContext();
        var limited = dbContext.Users.First(u => u.UserName == _limitedUserName);
        var superAdminRole = dbContext.Roles.First(r => r.Name == nameof(PermissionCatalog.App.SuperAdmin));

        var response = await client.PutAsJsonAsync($"{_root}/users/user", new UpdateUserCommand
        {
            Id = limited.Id,
            RoleIdList = new UpdateProperty<List<int>>([superAdminRole.Id])
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateApiKey_WithSuperAdminPermission_ShouldBeForbiddenEvenForSuperAdmin()
    {
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        var response = await client.PostAsJsonAsync($"{_root}/apikeys/apikey", new CreateApiKeyCommand
        {
            Name = "too-strong",
            PermissionIdList = [1]
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ApiKeyManagement_WithApiKey_ShouldReturnForbidden()
    {
        await SeedRootUserAndSuperAdminRoleAsync();
        var permission = await AddPermissionAsync(PermissionCatalog.ApiKeyManagement.Detail);
        var rootClient = await _factory.CreateClient().LoginAsync();

        var created = await rootClient.PostAsJsonAsync($"{_root}/apikeys/apikey", new CreateApiKeyCommand
        {
            Name = "ci",
            PermissionIdList = [permission.Id]
        });

        var key = (await created.Content.ReadFromJsonAsync<Response<CreatedApiKeyDto>>()).Data;
        key.Should().NotBeNull();

        var keyClient = _factory.CreateClient();
        keyClient.DefaultRequestHeaders.Add("X-ApiKey", key.Key);

        var response = await keyClient.GetAsync($"{_root}/apikeys/apikey?id={key.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Lookup_WithoutEntityListPermission_ShouldReturnForbidden()
    {
        var client = await LoginLimitedUserAsync(PermissionCatalog.WorkerManagement.List);

        var response = await PatchLookupAsync(client, "User", "UserName");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Lookup_OfPasswordHash_ShouldReturnForbiddenEvenForSuperAdmin()
    {
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        var response = await PatchLookupAsync(client, "User", "PasswordHash");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ExternalIdentity_GroupNamedAfterLocalRole_ShouldNotInheritIt()
    {
        await SeedRootUserAndSuperAdminRoleAsync();
        var permission = await AddPermissionAsync(PermissionCatalog.WorkerManagement.List);
        await AddRoleAsync("local-admins", permission.Id);

        var service = _serviceProvider.GetRequiredService<IExternalIdentityService>();

        var claims = await service.ResolveAndBuildClaimsAsync(new ExternalIdentityDescriptor
        {
            Provider = ExternalProvider.Oidc,
            Issuer = "https://idp.example.test",
            Subject = "subject-1",
            UserName = "sso.user",
            RoleNames = [nameof(PermissionCatalog.App.SuperAdmin), "local-admins"]
        });

        claims.Where(c => c.Type == ClaimTypes.Role).Should().BeEmpty();
    }

    [Fact]
    public async Task ExternalIdentity_RoleOwnedByProvider_ShouldGrantItsPermissions()
    {
        var permission = await AddPermissionAsync(PermissionCatalog.WorkerManagement.List);
        var dbContext = GetDbContext();

        await dbContext.Roles.AddAsync(new Role
        {
            Name = "idp-operators",
            Provider = ExternalProvider.Oidc,
            RolePermissionRelations = [new() { PermissionId = permission.Id }]
        });
        await dbContext.SaveChangesAsync();

        var service = _serviceProvider.GetRequiredService<IExternalIdentityService>();

        var claims = await service.ResolveAndBuildClaimsAsync(new ExternalIdentityDescriptor
        {
            Provider = ExternalProvider.Oidc,
            Issuer = "https://idp.example.test",
            Subject = "subject-2",
            UserName = "sso.operator",
            RoleNames = ["idp-operators"]
        });

        claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == PermissionCatalog.WorkerManagement.List);
    }

    private static Task<HttpResponseMessage> PatchLookupAsync(HttpClient client, string entityName, string propertyName)
        => client.PatchAsJsonAsync($"{_root}/lookups", new LookupRequest
        {
            Parameters = [new() { EntityName = entityName, RequestedPropertyNames = [propertyName] }]
        });

    /// <summary>Seeds the root user as well, so the super admin permission and role exist for the test to target.</summary>
    private async Task<HttpClient> LoginLimitedUserAsync(params string[] permissions)
    {
        await SeedRootUserAndSuperAdminRoleAsync();

        var permissionIds = new List<int>();

        foreach (var permission in permissions)
            permissionIds.Add((await AddPermissionAsync(permission)).Id);

        var role = await AddRoleAsync("limited-role", [.. permissionIds]);

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var user = new User
        {
            UserName = _limitedUserName,
            NormalizedUserName = _limitedUserName.ToUpperInvariant(),
            Email = "limited@example.test",
            NormalizedEmail = "LIMITED@EXAMPLE.TEST",
            Name = "Limited",
            Surname = "User",
            RoleRelations = [new() { RoleId = role.Id }]
        };

        dbContext.ServiceProvider.GetService<IMilvaUserManager<User, int>>().SetPasswordHash(user, _limitedPassword);

        await dbContext.Users.AddAsync(user);
        await dbContext.SaveChangesAsync();

        return await _factory.CreateClient().LoginAsync(_limitedUserName, _limitedPassword);
    }

    private async Task<Permission> AddPermissionAsync(string fullName)
    {
        var parts = fullName.Split('.');
        var dbContext = GetDbContext();

        var permission = new Permission
        {
            Name = parts[1],
            NormalizedName = parts[1].MilvaNormalize(),
            Description = fullName,
            PermissionGroup = parts[0],
            PermissionGroupDescription = parts[0]
        };

        await dbContext.Permissions.AddAsync(permission);
        await dbContext.SaveChangesAsync();

        return permission;
    }

    private async Task<Role> AddRoleAsync(string name, params int[] permissionIds)
    {
        var dbContext = GetDbContext();

        var role = new Role
        {
            Name = name,
            RolePermissionRelations = [.. permissionIds.Select(id => new RolePermissionRelation { PermissionId = id })]
        };

        await dbContext.Roles.AddAsync(role);
        await dbContext.SaveChangesAsync();

        return role;
    }
}
