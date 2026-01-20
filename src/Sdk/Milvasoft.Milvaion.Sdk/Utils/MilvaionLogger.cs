using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.EntityBases.Concrete;
using System.Text.Json;
using ILoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory;

namespace Milvasoft.Milvaion.Sdk.Utils;

/// <summary>
/// Model for method logs.
/// </summary>
public class MethodLog : LogEntityBase<int>;

/// <summary>
/// Logs messages to the configured logging(<see cref="ILogger"/>) framework.
/// </summary>
/// <param name="loggerFactory"></param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2254:Template should be a static expression", Justification = "<Pending>")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0079:Remove unnecessary suppression", Justification = "<Pending>")]
public class MilvaionLogger(ILoggerFactory loggerFactory) : IMilvaLogger
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<MilvaionLogger>();

    /// <inheritdoc/>
    public void Log(string logEntry)
    {
        var logObject = JsonSerializer.Deserialize<MethodLog>(logEntry);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("{TransactionId}{Namespace}{ClassName}{MethodName}{MethodParams}{MethodResult}{ElapsedMs}{UtcLogTime}{CacheInfo}{Exception}{IsSuccess}",
                                   logObject.TransactionId,
                                   logObject.Namespace,
                                   logObject.ClassName,
                                   logObject.MethodName,
                                   logObject.MethodParams,
                                   logObject.MethodResult,
                                   logObject.ElapsedMs,
                                   logObject.UtcLogTime,
                                   logObject.CacheInfo,
                                   logObject.Exception,
                                   logObject.IsSuccess);
    }

    /// <summary>
    /// Logs the message with the specified severity.
    /// </summary>
    /// <param name="severity"></param>
    /// <param name="message"></param>
    public void Log(LogLevel severity, string message)
    {
        if (_logger.IsEnabled(severity))
            _logger.Log(severity, message);
    }

    /// <summary>
    /// Logs the message with the specified severity.
    /// </summary>
    /// <param name="severity"></param>
    /// <param name="messageTemplate"></param>
    /// <param name="args"></param>
    public void Log(LogLevel severity, string messageTemplate, params object[] args)
    {
        if (_logger.IsEnabled(severity))
            _logger.Log(severity, messageTemplate, args);
    }

    /// <summary>
    /// Logs the message with the specified severity.
    /// </summary>
    /// <param name="severity"></param>
    /// <param name="ex"></param>
    /// <param name="message"></param>
    public void Log(LogLevel severity, Exception ex, string message)
    {
        if (_logger.IsEnabled(severity))
            _logger.Log(severity, ex, message);
    }

    /// <summary>
    /// Logs the message with the specified severity.
    /// </summary>
    /// <param name="severity"></param>
    /// <param name="ex"></param>
    /// <param name="messageTemplate"></param>
    /// <param name="args"></param>
    public void Log(LogLevel severity, Exception ex, string messageTemplate, params object[] args)
    {
        if (_logger.IsEnabled(severity))
            _logger.Log(severity, ex, messageTemplate, args);
    }

    /// <inheritdoc/>
    public Task LogAsync(string logEntry)
    {
        var logObject = JsonSerializer.Deserialize<MethodLog>(logEntry);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("{TransactionId}{Namespace}{ClassName}{MethodName}{MethodParams}{MethodResult}{ElapsedMs}{UtcLogTime}{CacheInfo}{Exception}{IsSuccess}",
                                   logObject.TransactionId,
                                   logObject.Namespace,
                                   logObject.ClassName,
                                   logObject.MethodName,
                                   logObject.MethodParams,
                                   logObject.MethodResult,
                                   logObject.ElapsedMs,
                                   logObject.UtcLogTime,
                                   logObject.CacheInfo,
                                   logObject.Exception,
                                   logObject.IsSuccess);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Debug(string message) => Log(LogLevel.Debug, message);

    /// <inheritdoc/>
    public void Debug(string messageTemplate, params object[] propertyValues) => Log(LogLevel.Debug, messageTemplate, propertyValues);

    /// <inheritdoc/>
    public void Debug(Exception exception, string messageTemplate) => Log(LogLevel.Debug, exception, messageTemplate);

    /// <inheritdoc/>
    public void Debug(Exception exception, string messageTemplate, params object[] propertyValues) => Log(LogLevel.Debug, exception, messageTemplate, propertyValues);

    /// <inheritdoc/>
    public void Error(string message) => Log(LogLevel.Error, message);

    /// <inheritdoc/>
    public void Error(string messageTemplate, params object[] propertyValues) => Log(LogLevel.Error, messageTemplate, propertyValues);

    /// <inheritdoc/>
    public void Error(Exception exception, string messageTemplate) => Log(LogLevel.Error, exception, messageTemplate);

    /// <inheritdoc/>
    public void Error(Exception exception, string messageTemplate, params object[] propertyValues) => Log(LogLevel.Error, exception, messageTemplate, propertyValues);

    /// <inheritdoc/>
    public void Fatal(string message) => Log(LogLevel.Critical, message);

    /// <inheritdoc/>
    public void Fatal(string messageTemplate, params object[] propertyValues) => Log(LogLevel.Critical, messageTemplate, propertyValues);

    /// <inheritdoc/>
    public void Fatal(Exception exception, string messageTemplate) => Log(LogLevel.Critical, exception, messageTemplate);

    /// <inheritdoc/>
    public void Fatal(Exception exception, string messageTemplate, params object[] propertyValues) => Log(LogLevel.Critical, exception, messageTemplate, propertyValues);

    /// <inheritdoc/>
    public void Information(string message) => Log(LogLevel.Information, message);

    /// <inheritdoc/>
    public void Information(string messageTemplate, params object[] propertyValues) => Log(LogLevel.Information, messageTemplate, propertyValues);

    /// <inheritdoc/>
    public void Information(Exception exception, string messageTemplate) => Log(LogLevel.Information, exception, messageTemplate);

    /// <inheritdoc/>
    public void Information(Exception exception, string messageTemplate, params object[] propertyValues) => Log(LogLevel.Information, exception, messageTemplate, propertyValues);

    /// <inheritdoc/>
    public void Verbose(string message) => Log(LogLevel.Trace, message);

    /// <inheritdoc/>
    public void Verbose(string messageTemplate, params object[] propertyValues) => Log(LogLevel.Trace, messageTemplate, propertyValues);

    /// <inheritdoc/>
    public void Verbose(Exception exception, string messageTemplate) => Log(LogLevel.Trace, exception, messageTemplate);

    /// <inheritdoc/>
    public void Verbose(Exception exception, string messageTemplate, params object[] propertyValues) => Log(LogLevel.Trace, exception, messageTemplate, propertyValues);

    /// <inheritdoc/>
    public void Warning(string message) => Log(LogLevel.Warning, message);

    /// <inheritdoc/>
    public void Warning(string messageTemplate, params object[] propertyValues) => Log(LogLevel.Warning, messageTemplate, propertyValues);

    /// <inheritdoc/>
    public void Warning(Exception exception, string messageTemplate) => Log(LogLevel.Warning, exception, messageTemplate);

    /// <inheritdoc/>
    public void Warning(Exception exception, string messageTemplate, params object[] propertyValues) => Log(LogLevel.Warning, exception, messageTemplate, propertyValues);
}
