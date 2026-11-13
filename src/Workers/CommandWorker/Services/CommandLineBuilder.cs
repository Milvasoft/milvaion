using CommandWorker.Jobs;
using CommandWorker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommandWorker.Services;

/// <summary>
/// A command ready to start.
/// </summary>
public sealed record PreparedCommand(string Name,
                                     string FileName,
                                     IReadOnlyList<string> Arguments,
                                     string WorkingDirectory,
                                     IReadOnlyDictionary<string, string> Environment,
                                     IReadOnlySet<int> SuccessExitCodes,
                                     IReadOnlySet<int> PermanentFailureExitCodes);

/// <summary>
/// Turns job data into a command line, enforcing the configured commands and parameters.
/// </summary>
public static partial class CommandLineBuilder
{
    private static readonly TimeSpan _patternTimeout = TimeSpan.FromSeconds(1);

    [GeneratedRegex(@"\{\{|\}\}|\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// Builds the command for <paramref name="data"/>. Invalid job data throws <see cref="PermanentJobException"/>,
    /// since retrying cannot fix it.
    /// </summary>
    public static PreparedCommand Build(CommandJobData data, CommandWorkerOptions options)
    {
        if (data == null)
            throw new PermanentJobException("Job data is required.");

        var hasCommand = !string.IsNullOrWhiteSpace(data.Command);
        var hasAdHoc = !string.IsNullOrWhiteSpace(data.FileName);

        if (hasCommand && hasAdHoc)
            throw new PermanentJobException("Set either Command or FileName, not both.");

        if (hasAdHoc)
            return BuildAdHoc(data, options);

        if (!hasCommand)
            throw new PermanentJobException($"Job data needs a Command. Configured commands: {Available(options)}.");

        var (name, definition) = Find(options, data.Command.Trim())
            ?? throw new PermanentJobException($"Command '{data.Command}' is not configured. Configured commands: {Available(options)}.");

        if (data.Arguments?.Count > 0)
            throw new PermanentJobException("Arguments can only be set for ad-hoc commands; configured commands take Parameters.");

        var values = ResolveParameters(name, definition, data.Parameters);

        var arguments = definition.Arguments.Select(argument => Substitute(argument, values)).ToList();

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in definition.Environment)
            environment[key] = Substitute(value ?? string.Empty, values);

        foreach (var (key, value) in values)
            environment[$"MILVAION_PARAM_{key.ToUpperInvariant()}"] = value;

        return new PreparedCommand(name,
                                   definition.FileName,
                                   arguments,
                                   definition.WorkingDirectory,
                                   environment,
                                   ExitCodes(definition.SuccessExitCodes, defaultCode: 0),
                                   ExitCodes(definition.PermanentFailureExitCodes, defaultCode: null));
    }

    /// <summary>
    /// Checks every configured command, so a mistake in configuration stops the worker at startup instead of failing jobs.
    /// </summary>
    public static void ValidateConfiguration(CommandWorkerOptions options)
    {
        var errors = new List<string>();

        foreach (var (name, definition) in options.Commands)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.FileName))
            {
                errors.Add($"Command '{name}' has no FileName.");
                continue;
            }

            foreach (var (parameter, settings) in definition.Parameters)
            {
                if (!IsIdentifier(parameter))
                    errors.Add($"Command '{name}': parameter name '{parameter}' must be letters, digits and underscores.");

                if (!string.IsNullOrEmpty(settings?.Pattern))
                {
                    try
                    {
                        _ = new Regex(settings.Pattern, RegexOptions.None, _patternTimeout);
                    }
                    catch (ArgumentException ex)
                    {
                        errors.Add($"Command '{name}': pattern of parameter '{parameter}' is invalid: {ex.Message}");
                    }
                }
            }

