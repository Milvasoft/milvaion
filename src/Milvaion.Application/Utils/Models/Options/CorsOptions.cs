namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// CORS options configuration model
/// </summary>
public class CorsOptionsConfig
{
    /// <summary>
    /// Default CORS policy name
    /// </summary>
    public string DefaultPolicy { get; set; } = string.Empty;

    /// <summary>
    /// Policies for CORS configuration
    /// </summary>
    public Dictionary<string, CorsPolicyConfig> Policies { get; set; } = [];
}

/// <summary>
/// CORS policy configuration model
/// </summary>
public class CorsPolicyConfig
{
    /// <summary>
    /// Origin URLs allowed for CORS requests
    /// </summary>
    public string[] Origins { get; set; } = [];

    /// <summary>
    /// Methods allowed for CORS requests
    /// </summary>
    public string[] Methods { get; set; } = [];

    /// <summary>
    /// Header names allowed for CORS requests
    /// </summary>
    public string[] Headers { get; set; } = [];

    /// <summary>
    /// Exposed header names for CORS requests
    /// </summary>
    public string[] ExposedHeaders { get; set; } = [];

    /// <summary>
    /// Allow credentials for CORS requests
    /// </summary>
    public bool AllowCredentials { get; set; }
}