namespace CommandWorker.Options;

/// <summary>
/// Configuration options for the Command Worker.
/// </summary>
/// <remarks>
/// Jobs run commands defined here by name. Job data picks a command and fills in its declared parameters; it never
/// supplies the executable or a shell line, so whoever can create a job can only run what this file allows.
/// </remarks>
public class CommandWorkerOptions
{
    /// <summary>
    /// Configuration section key in appsettings.json.
    /// </summary>
    public const string SectionKey = "CommandWorker";

    /// <summary>
    /// Commands jobs may run, by name. The names are offered in the dashboard's job data form.
    /// </summary>
    public Dictionary<string, CommandDefinition> Commands { get; set; } = [];

    /// <summary>
    /// Lets job data name any executable and arguments instead of a configured command. Anyone who can create a job
    /// can then run anything this worker's user can, so leave it off unless every job author is trusted. Default: false.
    /// </summary>
    public bool AllowAdHocCommands { get; set; }

    /// <summary>
    /// Output lines written to the occurrence log per job; the rest are counted but not logged. Default: 1000.
    /// </summary>
    public int MaxLoggedLines { get; set; } = 1000;

    /// <summary>
    /// Characters of standard output kept as the job result (the end of the output). Default: 8192.
    /// </summary>
    public int MaxResultOutputChars { get; set; } = 8192;

    /// <summary>
    /// Whether commands see this worker's environment variables. When false they see only the variables their
    /// definition sets and the parameter variables. Default: true.
    /// </summary>
    public bool InheritEnvironment { get; set; } = true;
}

/// <summary>
/// A command jobs may run.
/// </summary>
public class CommandDefinition
{
    /// <summary>
    /// Shown in the dashboard.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Executable to start, e.g. <c>/usr/bin/python3</c> or <c>/scripts/backup.sh</c>. It is started directly,
    /// not through a shell.
    /// </summary>
    public string FileName { get; set; }

    /// <summary>
    /// Arguments, each passed to the process as one argument. <c>{name}</c> is replaced by the value of parameter
    /// <c>name</c> (<c>{{</c> and <c>}}</c> are literal braces). Because nothing goes through a shell, a parameter
    /// value can never start another command.
    /// </summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>
    /// Working directory. Default: the worker's.
    /// </summary>
    public string WorkingDirectory { get; set; }

    /// <summary>
    /// Environment variables to set. Values may use <c>{name}</c> placeholders like arguments.
    /// Every parameter is also passed as <c>MILVAION_PARAM_{NAME}</c>; scripts that build a shell line should read
    /// values from there, quoted, rather than receive them inside the line.
    /// </summary>
    public Dictionary<string, string> Environment { get; set; } = [];

    /// <summary>
    /// Parameters job data may set, by name. Job data that sets an undeclared parameter fails.
    /// </summary>
    public Dictionary<string, CommandParameter> Parameters { get; set; } = [];

    /// <summary>
    /// Exit codes that mean success. Default: 0.
    /// </summary>
    public List<int> SuccessExitCodes { get; set; } = [];

    /// <summary>
    /// Exit codes that mean retrying will not help; the job goes to the dead letter queue without retries.
    /// Any other non-success exit code is retried.
    /// </summary>
    public List<int> PermanentFailureExitCodes { get; set; } = [];
}

/// <summary>
/// A parameter of a command.
/// </summary>
public class CommandParameter
{
    /// <summary>
    /// Shown in the dashboard.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Whether job data must set it. A parameter with a <see cref="Default"/> is never missing.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// Value used when job data does not set the parameter.
    /// </summary>
    public string Default { get; set; }

    /// <summary>
    /// Regular expression the whole value must match, e.g. <c>^[a-z0-9-]+$</c>. Recommended for every parameter
    /// that ends up in a path or in an argument the program interprets.
    /// </summary>
    public string Pattern { get; set; }
}
