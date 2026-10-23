using Microsoft.Extensions.Hosting;

namespace Milvasoft.Milvaion.Sdk.Worker;

/// <summary>
/// Host extensions for running Milvaion workers.
/// </summary>
public static class WorkerHostExtensions
{
    /// <summary>
    /// Runs the worker host and returns the process exit code instead of letting a startup failure escape <c>Main</c>.
    /// A failed startup (e.g. RabbitMQ or Redis not reachable) returns 1, so the process exits cleanly with a
    /// non-zero code and container restart policies can kick in.
    /// </summary>
    /// <example>
    /// <code>
    /// var host = builder.Build();
    ///
    /// return await host.RunMilvaionWorkerAsync();
    /// </code>
    /// </example>
    /// <param name="host">Built worker host (also works with a <c>WebApplication</c>).</param>
    /// <param name="cancellationToken">Token that triggers shutdown.</param>
    /// <returns>Process exit code.</returns>
    public static async Task<int> RunMilvaionWorkerAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        try
        {
            await host.RunAsync(cancellationToken);

            return Environment.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            // The host has already logged the failure ("Hosting failed to start") and flushed its loggers on dispose.
            await Console.Error.WriteLineAsync($"Worker terminated: {ex.GetType().Name}: {ex.Message}");

            return 1;
        }
    }
}