            foreach (var template in definition.Arguments.Concat(definition.Environment.Values))
            {
                foreach (Match match in PlaceholderRegex().Matches(template ?? string.Empty))
                {
                    var placeholder = match.Groups[1];

                    if (placeholder.Success && FindKey(definition.Parameters, placeholder.Value) == null)
                        errors.Add($"Command '{name}' uses {{{placeholder.Value}}} but declares no parameter by that name.");
                }
            }
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid CommandWorker configuration:" + System.Environment.NewLine + string.Join(System.Environment.NewLine, errors));
    }

    private static PreparedCommand BuildAdHoc(CommandJobData data, CommandWorkerOptions options)
    {
        if (!options.AllowAdHocCommands)
            throw new PermanentJobException($"Ad-hoc commands are disabled on this worker. Use a configured command: {Available(options)}.");

        if (data.Parameters?.Count > 0)
            throw new PermanentJobException("Parameters only apply to configured commands; pass ad-hoc values as Arguments.");

        return new PreparedCommand("(ad-hoc)",
                                   data.FileName.Trim(),
                                   data.Arguments ?? [],
                                   null,
                                   new Dictionary<string, string>(),
                                   new HashSet<int> { 0 },
                                   new HashSet<int>());
    }

    private static Dictionary<string, string> ResolveParameters(string commandName, CommandDefinition definition, Dictionary<string, object> provided)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, raw) in provided ?? [])
        {
            var declared = FindKey(definition.Parameters, key)
                ?? throw new PermanentJobException($"Command '{commandName}' has no parameter '{key}'. Parameters: {ParameterList(definition)}.");

            values[declared] = ToText(raw);
        }

        foreach (var (name, parameter) in definition.Parameters)
        {
            var settings = parameter ?? new CommandParameter();

            if (!values.ContainsKey(name))
            {
                if (settings.Default != null)
                    values[name] = settings.Default;
                else if (settings.Required)
                    throw new PermanentJobException($"Command '{commandName}' needs parameter '{name}'.");
                else
                    continue;
            }

            if (!string.IsNullOrEmpty(settings.Pattern))
            {
                bool matches;

                try
                {
                    matches = Regex.IsMatch(values[name], $"^(?:{settings.Pattern})$", RegexOptions.None, _patternTimeout);
                }
                catch (RegexMatchTimeoutException)
                {
                    matches = false;
                }

                if (!matches)
                    throw new PermanentJobException($"Parameter '{name}' of command '{commandName}' does not match its allowed pattern.");
            }
        }

        return values;
    }

    /// <summary>
    /// Replaces <c>{name}</c> with parameter values. A placeholder whose parameter was not set becomes empty.
    /// </summary>
    public static string Substitute(string template, IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder();
        var last = 0;

        foreach (Match match in PlaceholderRegex().Matches(template))
        {
            builder.Append(template, last, match.Index - last);
            last = match.Index + match.Length;

            if (match.Value == "{{")
                builder.Append('{');
            else if (match.Value == "}}")
                builder.Append('}');
            else if (values.TryGetValue(match.Groups[1].Value, out var value))
                builder.Append(value);
        }

        builder.Append(template, last, template.Length - last);

        return builder.ToString();
    }

    private static string ToText(object raw) => raw switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null } => string.Empty,
        JsonElement element => element.GetRawText(),
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => raw.ToString()
    };

    private static (string Name, CommandDefinition Definition)? Find(CommandWorkerOptions options, string name)
    {
        var key = FindKey(options.Commands, name);

        return key == null || options.Commands[key] == null ? null : (key, options.Commands[key]);
    }

    private static string FindKey<T>(Dictionary<string, T> dictionary, string name)
        => dictionary.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlySet<int> ExitCodes(List<int> codes, int? defaultCode)
    {
        var set = new HashSet<int>(codes ?? []);

        if (set.Count == 0 && defaultCode.HasValue)
            set.Add(defaultCode.Value);

        return set;
    }

    private static bool IsIdentifier(string name) => !string.IsNullOrEmpty(name) && Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");

    private static string Available(CommandWorkerOptions options) => options.Commands.Count == 0 ? "(none)" : string.Join(", ", options.Commands.Keys);

    private static string ParameterList(CommandDefinition definition) => definition.Parameters.Count == 0 ? "(none)" : string.Join(", ", definition.Parameters.Keys);
}
