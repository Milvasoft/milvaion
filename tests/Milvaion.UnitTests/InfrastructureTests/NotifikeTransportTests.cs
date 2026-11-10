using FluentAssertions;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Dtos.NotificationDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Domain;
using Milvaion.Domain.Enums;
using Milvaion.Infrastructure.Services;
using Milvaion.Infrastructure.Services.Alerting.Channels;
using Milvasoft.Core.Abstractions;
using Moq;
using Moq.Protected;
using System.Linq.Expressions;
using System.Net;
using System.Text.Json;

namespace Milvaion.UnitTests.InfrastructureTests;

[Trait("Infrastructure Unit Tests", "NotifikeTransport unit tests.")]
public class NotifikeTransportTests
{
    private const string _apiKey = "nfk_test_secret_key";
    private const string _acceptedBody = """{"isSuccess":true,"data":[]}""";

    private sealed class CapturedRequests
    {
        public List<(HttpRequestMessage Request, string Body)> Items { get; } = [];
        public HttpRequestMessage Request => Items[^1].Request;
        public JsonElement Json => JsonDocument.Parse(Items[^1].Body).RootElement;
    }

    private static NotifikeOptions CreateOptions(bool enabled = true) => new()
    {
        Enabled = enabled,
        SendOnlyInProduction = false,
        BaseUrl = "https://notifike.example.com/",
        ApiKey = _apiKey
    };

    private static (NotifikeTransport Transport, Mock<HttpMessageHandler> Handler, CapturedRequests Captured, Mock<IMilvaLogger> Logger) CreateTransport(NotifikeOptions options,
                                                                                                                                                         HttpStatusCode responseStatus = HttpStatusCode.OK,
                                                                                                                                                         string responseBody = _acceptedBody)
    {
        var captured = new CapturedRequests();

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (request, cancellationToken) =>
            {
                captured.Items.Add((request, await request.Content.ReadAsStringAsync(cancellationToken)));

                return new HttpResponseMessage
                {
                    StatusCode = responseStatus,
                    Content = new StringContent(responseBody)
                };
            });

        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock.Setup(f => f.CreateClient(nameof(NotifikeTransport))).Returns(new HttpClient(handlerMock.Object));

        var loggerMock = new Mock<IMilvaLogger>();
        var transport = new NotifikeTransport(Options.Create(options), httpClientFactoryMock.Object, new Lazy<IMilvaLogger>(() => loggerMock.Object));

