using CommandWorker.Jobs;
using CommandWorker.Options;
using CommandWorker.Services;
using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using System.Text.Json;

namespace Milvaion.UnitTests.WorkerTests;

[Trait("Worker Unit Tests", "CommandWorker command lines and process runs.")]
public class CommandWorkerTests
{
    private static CommandWorkerOptions Options(bool allowAdHoc = false) => new()
    {
        AllowAdHocCommands = allowAdHoc,
        Commands = new Dictionary<string, CommandDefinition>
        {
            ["backup"] = new()
            {
                FileName = "/scripts/backup.sh",
                Arguments = ["--target", "{target}", "--label={label}", "{{literal}}"],
                Environment = new() { ["MODE"] = "run-{target}" },
                Parameters = new()
                {
                    ["target"] = new() { Required = true, Pattern = "[a-z0-9-]+" },
                    ["label"] = new() { Default = "nightly" },
                    ["verbose"] = new(),
                },
                PermanentFailureExitCodes = [2],
            },
        },
    };

    private static Dictionary<string, object> Parameters(string json) => JsonSerializer.Deserialize<Dictionary<string, object>>(json);

    [Fact]
    public void Build_ShouldPassEachParameterAsWholeArgumentsAndEnvironment()
    {
        var command = CommandLineBuilder.Build(new CommandJobData { Command = "BACKUP", Parameters = Parameters("""{ "Target": "s3-eu" }""") }, Options());

        command.Name.Should().Be("backup");
        command.FileName.Should().Be("/scripts/backup.sh");
        command.Arguments.Should().Equal("--target", "s3-eu", "--label=nightly", "{literal}");
        command.Environment["MODE"].Should().Be("run-s3-eu");
        command.Environment["MILVAION_PARAM_TARGET"].Should().Be("s3-eu");
        command.Environment["MILVAION_PARAM_LABEL"].Should().Be("nightly");
        command.SuccessExitCodes.Should().Equal(0);
        command.PermanentFailureExitCodes.Should().Equal(2);
    }

    [Fact]
    public void Build_ShouldKeepShellMetacharactersInsideOneArgument()
    {
        var options = Options();
        options.Commands["backup"].Parameters["target"].Pattern = null;

        var command = CommandLineBuilder.Build(new CommandJobData { Command = "backup", Parameters = Parameters("""{ "target": "x; rm -rf / #" }""") }, options);

        command.Arguments[1].Should().Be("x; rm -rf / #", "the value is one argv entry, never parsed by a shell");
    }

    [Fact]
    public void Build_ShouldConvertNonStringParameterValues()
    {
        var options = Options();
        options.Commands["backup"].Parameters["target"].Pattern = null;

        var command = CommandLineBuilder.Build(new CommandJobData { Command = "backup", Parameters = Parameters("""{ "target": 42, "verbose": true }""") }, options);

        command.Arguments[1].Should().Be("42");
        command.Environment["MILVAION_PARAM_VERBOSE"].Should().Be("true");
    }

    [Theory]
    [InlineData("""{ }""", "needs parameter 'target'")]
    [InlineData("""{ "target": "UPPER" }""", "does not match")]
    [InlineData("""{ "target": "ok", "other": "x" }""", "has no parameter 'other'")]
    public void Build_ShouldRejectInvalidParameters(string json, string error)
    {
        var act = () => CommandLineBuilder.Build(new CommandJobData { Command = "backup", Parameters = Parameters(json) }, Options());

        act.Should().Throw<PermanentJobException>().WithMessage($"*{error}*");
    }

    [Fact]
    public void Build_ShouldRejectUnknownCommandsAndAdHocWhenDisabled()
    {
        FluentActions.Invoking(() => CommandLineBuilder.Build(new CommandJobData { Command = "nope" }, Options()))
                     .Should().Throw<PermanentJobException>().WithMessage("*not configured*backup*");

        FluentActions.Invoking(() => CommandLineBuilder.Build(new CommandJobData { FileName = "/bin/sh", Arguments = ["-c", "id"] }, Options()))
                     .Should().Throw<PermanentJobException>().WithMessage("*Ad-hoc commands are disabled*");

        FluentActions.Invoking(() => CommandLineBuilder.Build(new CommandJobData(), Options()))
                     .Should().Throw<PermanentJobException>().WithMessage("*needs a Command*");

        FluentActions.Invoking(() => CommandLineBuilder.Build(new CommandJobData { Command = "backup", Arguments = ["--x"] }, Options()))
                     .Should().Throw<PermanentJobException>().WithMessage("*only be set for ad-hoc*");
    }

