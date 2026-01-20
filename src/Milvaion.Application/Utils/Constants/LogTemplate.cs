namespace Milvaion.Application.Utils.Constants;

/// <summary>
/// Represents a class that contains global constants for the application.
/// </summary>
public static class LogTemplate
{
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public static readonly string LogMessage = "Message: {Message}";
    public const string Exception = "Exception raised : {message}";
    public const string ExceptionWithRequest = "Exception : {message} {TransactionId}{Severity}{Timestamp}{Method}{Path}{@RequestInfoJson}{UserName}{@Exception}";
    public const string RequestResponse = "{TransactionId}{Severity}{Timestamp}{Path}{@RequestInfoJson}{@ResponseInfoJson}{ElapsedMs}{IpAddress}{UserName}{@Exception}";
    public const string JobException = "Exception occured in {JobName}. {message}";
    public const string JobExecuted = "{JobName} executed successfully! Affected rows : {RowCount}";
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}