        return (transport, handlerMock, captured, loggerMock);
    }

    private static NotifikeEmail CreateEmail() => new()
    {
        Subject = "Disk full",
        HtmlBody = "<p>Disk full</p>",
        Recipients = ["ops@example.com"],
        Tag = "JobExecutionFailed"
    };

    private static void VerifyNoRequest(Mock<HttpMessageHandler> handler)
        => handler.Protected().Verify("SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());

    #region Activation

    [Fact]
    public void Options_ShouldBeDisabledByDefault() => new NotifikeOptions().Enabled.Should().BeFalse();

    [Fact]
    public void CanSend_ShouldBeTrue_WhenFullyConfigured()
    {
        var (transport, _, _, _) = CreateTransport(CreateOptions());

        transport.CanSendEmail.Should().BeTrue();
        transport.CanSendInApp.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, _apiKey, "https://notifike.example.com", false)]
    [InlineData(true, null, "https://notifike.example.com", false)]
    [InlineData(true, "  ", "https://notifike.example.com", false)]
    [InlineData(true, _apiKey, null, false)]
    [InlineData(true, _apiKey, "not a url", false)]
    [InlineData(true, _apiKey, "ftp://notifike.example.com", false)]
    [InlineData(true, _apiKey, "https://notifike.example.com", true)]
    public void CanSend_ShouldBeFalse_WhenDisabledOrMisconfiguredOrNotProduction(bool enabled, string apiKey, string baseUrl, bool sendOnlyInProduction)
    {
        var (transport, _, _, _) = CreateTransport(new NotifikeOptions
        {
            Enabled = enabled,
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            SendOnlyInProduction = sendOnlyInProduction
        });

        transport.CanSendEmail.Should().BeFalse();
        transport.CanSendInApp.Should().BeFalse();
    }

    [Fact]
    public void CanSend_ShouldFollowPerKindSwitches()
    {
        var options = CreateOptions();
        options.UseForEmail = false;
        var (transport, _, _, _) = CreateTransport(options);

        transport.CanSendEmail.Should().BeFalse();
        transport.CanSendInApp.Should().BeTrue();
    }

    [Fact]
    public async Task SendEmailAsync_ShouldNotCallNotifike_WhenDisabled()
    {
        var (transport, handler, _, _) = CreateTransport(CreateOptions(enabled: false));

        var result = await transport.SendEmailAsync(CreateEmail(), CancellationToken.None);

        result.Success.Should().BeFalse();
        VerifyNoRequest(handler);
    }

    #endregion

    #region E-mail

    [Fact]
    public async Task SendEmailAsync_ShouldPostContractRequest()
    {
        var (transport, _, captured, _) = CreateTransport(CreateOptions());

        var result = await transport.SendEmailAsync(CreateEmail(), CancellationToken.None);

        result.Success.Should().BeTrue();
        captured.Request.Method.Should().Be(HttpMethod.Post);
        captured.Request.RequestUri!.ToString().Should().Be("https://notifike.example.com/api/v1.0/notifications/send");
        captured.Request.Headers.GetValues("X-Api-Key").Should().ContainSingle().Which.Should().Be(_apiKey);

        var json = captured.Json;
        json.GetProperty("channel").GetInt32().Should().Be(1);
        json.GetProperty("priority").GetInt32().Should().Be(5);
        json.GetProperty("recipients")[0].GetProperty("to").GetString().Should().Be("ops@example.com");
        json.GetProperty("subject").GetString().Should().Be("Disk full");
        json.GetProperty("body").GetString().Should().Be("<p>Disk full</p>");
    }

    [Fact]
    public async Task SendEmailAsync_ShouldUseHighPriority_WhenRequested()
    {
        var (transport, _, captured, _) = CreateTransport(CreateOptions());
        var email = CreateEmail();
        email.IsHighPriority = true;

        await transport.SendEmailAsync(email, CancellationToken.None);

        captured.Json.GetProperty("priority").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task SendEmailAsync_ShouldFitSubjectLimit()
    {
        var (transport, _, captured, _) = CreateTransport(CreateOptions());
        var email = CreateEmail();
        email.Subject = new string('s', 2000);

        await transport.SendEmailAsync(email, CancellationToken.None);

        captured.Json.GetProperty("subject").GetString().Length.Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public async Task SendEmailAsync_ShouldSplitRecipientsIntoRequestsOfAtMostAThousand()
    {
        var (transport, _, captured, _) = CreateTransport(CreateOptions());
        var email = CreateEmail();
        email.Recipients = [.. Enumerable.Range(0, 1200).Select(i => $"user{i}@example.com"), "user0@example.com", " "];

        await transport.SendEmailAsync(email, CancellationToken.None);

        captured.Items.Should().HaveCount(2);
        captured.Items.Select(i => JsonDocument.Parse(i.Body).RootElement.GetProperty("recipients").GetArrayLength()).Should().Equal(1000, 200);
    }

    [Fact]
    public async Task SendEmailAsync_ShouldFailWithoutApiKey_WhenHttpFails()
    {
        var (transport, _, _, logger) = CreateTransport(CreateOptions(), HttpStatusCode.Unauthorized, """{"isSuccess":false,"messages":[{"key":"InvalidApiKey","message":"Key refused"}]}""");

        var result = await transport.SendEmailAsync(CreateEmail(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Unauthorized").And.Contain("Key refused").And.NotContain(_apiKey);
        logger.Invocations.SelectMany(i => i.Arguments).Should().NotContain(a => a != null && a.ToString().Contains(_apiKey));
    }

    [Fact]
    public async Task SendEmailAsync_ShouldFail_WhenResponseIsNotAnEnvelope()
    {
        var (transport, _, _, _) = CreateTransport(CreateOptions(), HttpStatusCode.BadGateway, "<html>Bad gateway</html>");

        var result = await transport.SendEmailAsync(CreateEmail(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("BadGateway").And.NotContain("<html>");
    }

    [Fact]
    public async Task SendEmailAsync_ShouldFail_WhenEnvelopeReportsFailureOnHttp200()
    {
        var (transport, _, _, _) = CreateTransport(CreateOptions(), HttpStatusCode.OK, """{"isSuccess":false,"messages":[{"key":"DailyLimitExceeded","message":"Daily limit exceeded"}]}""");

        var result = await transport.SendEmailAsync(CreateEmail(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Daily limit exceeded");
    }

    [Fact]
    public async Task SendEmailAsync_ShouldNotThrow_WhenRequestThrows()
    {
        var (transport, handler, _, _) = CreateTransport(CreateOptions());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var result = await transport.SendEmailAsync(CreateEmail(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Connection refused");
    }

    #endregion

    #region In-app

    private static NotifikeInAppNotification CreateInApp() => new()
    {
        Type = "JobExecutionFailed",
        Text = "Job failed",
        Link = "https://milvaion.example.com/jobs/1",
        EntityType = "Job",
        EntityId = "1",
        DataJson = """{"severity":"Error"}""",
        UserKeys = ["alice", "bob"]
    };

    [Fact]
    public async Task SendInAppAsync_ShouldPostContractRequest()
    {
        var (transport, _, captured, _) = CreateTransport(CreateOptions(), responseBody: """{"isSuccess":true,"data":{"createdCount":2,"skippedCount":0}}""");

        var result = await transport.SendInAppAsync(CreateInApp(), CancellationToken.None);

        result.Success.Should().BeTrue();
        captured.Request.RequestUri!.ToString().Should().Be("https://notifike.example.com/api/v1.0/inApp/send");
        captured.Request.Headers.GetValues("X-Api-Key").Should().ContainSingle().Which.Should().Be(_apiKey);

        var json = captured.Json;
        json.GetProperty("type").GetString().Should().Be("JobExecutionFailed");
        json.GetProperty("body").GetString().Should().Be("Job failed");
        json.GetProperty("link").GetString().Should().Be("https://milvaion.example.com/jobs/1");
        json.GetProperty("entityType").GetString().Should().Be("Job");
        json.GetProperty("entityId").GetString().Should().Be("1");
        json.GetProperty("data").GetProperty("severity").GetString().Should().Be("Error");
        json.GetProperty("recipients").EnumerateArray().Select(r => r.GetProperty("userKey").GetString()).Should().Equal("alice", "bob");
    }

    [Fact]
    public async Task SendInAppAsync_ShouldFitContractLimits()
    {
        var (transport, _, captured, _) = CreateTransport(CreateOptions());
        var notification = CreateInApp();
        notification.Text = new string('b', 9000);
        notification.Link = "https://milvaion.example.com/" + new string('l', 2000);
        notification.DataJson = "{\"v\":\"" + new string('d', 20_000) + "\"}";
        notification.UserKeys = ["alice", new string('u', 101)];

        await transport.SendInAppAsync(notification, CancellationToken.None);

        var json = captured.Json;
        json.GetProperty("body").GetString().Length.Should().BeLessThanOrEqualTo(4000);
        json.TryGetProperty("link", out _).Should().BeFalse();
        json.TryGetProperty("data", out _).Should().BeFalse();
        json.GetProperty("recipients").GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{broken")]
    public async Task SendInAppAsync_ShouldOmitData_WhenItIsNotAJsonObject(string dataJson)
    {
        var (transport, _, captured, _) = CreateTransport(CreateOptions());
        var notification = CreateInApp();
        notification.DataJson = dataJson;

        await transport.SendInAppAsync(notification, CancellationToken.None);

        captured.Json.TryGetProperty("data", out _).Should().BeFalse();
    }

    #endregion

    #region EmailAlertChannel

    private static AlertPayload CreatePayload() => new()
    {
        Title = "Disk full",
        Message = "No space left",
        Severity = AlertSeverity.Critical,
        Source = "UnitTest",
        Timestamp = DateTime.UtcNow
    };

    private static EmailAlertChannel CreateEmailChannel(INotifikeTransport transport, string smtpHost = null) => new(
        Options.Create(new AlertingOptions
        {
            SendOnlyInProduction = false,
            Channels = new AlertChannelsOptions
            {
                Email = new EmailChannelOptions
                {
                    Enabled = true,
                    SendOnlyInProduction = false,
                    SmtpHost = smtpHost,
                    DefaultRecipients = ["admin@example.com"]
                }
            }
        }),
        new Lazy<IMilvaLogger>(() => new Mock<IMilvaLogger>().Object),
        transport);

    [Fact]
    public async Task EmailChannel_ShouldSendThroughNotifikeWithoutSmtpSettings_WhenActive()
    {
        var (transport, _, captured, _) = CreateTransport(CreateOptions());

        var result = await CreateEmailChannel(transport).SendAsync(AlertType.JobExecutionFailed, CreatePayload());

        result.Success.Should().BeTrue();
        result.Message.Should().BeNullOrEmpty();
        var json = captured.Json;
        json.GetProperty("recipients")[0].GetProperty("to").GetString().Should().Be("admin@example.com");
        json.GetProperty("subject").GetString().Should().Contain("Disk full");
        json.GetProperty("body").GetString().Should().Contain("No space left");
        json.GetProperty("priority").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task EmailChannel_ShouldReportFailure_WhenNotifikeRefuses()
    {
        var (transport, _, _, _) = CreateTransport(CreateOptions(), HttpStatusCode.InternalServerError, "{}");

        var result = await CreateEmailChannel(transport).SendAsync(AlertType.JobExecutionFailed, CreatePayload());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("InternalServerError");
    }

    [Fact]
    public async Task EmailChannel_ShouldKeepLocalRules_WhenNotifikeIsOff()
    {
        var (transport, handler, _, _) = CreateTransport(CreateOptions(enabled: false));
        var channel = CreateEmailChannel(transport);

        channel.CanSend().Should().BeFalse("SMTP is not configured and Notifike is off");

        var result = await channel.SendAsync(AlertType.JobExecutionFailed, CreatePayload());

        result.Message.Should().Contain("disabled");
        VerifyNoRequest(handler);
    }

    #endregion

    #region NotificationService

    private static (NotificationService Service, Mock<IMilvaionRepositoryBase<InternalNotification>> Repository) CreateNotificationService(INotifikeTransport transport)
    {
        var userRepository = new Mock<IMilvaionRepositoryBase<User>>();
        userRepository.Setup(r => r.GetAllAsync(It.IsAny<Expression<Func<User, bool>>>(),
                                                It.IsAny<Expression<Func<User, User>>>(),
                                                It.IsAny<Expression<Func<User, bool>>>(),
                                                It.IsAny<bool>(),
                                                It.IsAny<bool>(),
                                                It.IsAny<CancellationToken>()))
                      .ReturnsAsync([new User { Id = 1, UserName = "alice" }]);

        var notificationRepository = new Mock<IMilvaionRepositoryBase<InternalNotification>>();

        var service = new NotificationService(notificationRepository.Object,
                                              userRepository.Object,
                                              new Lazy<IMilvaLogger>(() => new Mock<IMilvaLogger>().Object),
                                              transport);

        return (service, notificationRepository);
    }

    private static InternalNotificationRequest CreateNotificationRequest() => new()
    {
        Type = AlertType.JobExecutionFailed,
        Text = "Job failed",
        ActionLink = "/jobs/1",
        Data = new { Severity = "Error" },
        Recipients = ["alice"],
        FindRecipientsFromType = false
    };

    private static void VerifyStoredLocally(Mock<IMilvaionRepositoryBase<InternalNotification>> repository)
        => repository.Invocations.Should().ContainSingle(i => i.Method.Name == "BulkAddAsync");

    [Fact]
    public async Task NotificationService_ShouldOnlyStoreLocally_WhenNotifikeIsOff()
    {
        var (transport, handler, _, _) = CreateTransport(CreateOptions(enabled: false));
        var (service, repository) = CreateNotificationService(transport);

        await service.PublishAsync(CreateNotificationRequest(), null, CancellationToken.None);

        VerifyStoredLocally(repository);
        VerifyNoRequest(handler);
    }

    [Fact]
    public async Task NotificationService_ShouldStoreLocallyAndCopyToNotifike_WhenActive()
    {
        var (transport, _, captured, _) = CreateTransport(CreateOptions());
        var (service, repository) = CreateNotificationService(transport);

        await service.PublishAsync(CreateNotificationRequest(), null, CancellationToken.None);

        VerifyStoredLocally(repository);
        var json = captured.Json;
        json.GetProperty("type").GetString().Should().Be("JobExecutionFailed");
        json.GetProperty("body").GetString().Should().Be("Job failed");
        json.GetProperty("data").GetProperty("severity").GetString().Should().Be("Error");
        json.GetProperty("recipients")[0].GetProperty("userKey").GetString().Should().Be("alice");
        json.TryGetProperty("entityType", out _).Should().BeFalse();
    }

    [Fact]
    public async Task NotificationService_ShouldKeepLocalNotification_WhenNotifikeFails()
    {
        var (transport, _, _, _) = CreateTransport(CreateOptions(), HttpStatusCode.ServiceUnavailable, "{}");
        var (service, repository) = CreateNotificationService(transport);

        var act = () => service.PublishAsync(CreateNotificationRequest(), null, CancellationToken.None);

        await act.Should().NotThrowAsync();
        VerifyStoredLocally(repository);
    }

    #endregion
}
