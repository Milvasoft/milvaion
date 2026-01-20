namespace Milvaion.Application.Dtos.AdminDtos;

/// <summary>
/// Database statistics response containing table sizes, occurrence growth, and large occurrences.
/// </summary>
public class DatabaseStatisticsDto
{
    /// <summary>
    /// Table size statistics.
    /// </summary>
    public List<TableSizeDto> TableSizes { get; set; }

    /// <summary>
    /// Occurrence growth statistics (last 30 days).
    /// </summary>
    public List<OccurrenceGrowthDto> OccurrenceGrowth { get; set; }

    /// <summary>
    /// Top 10 largest occurrences.
    /// </summary>
    public List<LargeOccurrenceDto> LargeOccurrences { get; set; }

    /// <summary>
    /// Total database size in bytes.
    /// </summary>
    public long TotalDatabaseSizeBytes { get; set; }

    /// <summary>
    /// Total database size (human-readable).
    /// </summary>
    public string TotalDatabaseSize { get; set; }
}

/// <summary>
/// Table size information.
/// </summary>
public class TableSizeDto
{
    /// <summary>
    /// Schema name (usually 'public').
    /// </summary>
    public string SchemaName { get; set; }

    /// <summary>
    /// Table name.
    /// </summary>
    public string TableName { get; set; }

    /// <summary>
    /// Human-readable size (e.g., "6 GB").
    /// </summary>
    public string Size { get; set; }

    /// <summary>
    /// Size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Percentage of total database size.
    /// </summary>
    public decimal Percentage { get; set; }
}

/// <summary>
/// Occurrence growth statistics for a specific day and status.
/// </summary>
public class OccurrenceGrowthDto
{
    /// <summary>
    /// Day (date truncated to day).
    /// </summary>
    public DateTime Day { get; set; }

    /// <summary>
    /// Occurrence status (0=Pending, 1=Running, 2=Success, 3=Failed, 4=Cancelled).
    /// </summary>
    public int Status { get; set; }

    /// <summary>
    /// Number of occurrences for this day and status.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Average exception size in bytes.
    /// </summary>
    public int? AvgExceptionSize { get; set; }

    /// <summary>
    /// Average log entry count.
    /// </summary>
    public int? AvgLogCount { get; set; }
}

/// <summary>
/// Large occurrence information.
/// </summary>
public class LargeOccurrenceDto
{
    /// <summary>
    /// Occurrence ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Job name.
    /// </summary>
    public string JobName { get; set; }

    /// <summary>
    /// Occurrence status.
    /// </summary>
    public int Status { get; set; }

    /// <summary>
    /// Created at timestamp.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Logs size in bytes.
    /// </summary>
    public int LogsSize { get; set; }

    /// <summary>
    /// Exception size in bytes.
    /// </summary>
    public int ExceptionSize { get; set; }

    /// <summary>
    /// Status change logs size in bytes.
    /// </summary>
    public int StatusLogsSize { get; set; }

    /// <summary>
    /// Total size in bytes.
    /// </summary>
    public int TotalSize => LogsSize + ExceptionSize + StatusLogsSize;
}
