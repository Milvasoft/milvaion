namespace EmailWorker.Options;

/// <summary>
/// Configuration options for Email Worker.
/// </summary>
public class EmailWorkerOptions
{
    /// <summary>
    /// Configuration section key in appsettings.json.
    /// </summary>
    public const string SectionKey = "EmailConfig";

    /// <summary>
    /// Named SMTP configurations available to jobs.
    /// Key is the configuration alias (e.g., "Default", "Marketing", "Transactional").
    /// </summary>
    public Dictionary<string, SmtpConfig> SmtpConfigs { get; set; } = [];

    /// <summary>
    /// Default SMTP configuration name to use when not specified in job data.
    /// </summary>
    public string DefaultConfigName { get; set; } = "Default";

    /// <summary>
    /// Gets the list of available SMTP configuration names.
    /// </summary>
    public IReadOnlyList<string> GetConfigNames() => [.. SmtpConfigs.Keys];
}

/// <summary>
/// Individual SMTP server configuration.
/// </summary>
public class SmtpConfig
{
    /// <summary>
    /// SMTP server host name.
    /// </summary>
    public string Host { get; set; }

    /// <summary>
    /// SMTP server port (typically 25, 465, or 587).
    /// </summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// Username for SMTP authentication.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Password for SMTP authentication.
    /// </summary>
    public string Password { get; set; }

    /// <summary>
    /// Whether to use SSL/TLS encryption.
    /// </summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// Default sender email address.
    /// </summary>
    public string DefaultFromEmail { get; set; }

    /// <summary>
    /// Default sender display name.
    /// </summary>
    public string DefaultFromName { get; set; }

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to skip SSL certificate validation (use with caution!).
    /// </summary>
    public bool IgnoreCertificateErrors { get; set; } = false;
}
