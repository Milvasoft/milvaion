using Milvasoft.Milvaion.Sdk.Worker.Attributes;
using System.ComponentModel;

namespace CommandWorker.Jobs;

/// <summary>
/// Job data for running a command.
/// </summary>
public class CommandJobData
{
    /// <summary>
    /// Configuration key for dynamic enum values (command names).
    /// </summary>
    public const string CommandsConfigKey = "CommandWorker:Commands";

    /// <summary>
    /// Name of a command configured in the worker's CommandWorker:Commands section.
    /// </summary>
    [Description("Command to run (configured in the worker's appsettings.json)")]
    [DynamicEnum(CommandsConfigKey)]
    public string Command { get; set; }

    /// <summary>
    /// Values of the command's declared parameters.
    /// </summary>
    [Description("Parameter values, e.g. { \"target\": \"s3\" }. Only parameters the command declares are accepted.")]
    public Dictionary<string, object> Parameters { get; set; }

    /// <summary>
    /// Text written to the command's standard input.
    /// </summary>
    [Description("Text written to the command's standard input (optional)")]
    public string StandardInput { get; set; }

    /// <summary>
    /// Executable to run instead of a configured command. Only accepted when AllowAdHocCommands is enabled.
    /// </summary>
    [Description("Ad-hoc executable instead of a configured command (only if the worker allows ad-hoc commands)")]
    public string FileName { get; set; }

    /// <summary>
    /// Arguments of the ad-hoc executable.
    /// </summary>
    [Description("Ad-hoc arguments, one item per argument (only if the worker allows ad-hoc commands)")]
    public List<string> Arguments { get; set; }
}

/// <summary>
/// Result of a command run.
/// </summary>
public class CommandResult
{
    /// <summary>
    /// Exit code of the process.
    /// </summary>
    public int ExitCode { get; set; }

    /// <summary>
    /// The end of the standard output.
    /// </summary>
    public string Output { get; set; }

    /// <summary>
    /// Whether <see cref="Output"/> is cut to its last characters.
    /// </summary>
    public bool OutputTruncated { get; set; }

    /// <summary>
    /// How long the process ran.
    /// </summary>
    public long DurationMs { get; set; }
}
