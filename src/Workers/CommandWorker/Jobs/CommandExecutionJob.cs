using CommandWorker.Options;
using CommandWorker.Services;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;

namespace CommandWorker.Jobs;

/// <summary>
/// Runs a configured command, or an ad-hoc one where the worker allows it. Output lines go to the occurrence log;
/// the exit code decides the outcome.
/// </summary>
public class CommandExecutionJob(IOptions<CommandWorkerOptions> options) : IAsyncJobWithResult<CommandJobData, CommandResult>
{
    private readonly CommandWorkerOptions _options = options.Value;

    public async Task<CommandResult> ExecuteAsync(IJobContext context)
    {
        var data = context.GetData<CommandJobData>() ?? throw new PermanentJobException("CommandJobData is required but was null");
        var command = CommandLineBuilder.Build(data, _options);

        // Arguments can carry parameter values, which may be secrets, so only the command and executable are logged.
        context.LogInformation($"Running command '{command.Name}' ({command.FileName})");

        var logged = 0;
        var skipped = 0;
        var gate = new Lock();

        void OnLine(string line, bool isError)
        {
            lock (gate)
            {
                if (logged >= _options.MaxLoggedLines)
                {
                    skipped++;
                    return;
                }

                logged++;
            }

            if (isError)
                context.LogWarning(line, new Dictionary<string, object> { ["Stream"] = "stderr" });
            else
                context.LogInformation(line);
        }

        var outcome = await ProcessRunner.RunAsync(command, data.StandardInput, _options.InheritEnvironment, OnLine, _options.MaxResultOutputChars, context.CancellationToken);

        if (skipped > 0)
            context.LogWarning($"{skipped} more output line(s) were not logged (MaxLoggedLines is {_options.MaxLoggedLines}).");

        context.LogInformation($"Command '{command.Name}' exited with code {outcome.ExitCode} after {outcome.DurationMs} ms");

        if (command.SuccessExitCodes.Contains(outcome.ExitCode))
        {
            return new CommandResult
            {
                ExitCode = outcome.ExitCode,
                Output = outcome.StandardOutputTail,
                OutputTruncated = outcome.StandardOutputTruncated,
                DurationMs = outcome.DurationMs
            };
        }

        var message = $"Command '{command.Name}' exited with code {outcome.ExitCode}.";

        if (!string.IsNullOrWhiteSpace(outcome.StandardErrorTail))
            message += $" Error output:\n{outcome.StandardErrorTail}";

        if (command.PermanentFailureExitCodes.Contains(outcome.ExitCode))
            throw new PermanentJobException(message);

        throw new CommandFailedException(message, outcome.ExitCode);
    }
}

/// <summary>
/// A command exited with a code that is neither success nor a permanent failure; the job is retried.
/// </summary>
public class CommandFailedException(string message, int exitCode) : Exception(message)
{
    /// <summary>
    /// Exit code of the process.
    /// </summary>
    public int ExitCode { get; } = exitCode;
}
