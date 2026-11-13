using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Features.ScheduledJobs.TriggerScheduledJob;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Services.Secrets;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

/// <summary>
/// End to end: a job whose data references a secret is triggered through the API. The message a worker
/// receives carries the value; the database and the API responses carry only the reference.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Secret references in job data.")]
public class SecretReferenceTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private static string Ref(string name) => "{{secret:" + name + "}}";

    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/jobs";

    [Fact]
    public async Task TriggerJob_WithSecretReference_ShouldPublishTheValueButNeverStoreOrReturnIt()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();

        var secretName = $"it-{Guid.NewGuid():N}";
        var secretValue = $"value-{Guid.NewGuid():N}";
        var variable = EnvironmentSecretProvider.ToVariableName("MILVAION_SECRET_", secretName);
        var template = $$"""{"user":"bob","password":"{{Ref(secretName)}}","auth":"Bearer {{Ref(secretName)}}"}""";

        Environment.SetEnvironmentVariable(variable, secretValue);

        try
        {
            var job = await SeedJobAsync(template);

            await using var rabbit = await RabbitQueue.BindAsync(_factory, job);

            var client = await _factory.CreateClient().LoginAsync();

            // Act
            var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/job/trigger", new TriggerScheduledJobCommand { JobId = job.Id, Reason = "secret test" });
            var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

            // Assert - the worker's message has the value
            result.IsSuccess.Should().BeTrue(string.Join(", ", result.Messages.Select(m => m.Message)));

            var message = await rabbit.GetAsync();
            message.Should().NotBeNull("the triggered job must be published");

            using (var body = JsonDocument.Parse(message))
            {
                var jobData = body.RootElement.GetProperty("jobData").GetString();

                using var data = JsonDocument.Parse(jobData);
                data.RootElement.GetProperty("password").GetString().Should().Be(secretValue);
                data.RootElement.GetProperty("auth").GetString().Should().Be($"Bearer {secretValue}");
                jobData.Should().NotContain("{{secret:");
            }

            // Assert - the database has only the reference
            var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

            var storedJob = await dbContext.ScheduledJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
            storedJob.JobData.Should().Contain(Ref(secretName)).And.NotContain(secretValue);

            var occurrences = await dbContext.JobOccurrences.AsNoTracking().Include(o => o.Logs).Where(o => o.JobId == job.Id).ToListAsync();
            occurrences.Should().ContainSingle();
            JsonSerializer.Serialize(occurrences.Select(o => new { o.Exception, o.Result, Logs = o.Logs.Select(l => new { l.Message, l.Data }) }))
                          .Should().NotContain(secretValue);

            // Assert - the API returns only the reference
            var jobResponse = await client.GetStringAsync($"{_baseUrl}/job?JobId={job.Id}");
            jobResponse.Should().Contain($"secret:{secretName}").And.NotContain(secretValue);

            var occurrenceResponse = await client.GetStringAsync($"{_baseUrl}/occurrences/occurrence?OccurrenceId={occurrences[0].Id}");
            occurrenceResponse.Should().NotContain(secretValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task TriggerJob_WithMissingSecret_ShouldFailTheOccurrenceAndPublishNothing()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();

        var secretName = $"missing-{Guid.NewGuid():N}";
        var job = await SeedJobAsync($$"""{"password":"{{Ref(secretName)}}"}""");

        await using var rabbit = await RabbitQueue.BindAsync(_factory, job);

        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/job/trigger", new TriggerScheduledJobCommand { JobId = job.Id });
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Messages.Should().Contain(m => m.Message.Contains(secretName) && m.Message.Contains("not published"));

        (await rabbit.GetAsync()).Should().BeNull("nothing may be published when a secret is missing");

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var occurrence = await dbContext.JobOccurrences.AsNoTracking().Include(o => o.Logs).SingleAsync(o => o.JobId == job.Id);

        occurrence.Status.Should().Be(JobOccurrenceStatus.Failed);
        occurrence.Exception.Should().Contain(secretName);
        occurrence.Logs.Should().Contain(l => l.Level == "Error" && l.Message.Contains(secretName));
    }

    private async Task<ScheduledJob> SeedJobAsync(string jobData)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var workerId = $"secretit{Guid.NewGuid():N}";

        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = "Secret reference job",
            JobNameInWorker = "SecretJob",
            JobData = jobData,
            ExecuteAt = DateTime.UtcNow.AddHours(1),
            IsActive = true,
            WorkerId = workerId,
            RoutingPattern = $"{workerId}.*",
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername
        };

        await dbContext.ScheduledJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        return job;
    }
}

/// <summary>
/// A temporary queue bound to one job's routing key, standing in for the worker.
/// </summary>
internal sealed class RabbitQueue : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly string _queue;

    private RabbitQueue(IConnection connection, IChannel channel, string queue)
    {
        _connection = connection;
        _channel = channel;
        _queue = queue;
    }

    public static async Task<RabbitQueue> BindAsync(CustomWebApplicationFactory factory, ScheduledJob job)
    {
        var connection = await new ConnectionFactory
        {
            HostName = factory.GetRabbitMqHost(),
            Port = factory.GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        }.CreateConnectionAsync();

        var channel = await connection.CreateChannelAsync();

        await channel.ExchangeDeclareAsync(WorkerConstant.ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false);

        var queue = $"secret-test-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: true);

        // Same mapping as the publisher: "worker.*" is published as "worker.job".
        var routingKey = job.RoutingPattern.Replace("*", "job").Replace("..", ".");
        await channel.QueueBindAsync(queue, WorkerConstant.ExchangeName, routingKey);

        return new RabbitQueue(connection, channel, queue);
    }

    /// <summary>
    /// The next message body, or null when none arrives within a few seconds.
    /// </summary>
    public async Task<string> GetAsync(TimeSpan? wait = null)
    {
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(3));

        do
        {
            var result = await _channel.BasicGetAsync(_queue, autoAck: true);

            if (result != null)
                return Encoding.UTF8.GetString(result.Body.Span);

            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _channel.QueueDeleteAsync(_queue);
        }
        catch
        {
            // Best effort; the queue is auto-delete anyway.
        }

        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