    [Fact]
    public void Build_ShouldAllowAdHocWhenEnabled()
    {
        var command = CommandLineBuilder.Build(new CommandJobData { FileName = "/bin/sh", Arguments = ["-c", "id"] }, Options(allowAdHoc: true));

        command.FileName.Should().Be("/bin/sh");
        command.Arguments.Should().Equal("-c", "id");
    }

    [Fact]
    public void ValidateConfiguration_ShouldReportBrokenDefinitions()
    {
        var options = new CommandWorkerOptions
        {
            Commands = new()
            {
                ["no-file"] = new(),
                ["bad"] = new()
                {
                    FileName = "/bin/true",
                    Arguments = ["{missing}"],
                    Parameters = new() { ["p"] = new() { Pattern = "([" }, ["bad-name"] = new() },
                },
            },
        };

        var act = () => CommandLineBuilder.ValidateConfiguration(options);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*'no-file' has no FileName*")
           .WithMessage("*{missing}*")
           .WithMessage("*pattern of parameter 'p'*")
           .WithMessage("*'bad-name'*");

        FluentActions.Invoking(() => CommandLineBuilder.ValidateConfiguration(Options())).Should().NotThrow();
    }

    private static PreparedCommand Command(string fileName, params string[] arguments)
        => new("test", fileName, arguments, null, new Dictionary<string, string> { ["MILVAION_TEST"] = "yes" }, new HashSet<int> { 0 }, new HashSet<int>());

    private static string DotNet => Environment.ProcessPath is { } path && Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? path : "dotnet";

    [Fact]
    public async Task RunAsync_ShouldStreamOutputAndReturnTheExitCode()
    {
        var lines = new List<string>();

        var outcome = await ProcessRunner.RunAsync(Command(DotNet, "--version"), null, true, (line, _) => lines.Add(line), 8192, CancellationToken.None);

        outcome.ExitCode.Should().Be(0);
        lines.Should().NotBeEmpty();
        outcome.StandardOutputTail.Should().Contain(lines[0]);
    }

    [Fact]
    public async Task RunAsync_ShouldReportNonZeroExitCodes()
    {
        var outcome = await ProcessRunner.RunAsync(Command(DotNet, "this-command-does-not-exist-xyz"), null, true, (_, _) => { }, 8192, CancellationToken.None);

        outcome.ExitCode.Should().NotBe(0);
    }

    [Fact]
    public async Task RunAsync_ShouldWriteStandardInputAndPassEnvironment()
    {
        PreparedCommand command = OperatingSystem.IsWindows()
            ? Command("cmd.exe", "/c", "more & echo %MILVAION_TEST%")
            : Command("/bin/sh", "-c", "cat; echo \"$MILVAION_TEST\"");

        var outcome = await ProcessRunner.RunAsync(command, "hello from stdin", true, (_, _) => { }, 8192, CancellationToken.None);

        outcome.ExitCode.Should().Be(0);
        outcome.StandardOutputTail.Should().Contain("hello from stdin").And.Contain("yes");
    }

    [Fact]
    public async Task RunAsync_ShouldKeepOnlyTheEndOfLongOutput()
    {
        PreparedCommand command = OperatingSystem.IsWindows()
            ? Command("cmd.exe", "/c", "for /l %i in (1,1,200) do @echo line-%i")
            : Command("/bin/sh", "-c", "i=1; while [ $i -le 200 ]; do echo line-$i; i=$((i+1)); done");

        var outcome = await ProcessRunner.RunAsync(command, null, true, (_, _) => { }, 50, CancellationToken.None);

        outcome.StandardOutputTruncated.Should().BeTrue();
        outcome.StandardOutputTail.Length.Should().BeLessThanOrEqualTo(50);
        outcome.StandardOutputTail.Should().EndWith("line-200");
    }

    [Fact]
    public async Task RunAsync_ShouldFailPermanentlyWhenTheExecutableIsMissing()
    {
        var act = () => ProcessRunner.RunAsync(Command("/definitely/not/here-" + Guid.NewGuid()), null, true, (_, _) => { }, 8192, CancellationToken.None);

        await act.Should().ThrowAsync<PermanentJobException>().WithMessage("*Could not start*");
    }

    [Fact]
    public async Task RunAsync_ShouldKillTheProcessWhenCancelled()
    {
        PreparedCommand command = OperatingSystem.IsWindows()
            ? Command("powershell.exe", "-NoProfile", "-Command", "Start-Sleep -Seconds 60")
            : Command("/bin/sleep", "60");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;

        var act = () => ProcessRunner.RunAsync(command, null, true, (_, _) => { }, 8192, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(20));
    }
}
