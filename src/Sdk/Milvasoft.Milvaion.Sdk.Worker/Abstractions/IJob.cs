namespace Milvasoft.Milvaion.Sdk.Worker.Abstractions;

/// <summary>
/// Base interface for all job types.
/// </summary>
public interface IJobBase
{
}

#region Non-Generic Interfaces (for jobs without typed JobData)

/// <summary>
/// Interface that all job implementations must implement.
/// Provides strongly-typed job execution with context.
/// NOTE: Synchronous jobs do not support cancellation.
/// For cancellation support, use <see cref="IAsyncJob"/> instead.
/// </summary>
public interface IJob : IJobBase
{
    /// <summary>
    /// Executes the job logic.
    /// </summary>
    /// <param name="context">Job execution context providing logging, cancellation, and metadata</param>
    void Execute(IJobContext context);
}

/// <summary>
/// Interface that all job implementations must implement.
/// Provides strongly-typed job execution with context.
/// NOTE: Synchronous jobs do not support cancellation.
/// For cancellation support, use <see cref="IAsyncJobWithResult"/> instead.
/// </summary>
public interface IJobWithResult : IJobBase
{
    /// <summary>
    /// Executes the job logic.
    /// </summary>
    /// <param name="context">Job execution context providing logging, cancellation, and metadata</param>
    /// <returns>Result string to store</returns>
    string Execute(IJobContext context);
}

/// <summary>
/// Interface that all job implementations must implement.
/// Provides strongly-typed job execution with context and cancellation support.
/// </summary>
public interface IAsyncJob : IJobBase
{
    /// <summary>
    /// Executes the job logic.
    /// </summary>
    /// <param name="context">Job execution context providing logging, cancellation, and metadata</param>
    /// <returns>Task representing the asynchronous operation</returns>
    Task ExecuteAsync(IJobContext context);
}

/// <summary>
/// Interface that all job implementations must implement.
/// Provides strongly-typed job execution with context and cancellation support.
/// </summary>
public interface IAsyncJobWithResult : IJobBase
{
    /// <summary>
    /// Executes the job logic.
    /// </summary>
    /// <param name="context">Job execution context providing logging, cancellation, and metadata</param>
    /// <returns>Task representing the asynchronous operation with result</returns>
    Task<string> ExecuteAsync(IJobContext context);
}

#endregion

#region Generic Interfaces (for jobs with typed JobData)

/// <summary>
/// Interface for jobs that require typed job data.
/// The generic type parameter defines the expected job data schema.
/// </summary>
/// <typeparam name="TJobData">The type of job data this job expects. Must be a class with parameterless constructor.</typeparam>
public interface IJob<TJobData> : IJob where TJobData : class, new()
{
}

/// <summary>
/// Interface for jobs that require typed job data and return a result.
/// The generic type parameter defines the expected job data schema.
/// </summary>
/// <typeparam name="TJobData">The type of job data this job expects. Must be a class with parameterless constructor.</typeparam>
public interface IJobWithResult<TJobData> : IJobWithResult where TJobData : class, new()
{
}

/// <summary>
/// Interface for async jobs that require typed job data.
/// The generic type parameter defines the expected job data schema.
/// Use <see cref="IJobContext.GetData{T}"/> in ExecuteAsync to get the typed data.
/// </summary>
/// <typeparam name="TJobData">The type of job data this job expects. Must be a class with parameterless constructor.</typeparam>
/// <example>
/// <code>
/// public class SendEmailJob : IAsyncJob&lt;EmailJobData&gt;
/// {
///     public async Task ExecuteAsync(IJobContext context)
///     {
///         var data = context.GetData&lt;EmailJobData&gt;();
///         // Send email using data.To, data.Subject, etc.
///     }
/// }
/// 
/// public class EmailJobData
/// {
///     public string To { get; set; }
///     public string Subject { get; set; }
///     public string Body { get; set; }
/// }
/// </code>
/// </example>
public interface IAsyncJob<TJobData> : IAsyncJob where TJobData : class, new()
{
}

/// <summary>
/// Interface for async jobs that require typed job data and return a result.
/// The generic type parameter defines the expected job data schema.
/// </summary>
/// <typeparam name="TJobData">The type of job data this job expects. Must be a class with parameterless constructor.</typeparam>
public interface IAsyncJobWithResult<TJobData> : IAsyncJobWithResult where TJobData : class, new()
{
}

#endregion
