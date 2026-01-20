namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// Database configuration.
/// </summary>
public class DatabaseConfigDto
{
    /// <summary>
    /// Database provider (PostgreSQL, SQL Server, etc.).
    /// </summary>
    public string Provider { get; set; }

    /// <summary>
    /// Database name.
    /// </summary>
    public string DatabaseName { get; set; }

    /// <summary>
    /// Database host.
    /// </summary>
    public string Host { get; set; }
}
