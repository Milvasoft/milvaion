using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.AccountDtos;
using Milvaion.Application.Features.Account.Login;
using Milvaion.Application.Utils.Constants;
using Milvaion.Domain;
using Milvaion.Domain.Enums;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for controllers.")]
public class AccountControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    #region Login

    [Fact]
    public async Task LoginAsync_WithInvalidUsernameAndPassword_ShouldNotLogin()
    {
        // Arrange
        var request = new LoginCommand
        {
            UserName = "admin",
            Password = "admin",
            DeviceId = "device-id"
        };

        // Act
        var httpResponse = await _factory.CreateClient().PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);
        var loginResult = await httpResponse.Content.ReadFromJsonAsync<Response<LoginResponseDto>>();

        // Arrange
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        loginResult.Should().NotBeNull();
        loginResult.Data.Should().BeNull();
        loginResult.IsSuccess.Should().BeFalse();
        loginResult.IsCachedData.Should().BeFalse();
        loginResult.Metadatas.Should().BeEmpty();
        loginResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task LoginAsync_WithValidUsernameButInvalidPassword_ShouldNotLogin()
    {
        // Arrange
        var request = new LoginCommand
        {
            UserName = "bugrakosen",
            Password = "string",
            DeviceId = "device-id"
        };

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var user = new User
        {
            UserName = "bugrakosen",
            PasswordHash = "AQAAAAEAACcQAAAAEJ9"
        };

        await dbContext.AddAsync(user);
        await dbContext.SaveChangesAsync();

        // Act
        var httpResponse = await _factory.CreateClient().PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);
        var loginResult = await httpResponse.Content.ReadFromJsonAsync<Response<LoginResponseDto>>();

        // Arrange
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        loginResult.Should().NotBeNull();
        loginResult.Data.Should().BeNull();
        loginResult.IsSuccess.Should().BeFalse();
        loginResult.IsCachedData.Should().BeFalse();
        loginResult.Metadatas.Should().BeEmpty();
        loginResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task LoginAsync_WithValidUsernameButInvalidPasswordAndFailedMaxLockoutCount_ShouldNotLoginAndLockUser()
    {
        // Arrange
        var request = new LoginCommand
        {
            UserName = "bugrakosen",
            Password = "string",
            DeviceId = "device-id"
        };

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var user = new User
        {
            UserName = "bugrakosen",
            PasswordHash = "AQAAAAEAACcQAAAAEJ9",
            LockoutEnabled = true,
            AccessFailedCount = 0
        };

        await dbContext.AddAsync(user);
        await dbContext.SaveChangesAsync();

        // Act
        var client = _factory.CreateClientWithLanguageHeader("en-US");
        await client.PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);
        await client.PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);
        await client.PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);

        var beforeLockHttpResponse = await client.PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);
        var beforeLockLoginResult = await beforeLockHttpResponse.Content.ReadFromJsonAsync<Response<LoginResponseDto>>();
        var userBeforeLockout = await dbContext.Users.FirstOrDefaultAsync();

        var lockHttpResponse = await client.PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);
        var lockLoginResult = await lockHttpResponse.Content.ReadFromJsonAsync<Response<LoginResponseDto>>();
        var userOnLockout = await dbContext.Users.FirstOrDefaultAsync();

        var afterLockHttpResponse = await client.PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);
        var afterLockLoginResult = await afterLockHttpResponse.Content.ReadFromJsonAsync<Response<LoginResponseDto>>();
        var userAfterLockout = await dbContext.Users.FirstOrDefaultAsync();

        // Arrange
        beforeLockHttpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        beforeLockLoginResult.Should().NotBeNull();
        beforeLockLoginResult.Messages[0].Message.Should().Be("The session will be locked after 1 invalid attempts.");
        beforeLockLoginResult.Data.Should().BeNull();
        beforeLockLoginResult.IsSuccess.Should().BeFalse();
        beforeLockLoginResult.IsCachedData.Should().BeFalse();
        beforeLockLoginResult.Metadatas.Should().BeEmpty();
        beforeLockLoginResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        userBeforeLockout.Should().NotBeNull();
        userBeforeLockout.LockoutEnabled.Should().BeTrue();
        userBeforeLockout.AccessFailedCount.Should().Be(4);
        userBeforeLockout.LockoutEnd.Should().BeNull();

        lockHttpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        lockLoginResult.Should().NotBeNull();
        lockLoginResult.Messages[0].Message.Should().Be("Your account has been locked due to repeated unsuccessful login attempts. Try again after 4 minutes.");
        lockLoginResult.Data.Should().BeNull();
        lockLoginResult.IsSuccess.Should().BeFalse();
        lockLoginResult.IsCachedData.Should().BeFalse();
        lockLoginResult.Metadatas.Should().BeEmpty();
        lockLoginResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        userOnLockout.Should().NotBeNull();
        userOnLockout.LockoutEnabled.Should().BeTrue();
        userOnLockout.AccessFailedCount.Should().Be(0);
        userOnLockout.LockoutEnd.Should().NotBeNull();

        afterLockHttpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        afterLockLoginResult.Should().NotBeNull();
        afterLockLoginResult.Messages[0].Message.Should().Be("Your account has been locked due to repeated unsuccessful login attempts. Try again after 4 minutes.");
        afterLockLoginResult.Data.Should().BeNull();
        afterLockLoginResult.IsSuccess.Should().BeFalse();
        afterLockLoginResult.IsCachedData.Should().BeFalse();
        afterLockLoginResult.Metadatas.Should().BeEmpty();
        afterLockLoginResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);

        userAfterLockout.Should().NotBeNull();
        userAfterLockout.LockoutEnabled.Should().BeTrue();
        userAfterLockout.AccessFailedCount.Should().Be(0);
        userAfterLockout.LockoutEnd.Should().NotBeNull();
    }

    [Fact]
    public async Task LoginAsync_WithLockedUser_ShouldNotLogin()
    {
        // Arrange
        var request = new LoginCommand
        {
            UserName = "bugrakosen",
            Password = "string",
            DeviceId = "device-id"
        };

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var user = new User
        {
            UserName = "bugrakosen",
            PasswordHash = "AQAAAAEAACcQAAAAEIMgDN79W03UXM/6VMhU4ua5i6DRRmS/kj77Jdfi1vjxSUnugS7ZF1CszYSLuhKvpw==",
            LockoutEnabled = true,
            AccessFailedCount = 25,
            LockoutEnd = DateTime.UtcNow.AddMinutes(10)
        };

        await dbContext.AddAsync(user);
        await dbContext.SaveChangesAsync();

        // Act
        var httpResponse = await _factory.CreateClient().PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);
        var loginResult = await httpResponse.Content.ReadFromJsonAsync<Response<LoginResponseDto>>();

        // Arrange
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        loginResult.Should().NotBeNull();
        loginResult.Data.Should().BeNull();
        loginResult.IsSuccess.Should().BeFalse();
        loginResult.IsCachedData.Should().BeFalse();
        loginResult.Metadatas.Should().BeEmpty();
        loginResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task LoginAsync_WithLockedUserButSuccessfullLogin_ShouldLoginAndResetLockout()
    {
        // Arrange
        var request = new LoginCommand
        {
            UserName = "bugrakosen",
            Password = "string",
            DeviceId = "device-id"
        };

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var user = new User
        {
            UserName = "bugrakosen",
            PasswordHash = "AQAAAAEAACcQAAAAEIMgDN79W03UXM/6VMhU4ua5i6DRRmS/kj77Jdfi1vjxSUnugS7ZF1CszYSLuhKvpw==",
            LockoutEnabled = true,
            AccessFailedCount = 25,
            LockoutEnd = DateTime.UtcNow.AddMinutes(-1)
        };

        await dbContext.AddAsync(user);
        await dbContext.SaveChangesAsync();

        // Act
        var httpResponse = await _factory.CreateClient().PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);
        var loginResult = await httpResponse.Content.ReadFromJsonAsync<Response<LoginResponseDto>>();

        // Arrange
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        loginResult.Should().NotBeNull();
        loginResult.Data.Should().NotBeNull();
        loginResult.Data.UserType.Should().Be(UserType.AppUser);
        loginResult.Data.Token.Should().NotBeNull();
        loginResult.Data.Token.ExpiresIn.Should().NotBe(0);
        loginResult.Data.Token.RefreshTokenExpiresIn.Should().NotBe(0);
        loginResult.Data.Token.RefreshToken.Should().NotBeNull();
        loginResult.Data.Token.TokenType.Should().Be("Bearer");
        loginResult.Data.Token.Scope.Should().Be("Milvaion");
        loginResult.Data.Token.SessionState.Should().BeNull();
        loginResult.Data.AccessibleMenuItems.Should().BeNullOrEmpty();
        loginResult.Data.PageInformations.Should().BeNullOrEmpty();

        loginResult.IsSuccess.Should().BeTrue();
        loginResult.IsCachedData.Should().BeFalse();
        loginResult.Metadatas.Should().BeEmpty();
        loginResult.StatusCode.Should().Be((int)HttpStatusCode.OK);

        user = await dbContext.Users.FirstOrDefaultAsync();
        user.Should().NotBeNull();
        user.LockoutEnabled.Should().BeTrue();
        user.LockoutEnd.Should().BeNull();
        user.AccessFailedCount.Should().Be(0);
    }

    [Fact]
    public async Task LoginAsync_WithValidUsernameAndPasswordButInvalidDeviceId_ShouldNotLogin()
    {
        // Arrange
        var request = new LoginCommand
        {
            UserName = "bugrakosen",
            Password = "string",
            DeviceId = ""
        };

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var user = new User
        {
            UserName = "bugrakosen",
            PasswordHash = "AQAAAAEAACcQAAAAEJ9"
        };

        await dbContext.AddAsync(user);
        await dbContext.SaveChangesAsync();

        // Act
        var httpResponse = await _factory.CreateClient().PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);
        var loginResult = await httpResponse.Content.ReadFromJsonAsync<Response<LoginResponseDto>>();

        // Arrange
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        loginResult.Should().NotBeNull();
        loginResult.Data.Should().BeNull();
        loginResult.IsSuccess.Should().BeFalse();
        loginResult.IsCachedData.Should().BeFalse();
        loginResult.Metadatas.Should().BeEmpty();
        loginResult.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task LoginAsync_WithValidCredentials_ShouldLogin()
    {
        // Arrange
        var request = new LoginCommand
        {
            UserName = "bugrakosen",
            Password = "string",
            DeviceId = "device-id"
        };

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var user = new User
        {
            UserName = "bugrakosen",
            PasswordHash = "AQAAAAEAACcQAAAAEIMgDN79W03UXM/6VMhU4ua5i6DRRmS/kj77Jdfi1vjxSUnugS7ZF1CszYSLuhKvpw=="
        };

        await dbContext.AddAsync(user);
        await dbContext.SaveChangesAsync();

        // Act
        var httpResponse = await _factory.CreateClient().PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/login", request);
        var loginResult = await httpResponse.Content.ReadFromJsonAsync<Response<LoginResponseDto>>();

        // Arrange
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        loginResult.Should().NotBeNull();
        loginResult.Data.Should().NotBeNull();
        loginResult.Data.UserType.Should().Be(UserType.AppUser);
        loginResult.Data.Token.Should().NotBeNull();
        loginResult.Data.Token.ExpiresIn.Should().NotBe(0);
        loginResult.Data.Token.RefreshTokenExpiresIn.Should().NotBe(0);
        loginResult.Data.Token.RefreshToken.Should().NotBeNull();
        loginResult.Data.Token.TokenType.Should().Be("Bearer");
        loginResult.Data.Token.Scope.Should().Be("Milvaion");
        loginResult.Data.Token.SessionState.Should().BeNull();
        loginResult.Data.AccessibleMenuItems.Should().BeNullOrEmpty();
        loginResult.Data.PageInformations.Should().BeNullOrEmpty();

        loginResult.IsSuccess.Should().BeTrue();
        loginResult.IsCachedData.Should().BeFalse();
        loginResult.Metadatas.Should().BeEmpty();
        loginResult.StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    #endregion

    #region AccountDetailsAsync

    [Fact]
    public async Task AccountDetailsAsync_WithLoggedInUser_ShouldReturnAccountDetails()
    {
        // Arrange
        var user = await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{GlobalConstant.RoutePrefix}/v1.0/account/detail?UserId={user.Id}");
        var loginResult = await httpResponse.Content.ReadFromJsonAsync<Response<AccountDetailDto>>();

        // Arrange
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        loginResult.Should().NotBeNull();
        loginResult.Data.Should().NotBeNull();

        loginResult.IsSuccess.Should().BeTrue();
        loginResult.IsCachedData.Should().BeFalse();
        loginResult.Metadatas.Should().BeEmpty();
        loginResult.StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    #endregion
}