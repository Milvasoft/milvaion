using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CommandWorker.Services;

/// <summary>
/// Outcome of a process run.
/// </summary>
public sealed record ProcessOutcome(int ExitCode, string StandardOutputTail, bool StandardOutputTruncated, string StandardErrorTail, long DurationMs);

/// <summary>
/// Starts a prepared command without a shell, streams its output line by line, and kills the whole process tree
/// when the job is cancelled or times out.
/// </summary>
public static class ProcessRunner
{
    private const int _stderrTailChars = 4096;

    /// <param name="command">The command to start.</param>
    /// <param name="standardInput">Written to standard input, which is then closed. Null closes it right away.</param>
    /// <param name="inheritEnvironment">Whether the process sees this process's environment variables.</param>
    /// <param name="onLine">Receives every output line; the flag is true for standard error.</param>
    /// <param name="maxOutputChars">Characters of standard output kept for the result (the end of it).</param>
    /// <param name="cancellationToken">Kills the process tree when cancelled.</param>
    public static async Task<ProcessOutcome> RunAsync(PreparedCommand command,
                                                      string standardInput,
                                                      bool inheritEnvironment,
                                                      Action<string, bool> onLine,
                                                      int maxOutputChars,
                                                      CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);

        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
            startInfo.WorkingDirectory = command.WorkingDirectory;

        if (!inheritEnvironment)
            startInfo.Environment.Clear();

        foreach (var (key, value) in command.Environment)
            startInfo.Environment[key] = value;

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
                throw new PermanentJobException($"Could not start '{command.FileName}'.");
        }
        catch (Win32Exception ex)
        {
            // Missing executable or no permission: retrying will not fix it.
            throw new PermanentJobException($"Could not start '{command.FileName}': {ex.Message}", ex);
        }

        var stdout = new TailBuffer(maxOutputChars);
        var stderr = new TailBuffer(_stderrTailChars);

        var readOutput = PumpAsync(process.StandardOutput, line =>
        {
            stdout.AppendLine(line);
            onLine(line, false);
        });

        var readError = PumpAsync(process.StandardError, line =>
        {
            stderr.AppendLine(line);
            onLine(line, true);
        });

        try
        {
            if (standardInput != null)
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process exited without reading its input.
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(readOutput, readError);

            throw;
        }

        // WaitForExitAsync returns once the process ends; the output may still be draining.
        await Task.WhenAll(readOutput, readError);
        stopwatch.Stop();

        return new ProcessOutcome(process.ExitCode, stdout.ToString(), stdout.Truncated, stderr.ToString(), stopwatch.ElapsedMilliseconds);
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
            onLine(line);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited
        }
    }

    /// <summary>
    /// Keeps the last <c>capacity</c> characters written to it.
    /// </summary>
    private sealed class TailBuffer(int capacity)
    {
        private readonly StringBuilder _builder = new();
        private readonly Lock _lock = new();

        public bool Truncated { get; private set; }

        public void AppendLine(string line)
        {
            lock (_lock)
            {
                if (_builder.Length > 0)
                    _builder.Append('\n');

                _builder.Append(line);

                if (capacity > 0 && _builder.Length > capacity)
                {
                    _builder.Remove(0, _builder.Length - capacity);
                    Truncated = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                return _builder.ToString();
            }
        }
    }
}
