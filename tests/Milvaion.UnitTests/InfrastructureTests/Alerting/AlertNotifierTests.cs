using FluentAssertions;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Domain.Enums;
using Milvaion.Domain.JsonModels;
using Milvaion.Infrastructure.Services.Alerting;
using Milvasoft.Core.Abstractions;
using Moq;

namespace Milvaion.UnitTests.InfrastructureTests.Alerting;

[Trait("Infrastructure Unit Tests", "AlertNotifier unit tests.")]
public class AlertNotifierTests
{
    private static AlertPayload CreatePayload(string title = "Test Alert", AlertSeverity severity = AlertSeverity.Info) => new()
    {
        Title = title,
        Message = "Test message",
        Severity = severity,
        Source = "UnitTest",
        Timestamp = DateTime.UtcNow
    };

    private static AlertingOptions CreateOptions(string defaultChannel = "Slack", bool sendOnlyInProduction = false, Dictionary<AlertType, AlertConfig> alerts = null) => new()
    {
        DefaultChannel = defaultChannel,
        SendOnlyInProduction = sendOnlyInProduction,
        Alerts = alerts ?? []
    };

    private static Mock<IAlertChannel> CreateMockChannel(string channelName, bool success = true)
    {
        var mock = new Mock<IAlertChannel>();
        mock.Setup(c => c.ChannelName).Returns(channelName);
        mock.Setup(c => c.IsEnabled).Returns(true);
        mock.Setup(c => c.CanSend()).Returns(true);
        mock.Setup(c => c.SendAsync(It.IsAny<AlertType>(), It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(success
                ? ChannelResult.Successful(channelName)
                : ChannelResult.Failed(channelName, "Simulated failure"));
        return mock;
    }

    private static AlertNotifier CreateNotifier(AlertingOptions options, params IAlertChannel[] channels)
    {
        var loggerMock = new Mock<IMilvaLogger>();

        var settingsProviderMock = new Mock<ISettingsProvider>();
        settingsProviderMock.Setup(s => s.Current).Returns(new AppSettingsDocument());

        return new AlertNotifier(
            Options.Create(options),
            channels,
            new Lazy<IMilvaLogger>(() => loggerMock.Object),
            settingsProviderMock.Object);
    }

    #region SendAsync

    [Fact]
    public async Task SendAsync_ShouldReturnSkipped_WhenAlertTypeIsDisabled()
    {
        // Arrange
        var options = CreateOptions(alerts: new Dictionary<AlertType, AlertConfig>
        {
            [AlertType.JobAutoDisabled] = new AlertConfig { Enabled = false }
        });
        var notifier = CreateNotifier(options);

        // Act
        var result = await notifier.SendAsync(AlertType.JobAutoDisabled, CreatePayload());

        // Assert
        result.Success.Should().BeTrue();
        result.ChannelResults.Should().ContainSingle()
              .Which.Message.Should().Contain("disabled");
    }

    [Fact]
    public async Task SendAsync_ShouldReturnSkipped_WhenNoRoutesConfigured()
    {
        // Arrange
        var options = CreateOptions(defaultChannel: ""); // No default channel
        var notifier = CreateNotifier(options);

        // Act
        var result = await notifier.SendAsync(AlertType.ZombieOccurrenceDetected, CreatePayload());

        // Assert
        result.Success.Should().BeTrue();
        result.ChannelResults.Should().ContainSingle()
              .Which.Message.Should().Contain("No routes");
    }

    [Fact]
    public async Task SendAsync_ShouldRouteToDefaultChannel_WhenNoSpecificRoutesConfigured()
    {
        // Arrange
        var mockChannel = CreateMockChannel("Slack");
        var options = CreateOptions(defaultChannel: "Slack");
        var notifier = CreateNotifier(options, mockChannel.Object);

        // Act
        var result = await notifier.SendAsync(AlertType.ZombieOccurrenceDetected, CreatePayload());

        // Assert
        result.Success.Should().BeTrue();
        mockChannel.Verify(c => c.SendAsync(AlertType.ZombieOccurrenceDetected, It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_ShouldRouteToSpecificChannels_WhenAlertHasRoutes()
    {
        // Arrange
        var slackMock = CreateMockChannel("Slack");
        var gchatMock = CreateMockChannel("GoogleChat");
        var emailMock = CreateMockChannel("Email");

        var options = CreateOptions(alerts: new Dictionary<AlertType, AlertConfig>
        {
            [AlertType.JobAutoDisabled] = new AlertConfig
            {
                Enabled = true,
                Routes = ["Slack", "GoogleChat"]
            }
        });
        var notifier = CreateNotifier(options, slackMock.Object, gchatMock.Object, emailMock.Object);

        // Act
        var result = await notifier.SendAsync(AlertType.JobAutoDisabled, CreatePayload());

        // Assert
        result.Success.Should().BeTrue();
        slackMock.Verify(c => c.SendAsync(AlertType.JobAutoDisabled, It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()), Times.Once);
        gchatMock.Verify(c => c.SendAsync(AlertType.JobAutoDisabled, It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()), Times.Once);
        emailMock.Verify(c => c.SendAsync(It.IsAny<AlertType>(), It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnSuccess_WhenAtLeastOneChannelSucceeds()
    {
        // Arrange
        var successChannel = CreateMockChannel("Slack", success: true);
        var failChannel = CreateMockChannel("GoogleChat", success: false);

        var options = CreateOptions(alerts: new Dictionary<AlertType, AlertConfig>
        {
            [AlertType.WorkerDisconnected] = new AlertConfig
            {
                Enabled = true,
                Routes = ["Slack", "GoogleChat"]
            }
        });
        var notifier = CreateNotifier(options, successChannel.Object, failChannel.Object);

        // Act
        var result = await notifier.SendAsync(AlertType.WorkerDisconnected, CreatePayload());

        // Assert
        result.Success.Should().BeTrue("at least one channel succeeded");
        result.ChannelResults.Should().HaveCount(2);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailed_WhenAllChannelsFail()
    {
        // Arrange
        var failChannel1 = CreateMockChannel("Slack", success: false);
        var failChannel2 = CreateMockChannel("GoogleChat", success: false);

        var options = CreateOptions(alerts: new Dictionary<AlertType, AlertConfig>
        {
            [AlertType.DatabaseConnectionFailed] = new AlertConfig
            {
                Enabled = true,
                Routes = ["Slack", "GoogleChat"]
            }
        });
        var notifier = CreateNotifier(options, failChannel1.Object, failChannel2.Object);

        // Act
        var result = await notifier.SendAsync(AlertType.DatabaseConnectionFailed, CreatePayload());

        // Assert
        result.Success.Should().BeFalse("all channels failed");
        result.ChannelResults.Should().HaveCount(2);
        result.ChannelResults.Should().OnlyContain(r => !r.Success);
    }

    [Fact]
    public async Task SendAsync_ShouldSkipUnregisteredChannels()
    {
        // Arrange
        var slackMock = CreateMockChannel("Slack");
        var options = CreateOptions(alerts: new Dictionary<AlertType, AlertConfig>
        {
            [AlertType.RedisConnectionFailed] = new AlertConfig
            {
                Enabled = true,
                Routes = ["Slack", "NonExistentChannel"]
            }
        });
        var notifier = CreateNotifier(options, slackMock.Object);

        // Act
        var result = await notifier.SendAsync(AlertType.RedisConnectionFailed, CreatePayload());

        // Assert
        result.Success.Should().BeTrue();
        result.ChannelResults.Should().HaveCount(2);
        result.ChannelResults.Should().Contain(r => r.ChannelName == "NonExistentChannel" && r.Message.Contains("not registered"));
    }

    [Fact]
    public async Task SendAsync_ShouldBuildActionUrl_WhenActionLinkIsRelative()
    {
        // Arrange
        var mockChannel = CreateMockChannel("Slack");
        var options = CreateOptions(defaultChannel: "Slack");
        options.MilvaionAppUrl = "https://milvaion.example.com";
        var notifier = CreateNotifier(options, mockChannel.Object);

        var payload = CreatePayload();
        payload.ActionLink = "/jobs/123";

        // Act
        await notifier.SendAsync(AlertType.JobAutoDisabled, payload);

        // Assert
        mockChannel.Verify(c => c.SendAsync(
            AlertType.JobAutoDisabled,
            It.Is<AlertPayload>(p => p.ActionLink == "https://milvaion.example.com/jobs/123"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_ShouldHandleChannelException_WithoutCrashing()
    {
        // Arrange
        var throwingChannel = new Mock<IAlertChannel>();
        throwingChannel.Setup(c => c.ChannelName).Returns("BrokenChannel");
        throwingChannel.Setup(c => c.SendAsync(It.IsAny<AlertType>(), It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new InvalidOperationException("Channel exploded"));

        var options = CreateOptions(defaultChannel: "BrokenChannel");
        var notifier = CreateNotifier(options, throwingChannel.Object);

        // Act
        var result = await notifier.SendAsync(AlertType.UnknownException, CreatePayload());

        // Assert
        result.Success.Should().BeFalse();
        result.ChannelResults.Should().ContainSingle()
              .Which.Message.Should().Contain("Channel exploded");
    }

    #endregion

    #region SendFireAndForget

    [Fact]
    public void SendFireAndForget_ShouldNotThrow_WhenAlertTypeIsDisabled()
    {
        // Arrange
        var options = CreateOptions(alerts: new Dictionary<AlertType, AlertConfig>
        {
            [AlertType.JobAutoDisabled] = new AlertConfig { Enabled = false }
        });
        var notifier = CreateNotifier(options);

        // Act & Assert
        var act = () => notifier.SendFireAndForget(AlertType.JobAutoDisabled, CreatePayload());
        act.Should().NotThrow();
    }

    [Fact]
    public void SendFireAndForget_ShouldNotThrow_WhenNoRoutesConfigured()
    {
        // Arrange
        var options = CreateOptions(defaultChannel: "");
        var notifier = CreateNotifier(options);

        // Act & Assert
        var act = () => notifier.SendFireAndForget(AlertType.ZombieOccurrenceDetected, CreatePayload());
        act.Should().NotThrow();
    }

    [Fact]
    public async Task SendFireAndForget_ShouldSendToChannel()
    {
        // Arrange
        var mockChannel = CreateMockChannel("Slack");
        var options = CreateOptions(defaultChannel: "Slack");
        var notifier = CreateNotifier(options, mockChannel.Object);

        // Act
        notifier.SendFireAndForget(AlertType.WorkerDisconnected, CreatePayload());

        // Allow fire-and-forget task to complete
        await Task.Delay(500);

        // Assert
        mockChannel.Verify(c => c.SendAsync(AlertType.WorkerDisconnected, It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SendFireAndForget_ShouldNotThrow_WhenChannelThrows()
    {
        // Arrange
        var throwingChannel = new Mock<IAlertChannel>();
        throwingChannel.Setup(c => c.ChannelName).Returns("BrokenChannel");
        throwingChannel.Setup(c => c.SendAsync(It.IsAny<AlertType>(), It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new InvalidOperationException("Boom"));

        var options = CreateOptions(defaultChannel: "BrokenChannel");
        var notifier = CreateNotifier(options, throwingChannel.Object);

        // Act & Assert - Should never throw from fire-and-forget
        var act = () => notifier.SendFireAndForget(AlertType.UnknownException, CreatePayload());
        act.Should().NotThrow();
    }

    #endregion

    #region IsAlertEnabled

    [Fact]
    public void IsAlertEnabled_ShouldReturnTrue_WhenAlertNotConfigured()
    {
        // Arrange
        var options = CreateOptions();
        var notifier = CreateNotifier(options);

        // Act & Assert
        notifier.IsAlertEnabled(AlertType.ZombieOccurrenceDetected).Should().BeTrue("unconfigured alerts default to enabled");
    }

    [Fact]
    public void IsAlertEnabled_ShouldReturnTrue_WhenExplicitlyEnabled()
    {
        // Arrange
        var options = CreateOptions(alerts: new Dictionary<AlertType, AlertConfig>
        {
            [AlertType.JobAutoDisabled] = new AlertConfig { Enabled = true }
        });
        var notifier = CreateNotifier(options);

        // Act & Assert
        notifier.IsAlertEnabled(AlertType.JobAutoDisabled).Should().BeTrue();
    }

    [Fact]
    public void IsAlertEnabled_ShouldReturnFalse_WhenExplicitlyDisabled()
    {
        // Arrange
        var options = CreateOptions(alerts: new Dictionary<AlertType, AlertConfig>
        {
            [AlertType.JobAutoDisabled] = new AlertConfig { Enabled = false }
        });
        var notifier = CreateNotifier(options);

        // Act & Assert
        notifier.IsAlertEnabled(AlertType.JobAutoDisabled).Should().BeFalse();
    }

    [Fact]
    public void IsAlertEnabled_ShouldReturnTrue_WhenAlertsDictionaryIsNull()
    {
        // Arrange
        var options = CreateOptions();
        options.Alerts = null;
        var notifier = CreateNotifier(options);

        // Act & Assert
        notifier.IsAlertEnabled(AlertType.DatabaseConnectionFailed).Should().BeTrue();
    }

    #endregion

    #region GetRoutesForAlert

    [Fact]
    public void GetRoutesForAlert_ShouldReturnDefaultChannel_WhenNoSpecificRoutes()
    {
        // Arrange
        var options = CreateOptions(defaultChannel: "InternalNotification");
        var notifier = CreateNotifier(options);

        // Act
        var routes = notifier.GetRoutesForAlert(AlertType.ZombieOccurrenceDetected);

        // Assert
        routes.Should().ContainSingle().Which.Should().Be("InternalNotification");
    }

    [Fact]
    public void GetRoutesForAlert_ShouldReturnSpecificRoutes_WhenConfigured()
    {
        // Arrange
        var options = CreateOptions(alerts: new Dictionary<AlertType, AlertConfig>
        {
            [AlertType.JobAutoDisabled] = new AlertConfig
            {
                Enabled = true,
                Routes = ["Slack", "Email"]
            }
        });
        var notifier = CreateNotifier(options);

        // Act
        var routes = notifier.GetRoutesForAlert(AlertType.JobAutoDisabled);

        // Assert
        routes.Should().BeEquivalentTo(["Slack", "Email"]);
    }

    [Fact]
    public void GetRoutesForAlert_ShouldReturnEmpty_WhenNoDefaultChannelAndNoRoutes()
    {
        // Arrange
        var options = CreateOptions(defaultChannel: "");
        var notifier = CreateNotifier(options);

        // Act
        var routes = notifier.GetRoutesForAlert(AlertType.QueueDepthCritical);

        // Assert
        routes.Should().BeEmpty();
    }

    #endregion
}
