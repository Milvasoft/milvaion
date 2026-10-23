using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using System.Net;
using System.Net.Sockets;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "Worker startup failure unit tests.")]
public class WorkerStartupFailureTests
{
    [Fact]
    public async Task ConnectionStartupValidator_ShouldThrowAndArmForcedExit_WhenRabbitMQIsUnreachable()
    {
        // Arrange
        var options = Microsoft.Extensions.Options.Options.Create(new WorkerOptions
        {
            WorkerId = "test-worker",
            StartupConnectionTimeoutSeconds = 2,
            RabbitMQ = new RabbitMQSettings
            {
                Host = "127.0.0.1",
                Port = GetUnusedTcpPort()
            }
        });

        TimeSpan? armedGracePeriod = null;

        var validator = new ConnectionStartupValidator(options, NullLoggerFactory.Instance)
        {
            ArmForcedExit = grace => armedGracePeriod = grace
        };

        var previousExitCode = Environment.ExitCode;

        try
        {
            // Act
            var act = () => validator.StartAsync(CancellationToken.None);

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Failed to connect to RabbitMQ*");
            armedGracePeriod.Should().Be(ConnectionStartupValidator.ForcedExitGracePeriod, "a failed startup must not be able to leave the process running");
            Environment.ExitCode.Should().Be(1);
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }

    [Fact]
    public async Task ConnectionStartupValidator_ShouldNotArmForcedExit_WhenValidationIsDisabled()
    {
        // Arrange
        var options = Microsoft.Extensions.Options.Options.Create(new WorkerOptions { StartupConnectionTimeoutSeconds = 0 });
        var armed = false;

        var validator = new ConnectionStartupValidator(options, NullLoggerFactory.Instance)
        {
            ArmForcedExit = _ => armed = true
        };

        // Act
        await validator.StartAsync(CancellationToken.None);

        // Assert
        armed.Should().BeFalse();
    }

    [Fact]
    public async Task RunMilvaionWorkerAsync_ShouldReturnNonZeroExitCode_WhenHostFailsToStart()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddHostedService<FailingStartupService>();

        using var host = builder.Build();

        // Act
        var exitCode = await host.RunMilvaionWorkerAsync();

        // Assert
        exitCode.Should().Be(1, "a startup failure must surface as a non-zero exit code instead of an unhandled exception");
    }

    private static int GetUnusedTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FailingStartupService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Failed to connect to RabbitMQ. Worker cannot start.");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
