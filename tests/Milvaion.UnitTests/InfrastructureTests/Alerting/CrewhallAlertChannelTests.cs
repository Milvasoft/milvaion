using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Features.Configuration.GetSystemConfiguration;
using Milvaion.Application.Features.Settings.GetSettings;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Domain.Enums;
using Milvaion.Domain.JsonModels;
using Milvaion.Infrastructure.Services.Alerting;
using Milvaion.Infrastructure.Services.Alerting.Channels;
using Milvasoft.Core.Abstractions;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Milvaion.UnitTests.InfrastructureTests.Alerting;

[Trait("Infrastructure Unit Tests", "CrewhallAlertChannel unit tests.")]
public class CrewhallAlertChannelTests
{
    private const string _webhookKey = "secret-webhook-key";
    private const string _webhookUrl = "https://crewhall.example.com/api/hooks/0195f0a0-0000-7000-8000-000000000001?key=" + _webhookKey;

    private sealed class CapturedRequest
    {
        public HttpRequestMessage Request { get; set; }
        public string Body { get; set; }
        public JsonElement Card => JsonDocument.Parse(Body).RootElement.GetProperty("card");
    }

    private static AlertPayload CreatePayload(AlertSeverity severity = AlertSeverity.Error) => new()
    {
        Title = "Test Crewhall Alert",
        Message = "Something critical happened",
        Severity = severity,
        Source = "UnitTest",
        Timestamp = DateTime.UtcNow,
        AdditionalData = new { JobId = "abc-123" }
    };

    private static AlertingOptions CreateOptions(bool enabled = true, bool? sendOnlyInProduction = false, string webhookUrl = _webhookUrl, string defaultChat = "alerts") => new()
    {
        SendOnlyInProduction = false,
        Channels = new AlertChannelsOptions
        {
            Crewhall = new CrewhallChannelOptions
            {
                Enabled = enabled,
                SendOnlyInProduction = sendOnlyInProduction,
                DefaultChat = defaultChat,
                Chats = [new CrewhallChatConfig { Chat = "alerts", WebhookUrl = webhookUrl }]
            }
        }
    };

    private static (CrewhallAlertChannel Channel, Mock<HttpMessageHandler> Handler, CapturedRequest Captured, Mock<IMilvaLogger> Logger) CreateChannel(AlertingOptions options,
                                                                                                                                                       HttpStatusCode responseStatus = HttpStatusCode.OK,
                                                                                                                                                       string responseBody = """{"messageId":"m1","seq":1}""")
    {
        var captured = new CapturedRequest();

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (request, cancellationToken) =>
            {
                captured.Request = request;
                captured.Body = await request.Content.ReadAsStringAsync(cancellationToken);

                return new HttpResponseMessage
                {
                    StatusCode = responseStatus,
                    Content = new StringContent(responseBody)
                };
            });

        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock.Setup(f => f.CreateClient(nameof(CrewhallAlertChannel))).Returns(new HttpClient(handlerMock.Object));

        var loggerMock = new Mock<IMilvaLogger>();
        var channel = new CrewhallAlertChannel(Options.Create(options), httpClientFactoryMock.Object, new Lazy<IMilvaLogger>(() => loggerMock.Object));

