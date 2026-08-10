using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Milvaion.Application.Utils.Models.Options;
using Milvasoft.Milvaion.Sdk.Worker.Options;

namespace Milvaion.UnitTests.WorkerSdkTests;

/// <summary>
/// Covers the whole path an operator actually uses: an environment variable, read through
/// AddEnvironmentVariables, bound onto the options class, and turned into channel options.
///
/// The double underscore is what makes the variable name a section path, and nothing else in the
/// suite exercises that translation - a test binding a colon-separated key directly would pass
/// even if the setting were unreachable from the environment.
///
/// Environment variables are process-wide, so this class is not parallelised and each test clears
/// what it sets.
/// </summary>
[Collection(nameof(PublisherConfirmsConfigurationTests))]
[CollectionDefinition(nameof(PublisherConfirmsConfigurationTests), DisableParallelization = true)]
[Trait("SDK Unit Tests", "PublisherConfirms configuration binding.")]
public class PublisherConfirmsConfigurationTests
{
    private const string ApiVariable = "MilvaionConfig__RabbitMQ__PublisherConfirms";
    private const string WorkerVariable = "Worker__RabbitMQ__PublisherConfirms";

    private static IConfiguration BuildConfiguration() => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    private static void WithVariable(string name, string value, Action assert)
    {
        var previous = Environment.GetEnvironmentVariable(name);

        try
        {
            Environment.SetEnvironmentVariable(name, value);
            assert();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("False", false)]
    public void ApiOptions_ShouldBindPublisherConfirms_FromEnvironmentVariable(string value, bool expected)
        => WithVariable(ApiVariable, value, () =>
        {
            var options = BuildConfiguration().GetSection("MilvaionConfig:RabbitMQ").Get<RabbitMQOptions>();

            options.Should().NotBeNull();
            options.PublisherConfirms.Should().Be(expected);
        });

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void WorkerSettings_ShouldBindPublisherConfirms_FromEnvironmentVariable(string value, bool expected)
        => WithVariable(WorkerVariable, value, () =>
        {
            var settings = BuildConfiguration().GetSection("Worker:RabbitMQ").Get<RabbitMQSettings>();

            settings.Should().NotBeNull();
            settings.PublisherConfirms.Should().Be(expected);
        });

    [Fact]
    public void WorkerSettings_ShouldReachChannelOptions_FromEnvironmentVariable()
        => WithVariable(WorkerVariable, "false", () =>
        {
            var settings = BuildConfiguration().GetSection("Worker:RabbitMQ").Get<RabbitMQSettings>();

            // The end of the chain: what the variable ultimately changes is the channel the worker opens.
            var channelOptions = settings.BuildChannelOptions();

            channelOptions.PublisherConfirmationsEnabled.Should().BeFalse();
            channelOptions.PublisherConfirmationTrackingEnabled.Should().BeFalse();
        });

    [Fact]
    public void ApiOptions_ShouldDefaultToEnabled_WhenVariableIsAbsent()
        => WithVariable(ApiVariable, null, () =>
        {
            // Absent means the binder leaves the property initialiser alone, rather than defaulting to false.
            var options = BuildConfiguration().GetSection("MilvaionConfig:RabbitMQ").Get<RabbitMQOptions>()
                          ?? new RabbitMQOptions();

            options.PublisherConfirms.Should().BeTrue();
        });

    [Fact]
    public void WorkerSettings_ShouldDefaultToEnabled_WhenVariableIsAbsent()
        => WithVariable(WorkerVariable, null, () =>
        {
            var settings = BuildConfiguration().GetSection("Worker:RabbitMQ").Get<RabbitMQSettings>()
                           ?? new RabbitMQSettings();

            settings.PublisherConfirms.Should().BeTrue();
        });
}
