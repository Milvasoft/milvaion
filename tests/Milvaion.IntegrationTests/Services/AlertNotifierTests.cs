using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Domain.Enums;
using Milvaion.Domain.JsonModels;
using Milvaion.Infrastructure.Services.Alerting;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.Services;

/// <summary>
/// Integration tests for AlertNotifier.
/// Tests alert routing, channel dispatching, and configuration-based behavior.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class AlertNotifierTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : RedisServiceTestBase(factory, output)
{
    [Fact]
    public async Task SendAsync_ShouldReturnSkipped_WhenAlertTypeIsDisabled()
    {
        // Arrange
        await InitializeAsync();

        var options = new AlertingOptions
        {
            Alerts = new Dictionary<AlertType, AlertConfig>
            {
                [AlertType.ZombieOccurrenceDetected] = new AlertConfig { Enabled = false }
            }
        };

        var notifier = CreateAlertNotifier(options);

        var payload = new AlertPayload
        {
            Title = "Test Alert",
            Message = "Test message",
            Severity = AlertSeverity.Warning,
            Source = "IntegrationTest"
        };

        // Act
        var result = await notifier.SendAsync(AlertType.ZombieOccurrenceDetected, payload);

        // Assert
        result.Success.Should().BeTrue();
        result.ChannelResults.FirstOrDefault().ChannelName.Should().Be("N/A");
        result.ChannelResults.FirstOrDefault().Message.Should().Contain("disabled");
    }

    [Fact]
    public async Task SendAsync_ShouldReturnSkipped_WhenNoRoutesConfigured()
    {
        // Arrange
        await InitializeAsync();

        var options = new AlertingOptions
        {
            DefaultChannel = null,
            Alerts = new Dictionary<AlertType, AlertConfig>
            {
                [AlertType.ZombieOccurrenceDetected] = new AlertConfig
                {
                    Enabled = true,
                    Routes = []
                }
            }
        };

        var notifier = CreateAlertNotifier(options, []);

        var payload = new AlertPayload
        {
            Title = "Test Alert",
            Message = "Test message"
        };

        // Act
        var result = await notifier.SendAsync(AlertType.ZombieOccurrenceDetected, payload);

        // Assert
        result.Success.Should().BeTrue();
        result.ChannelResults.FirstOrDefault().ChannelName.Should().Be("N/A");
        result.ChannelResults.FirstOrDefault().Message.Should().Contain("No routes configured");
    }

    [Fact]
    public async Task SendAsync_ShouldRouteToConfiguredChannel()
    {
        // Arrange
        await InitializeAsync();

        var stubChannel = new StubAlertChannel("TestChannel", ChannelResult.Successful("TestChannel"));

        var options = new AlertingOptions
        {
            Alerts = new Dictionary<AlertType, AlertConfig>
            {
                [AlertType.JobExecutionFailed] = new AlertConfig
                {
                    Enabled = true,
                    Routes = ["TestChannel"]
                }
            }
        };

        var notifier = CreateAlertNotifier(options, [stubChannel]);

        var payload = new AlertPayload
        {
            Title = "Job Failed",
            Message = "Test job execution failed",
            Severity = AlertSeverity.Critical
        };

        // Act
        var result = await notifier.SendAsync(AlertType.JobExecutionFailed, payload);

        // Assert
        result.Success.Should().BeTrue();
        stubChannel.SendCallCount.Should().Be(1);
    }

    [Fact]
    public async Task IsAlertEnabled_ShouldReturnTrue_WhenNotConfigured()
    {
        // Arrange
        await InitializeAsync();

        var options = new AlertingOptions();
        var notifier = CreateAlertNotifier(options);

        // Act
        var isEnabled = notifier.IsAlertEnabled(AlertType.ZombieOccurrenceDetected);

        // Assert
        isEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task IsAlertEnabled_ShouldReturnFalse_WhenDisabled()
    {
        // Arrange
        await InitializeAsync();

        var options = new AlertingOptions
        {
            Alerts = new Dictionary<AlertType, AlertConfig>
            {
                [AlertType.ZombieOccurrenceDetected] = new AlertConfig { Enabled = false }
            }
        };

        var notifier = CreateAlertNotifier(options);

        // Act
        var isEnabled = notifier.IsAlertEnabled(AlertType.ZombieOccurrenceDetected);

        // Assert
        isEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetRoutesForAlert_ShouldReturnDefaultChannel_WhenNoSpecificRoutes()
    {
        // Arrange
        await InitializeAsync();

        var options = new AlertingOptions
        {
            DefaultChannel = "DefaultTestChannel"
        };

        var notifier = CreateAlertNotifier(options);

        // Act
        var routes = notifier.GetRoutesForAlert(AlertType.ZombieOccurrenceDetected);

        // Assert
        routes.Should().ContainSingle().Which.Should().Be("DefaultTestChannel");
    }

    [Fact]
    public async Task GetRoutesForAlert_ShouldReturnConfiguredRoutes()
    {
        // Arrange
        await InitializeAsync();

        var options = new AlertingOptions
        {
            Alerts = new Dictionary<AlertType, AlertConfig>
            {
                [AlertType.JobAutoDisabled] = new AlertConfig
                {
                    Enabled = true,
                    Routes = ["Email", "Slack"]
                }
            }
        };

        var notifier = CreateAlertNotifier(options);

        // Act
        var routes = notifier.GetRoutesForAlert(AlertType.JobAutoDisabled);

        // Assert
        routes.Should().HaveCount(2);
        routes.Should().Contain("Email");
        routes.Should().Contain("Slack");
    }

    [Fact]
    public async Task SendAsync_ShouldBuildFullActionUrl_WhenRelativePathProvided()
    {
        // Arrange
        await InitializeAsync();

        var stubChannel = new StubAlertChannel("TestChannel", ChannelResult.Successful("TestChannel"));

        var options = new AlertingOptions
        {
            MilvaionAppUrl = "https://milvaion.example.com",
            Alerts = new Dictionary<AlertType, AlertConfig>
            {
                [AlertType.JobAutoDisabled] = new AlertConfig
                {
                    Enabled = true,
                    Routes = ["TestChannel"]
                }
            }
        };

        var notifier = CreateAlertNotifier(options, [stubChannel]);

        var payload = new AlertPayload
        {
            Title = "Job Disabled",
            Message = "Job was auto-disabled",
            ActionLink = "/jobs/123"
        };

        // Act
        await notifier.SendAsync(AlertType.JobAutoDisabled, payload);

        // Assert
        stubChannel.LastPayload.Should().NotBeNull();
        stubChannel.LastPayload.ActionLink.Should().Be("https://milvaion.example.com/jobs/123");
    }

    [Fact]
    public async Task SendAsync_WithMultipleChannels_ShouldSendToAll()
    {
        // Arrange
        await InitializeAsync();

        var stubChannel1 = new StubAlertChannel("Channel1", ChannelResult.Successful("Channel1"));
        var stubChannel2 = new StubAlertChannel("Channel2", ChannelResult.Successful("Channel2"));

        var options = new AlertingOptions
        {
            Alerts = new Dictionary<AlertType, AlertConfig>
            {
                [AlertType.QueueDepthCritical] = new AlertConfig
                {
                    Enabled = true,
                    Routes = ["Channel1", "Channel2"]
                }
            }
        };

        var notifier = CreateAlertNotifier(options, [stubChannel1, stubChannel2]);

        var payload = new AlertPayload
        {
            Title = "Queue Critical",
            Message = "Queue depth is critical"
        };

        // Act
        var result = await notifier.SendAsync(AlertType.QueueDepthCritical, payload);

        // Assert
        result.Success.Should().BeTrue();
        stubChannel1.SendCallCount.Should().Be(1);
        stubChannel2.SendCallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailed_WhenChannelThrowsException()
    {
        // Arrange
        await InitializeAsync();

        var failingChannel = new FailingAlertChannel("FailChannel", new Exception("SMTP timeout"));

        var options = new AlertingOptions
        {
            Alerts = new Dictionary<AlertType, AlertConfig>
            {
                [AlertType.JobExecutionFailed] = new AlertConfig
                {
                    Enabled = true,
                    Routes = ["FailChannel"]
                }
            }
        };

        var notifier = CreateAlertNotifier(options, [failingChannel]);

        var payload = new AlertPayload
        {
            Title = "Job Failed",
            Message = "Test failure"
        };

        // Act
        var result = await notifier.SendAsync(AlertType.JobExecutionFailed, payload);

        // Assert
        result.Success.Should().BeFalse();
        result.ChannelResults.Should().ContainSingle();
        result.ChannelResults.First().Success.Should().BeFalse();
        result.ChannelResults.First().Message.Should().Contain("SMTP timeout");
    }

    [Fact]
    public async Task SendAsync_ShouldReturnSkippedForUnregisteredChannel()
    {
        // Arrange
        await InitializeAsync();

        var options = new AlertingOptions
        {
            Alerts = new Dictionary<AlertType, AlertConfig>
            {
                [AlertType.JobExecutionFailed] = new AlertConfig
                {
                    Enabled = true,
                    Routes = ["NonExistentChannel"]
                }
            }
        };

        var notifier = CreateAlertNotifier(options, []);

        var payload = new AlertPayload
        {
            Title = "Test Alert",
            Message = "Should skip"
        };

        // Act
        var result = await notifier.SendAsync(AlertType.JobExecutionFailed, payload);

        // Assert
        result.Success.Should().BeTrue();
        result.ChannelResults.Should().ContainSingle();
        result.ChannelResults.First().Message.Should().Contain("not registered");
    }

    [Fact]
    public async Task SendAsync_ShouldPartialSucceed_WhenOneChannelFailsAndOneSucceeds()
    {
        // Arrange
        await InitializeAsync();

        var successChannel = new StubAlertChannel("SuccessChannel", ChannelResult.Successful("SuccessChannel"));
        var failingChannel = new FailingAlertChannel("FailChannel", new Exception("Network error"));

        var options = new AlertingOptions
        {
            Alerts = new Dictionary<AlertType, AlertConfig>
            {
                [AlertType.QueueDepthCritical] = new AlertConfig
                {
                    Enabled = true,
                    Routes = ["SuccessChannel", "FailChannel"]
                }
            }
        };

        var notifier = CreateAlertNotifier(options, [successChannel, failingChannel]);

        var payload = new AlertPayload
        {
            Title = "Queue Critical",
            Message = "Partial test"
        };

        // Act
        var result = await notifier.SendAsync(AlertType.QueueDepthCritical, payload);

        // Assert - Should be success because at least one channel succeeded
        result.Success.Should().BeTrue();
        result.ChannelResults.Should().HaveCount(2);
        successChannel.SendCallCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_ShouldNotBuildActionUrl_WhenActionLinkIsNullOrEmpty()
    {
        // Arrange
        await InitializeAsync();

        var stubChannel = new StubAlertChannel("TestChannel", ChannelResult.Successful("TestChannel"));

        var options = new AlertingOptions
        {
            MilvaionAppUrl = "https://milvaion.example.com",
            Alerts = new Dictionary<AlertType, AlertConfig>
            {
                [AlertType.JobAutoDisabled] = new AlertConfig
                {
                    Enabled = true,
                    Routes = ["TestChannel"]
                }
            }
        };

        var notifier = CreateAlertNotifier(options, [stubChannel]);

        var payload = new AlertPayload
        {
            Title = "No Link Alert",
            Message = "No action link",
            ActionLink = null
        };

        // Act
        await notifier.SendAsync(AlertType.JobAutoDisabled, payload);

        // Assert
        stubChannel.LastPayload.ActionLink.Should().BeNull();
    }

    [Fact]
    public async Task GetRoutesForAlert_ShouldReturnEmpty_WhenNoDefaultAndNoSpecificRoutes()
    {
        // Arrange
        await InitializeAsync();

        var options = new AlertingOptions
        {
            DefaultChannel = null,
            Alerts = null
        };

        var notifier = CreateAlertNotifier(options);

        // Act
        var routes = notifier.GetRoutesForAlert(AlertType.ZombieOccurrenceDetected);

        // Assert
        routes.Should().BeEmpty();
    }

    private AlertNotifier CreateAlertNotifier(AlertingOptions options, IAlertChannel[] channels = null)
    {
        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
        var lazyLogger = new Lazy<IMilvaLogger>(() => loggerFactory.CreateMilvaLogger<AlertNotifier>());

        // These tests exercise the appsettings-based routing/enablement. Use a settings provider
        // with no runtime rules so the notifier falls back to the AlertingOptions under test,
        // rather than the seeded runtime rules from the real provider.
        return new AlertNotifier(
            Options.Create(options),
            channels ?? [],
            lazyLogger,
            new EmptyRuntimeSettingsProvider());
    }

    /// <summary>
    /// A settings provider with no notification rules, so <see cref="AlertNotifier"/> falls back
    /// to the appsettings <see cref="AlertingOptions"/> under test.
    /// </summary>
    private sealed class EmptyRuntimeSettingsProvider : ISettingsProvider
    {
        private readonly AppSettingsDocument _document = new();

        public AppSettingsDocument Current => _document;

        public Task<AppSettingsDocument> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(_document);

        public Task UpdateAsync(AppSettingsDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Stub alert channel for testing.
    /// </summary>
    private sealed class StubAlertChannel(string channelName, ChannelResult resultToReturn) : IAlertChannel
    {
        public string ChannelName => channelName;
        public bool IsEnabled => true;
        public int SendCallCount { get; private set; }
        public AlertPayload LastPayload { get; private set; }

        public bool CanSend() => true;

        public Task<ChannelResult> SendAsync(AlertType alertType, AlertPayload payload, CancellationToken cancellationToken = default)
        {
            SendCallCount++;
            LastPayload = payload;
            return Task.FromResult(resultToReturn);
        }
    }

    /// <summary>
    /// Alert channel that always throws an exception.
    /// </summary>
    private sealed class FailingAlertChannel(string channelName, Exception exceptionToThrow) : IAlertChannel
    {
        public string ChannelName => channelName;
        public bool IsEnabled => true;

        public bool CanSend() => true;

        public Task<ChannelResult> SendAsync(AlertType alertType, AlertPayload payload, CancellationToken cancellationToken = default)
            => throw exceptionToThrow;
    }
}