        return (channel, handlerMock, captured, loggerMock);
    }

    private static void VerifyNoRequest(Mock<HttpMessageHandler> handler)
        => handler.Protected().Verify("SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());

    [Fact]
    public void ChannelName_ShouldBeCrewhall()
    {
        var (channel, _, _, _) = CreateChannel(CreateOptions());
        channel.ChannelName.Should().Be("Crewhall");
    }

    [Fact]
    public void Options_ShouldBeDisabledByDefault() => new CrewhallChannelOptions().Enabled.Should().BeFalse();

    [Fact]
    public async Task SendAsync_ShouldReturnSkipped_WhenDisabled()
    {
        var (channel, handler, _, _) = CreateChannel(CreateOptions(enabled: false));

        var result = await channel.SendAsync(AlertType.JobAutoDisabled, CreatePayload());

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("disabled");
        VerifyNoRequest(handler);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnSkipped_WhenSendOnlyInProductionAndNotProduction()
    {
        var (channel, handler, _, _) = CreateChannel(CreateOptions(sendOnlyInProduction: true));

        var result = await channel.SendAsync(AlertType.JobAutoDisabled, CreatePayload());

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("environment check");
        VerifyNoRequest(handler);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("YOUR_CREWHALL_WEBHOOK_URL_HERE")]
    [InlineData("ftp://crewhall.example.com/api/hooks/1?key=k")]
    public async Task SendAsync_ShouldReturnSkipped_WhenWebhookUrlIsMissingOrInvalid(string webhookUrl)
    {
        var (channel, handler, _, _) = CreateChannel(CreateOptions(webhookUrl: webhookUrl));

        var result = await channel.SendAsync(AlertType.JobAutoDisabled, CreatePayload());

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("No valid webhook");
        VerifyNoRequest(handler);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnSkipped_WhenDefaultChatIsNotConfigured()
    {
        var (channel, handler, _, _) = CreateChannel(CreateOptions(defaultChat: "other"));

        var result = await channel.SendAsync(AlertType.JobAutoDisabled, CreatePayload());

        result.Message.Should().Contain("No valid webhook");
        VerifyNoRequest(handler);
    }

    [Fact]
    public async Task SendAsync_ShouldPostContractCard_WhenWebhookAccepts()
    {
        var (channel, _, captured, _) = CreateChannel(CreateOptions());

        var result = await channel.SendAsync(AlertType.ZombieOccurrenceDetected, CreatePayload());

        result.Success.Should().BeTrue();
        captured.Request.Method.Should().Be(HttpMethod.Post);
        captured.Request.RequestUri!.ToString().Should().Be(_webhookUrl);

        var card = captured.Card;
        card.GetProperty("severity").GetString().Should().Be("critical");
        card.GetProperty("title").GetString().Should().Be("Test Crewhall Alert");
        card.GetProperty("subtitle").GetString().Should().Be(nameof(AlertType.ZombieOccurrenceDetected));
        card.GetProperty("source").GetString().Should().Be("UnitTest");
        card.GetProperty("text").GetString().Should().Be("Something critical happened");
        card.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("label").GetString()).Should().Equal("Severity", "Time", "Additional Data");
        card.GetProperty("buttons").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData(AlertSeverity.Critical, "critical")]
    [InlineData(AlertSeverity.Error, "critical")]
    [InlineData(AlertSeverity.Warning, "warning")]
    [InlineData(AlertSeverity.Info, "info")]
    public async Task SendAsync_ShouldMapSeverity(AlertSeverity severity, string expected)
    {
        var (channel, _, captured, _) = CreateChannel(CreateOptions());

        await channel.SendAsync(AlertType.JobExecutionFailed, CreatePayload(severity));

        captured.Card.GetProperty("severity").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task SendAsync_ShouldFitContractLimits_WhenAlertTextIsLong()
    {
        var (channel, _, captured, _) = CreateChannel(CreateOptions());
        var payload = CreatePayload();
        payload.Title = new string('ş', 2000);
        payload.Source = new string('ğ', 500);
        payload.Message = new string('ı', 9000);
        payload.AdditionalData = new { Value = new string('x', 5000) };

        await channel.SendAsync(AlertType.JobExecutionFailed, payload);

        var card = captured.Card;
        card.GetProperty("title").GetString().Length.Should().BeLessThanOrEqualTo(300);
        card.GetProperty("source").GetString().Length.Should().BeLessThanOrEqualTo(100);
        card.GetProperty("text").GetString().Length.Should().BeLessThanOrEqualTo(4000);
        card.GetProperty("fields").EnumerateArray().Should().OnlyContain(f => f.GetProperty("value").GetString().Length <= 1000);
        Encoding.UTF8.GetByteCount(captured.Body).Should().BeLessThanOrEqualTo(32 * 1024);
    }

    [Fact]
    public async Task SendAsync_ShouldAddButton_WhenActionLinkIsAbsolute()
    {
        var (channel, _, captured, _) = CreateChannel(CreateOptions());
        var payload = CreatePayload();
        payload.ActionLink = "https://milvaion.example.com/jobs/1";

        await channel.SendAsync(AlertType.JobExecutionFailed, payload);

        var button = captured.Card.GetProperty("buttons")[0];
        button.GetProperty("url").GetString().Should().Be("https://milvaion.example.com/jobs/1");
        button.GetProperty("text").GetString().Should().Be("View Details");
    }

    [Fact]
    public async Task SendAsync_ShouldOmitButton_WhenActionLinkIsRelative()
    {
        var (channel, _, captured, _) = CreateChannel(CreateOptions());
        var payload = CreatePayload();
        payload.ActionLink = "/jobs/1";

        await channel.SendAsync(AlertType.JobExecutionFailed, payload);

        captured.Card.GetProperty("buttons").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailedWithoutWebhookUrl_WhenHttpFails()
    {
        var (channel, _, _, logger) = CreateChannel(CreateOptions(), HttpStatusCode.TooManyRequests, """{"code":"rate_limited","message":"Too many requests"}""");

        var result = await channel.SendAsync(AlertType.DatabaseConnectionFailed, CreatePayload());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("TooManyRequests").And.Contain("rate_limited").And.NotContain(_webhookKey);
        logger.Invocations.SelectMany(i => i.Arguments).Should().NotContain(a => a != null && a.ToString().Contains(_webhookKey));
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailed_WhenResponseIsNotJson()
    {
        var (channel, _, _, _) = CreateChannel(CreateOptions(), HttpStatusCode.BadGateway, "<html>Bad gateway</html>");

        var result = await channel.SendAsync(AlertType.DatabaseConnectionFailed, CreatePayload());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("BadGateway").And.NotContain("<html>");
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailed_WhenRequestThrows()
    {
        var (channel, handler, _, _) = CreateChannel(CreateOptions());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var result = await channel.SendAsync(AlertType.DatabaseConnectionFailed, CreatePayload());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task AlertNotifier_ShouldStillDeliverOtherChannels_WhenCrewhallFails()
    {
        var alerting = CreateOptions();
        alerting.Alerts = new Dictionary<AlertType, AlertConfig>
        {
            [AlertType.JobExecutionFailed] = new AlertConfig { Enabled = true, Routes = ["Crewhall", "Slack"] }
        };

        var (crewhall, _, _, _) = CreateChannel(CreateOptions(), HttpStatusCode.InternalServerError, "{}");

        var slack = new Mock<IAlertChannel>();
        slack.Setup(c => c.ChannelName).Returns("Slack");
        slack.Setup(c => c.IsEnabled).Returns(true);
        slack.Setup(c => c.CanSend()).Returns(true);
        slack.Setup(c => c.SendAsync(It.IsAny<AlertType>(), It.IsAny<AlertPayload>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(ChannelResult.Successful("Slack"));

        var settingsProvider = new Mock<ISettingsProvider>();
        settingsProvider.Setup(s => s.Current).Returns(new AppSettingsDocument());

        var notifier = new AlertNotifier(Options.Create(alerting), [crewhall, slack.Object], new Lazy<IMilvaLogger>(() => new Mock<IMilvaLogger>().Object), settingsProvider.Object);

        var result = await notifier.SendAsync(AlertType.JobExecutionFailed, CreatePayload());

        result.ChannelResults.Should().Contain(r => r.ChannelName == "Crewhall" && !r.Success);
        result.ChannelResults.Should().Contain(r => r.ChannelName == "Slack" && r.Success);
    }

    [Fact]
    public async Task GetSettings_ShouldListCrewhallWithoutWebhookUrl()
    {
        var settingsProvider = new Mock<ISettingsProvider>();
        settingsProvider.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AppSettingsDocument());
        var handler = new GetSettingsQueryHandler(settingsProvider.Object, Options.Create(CreateOptions()));

        var response = await handler.Handle(new GetSettingsQuery(), CancellationToken.None);

        response.Data.AvailableChannels.Should().Contain("Crewhall");
        var status = response.Data.Channels.Should().ContainSingle(c => c.Name == "Crewhall").Subject;
        status.Enabled.Should().BeTrue();
        status.SendOnlyInProduction.Should().BeFalse();
        status.DefaultTarget.Should().Be("alerts");
        JsonSerializer.Serialize(response.Data.Channels).Should().NotContain(_webhookKey);
    }

    [Fact]
    public async Task GetSystemConfiguration_ShouldListCrewhallWithoutWebhookUrl()
    {
        var config = new MilvaionConfig
        {
            Alerting = CreateOptions(),
            JobDispatcher = new(),
            Redis = new(),
            RabbitMQ = new(),
            JobAutoDisable = new(),
            ApiKey = new()
        };
        var handler = new GetSystemConfigurationQueryHandler(new ConfigurationBuilder().Build(), config);

        var response = await handler.Handle(new GetSystemConfigurationQuery(), CancellationToken.None);

        response.Data.Alerting.Channels.Should().ContainSingle(c => c.Name == "Crewhall");
        JsonSerializer.Serialize(response.Data.Alerting).Should().NotContain(_webhookKey).And.NotContain("crewhall.example.com");
    }
}
