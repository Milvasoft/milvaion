namespace Milvasoft.Milvaion.Sdk.Worker.Attributes;

/// <summary>
/// Marks a property as having dynamic enum values that should be populated at runtime.
/// The values are read from worker configuration and injected into the JSON schema.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// [DynamicEnum("SqlExecutorConfig:Connections")]
/// public string ConnectionName { get; set; }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class DynamicEnumAttribute(string configurationKey) : Attribute
{
    /// <summary>
    /// Configuration key path to read values from.
    /// The values should be dictionary keys (e.g., "SqlExecutorConfig:Connections" will read connection aliases).
    /// </summary>
    public string ConfigurationKey { get; } = configurationKey;

    /// <summary>
    /// Optional description template for each value.
    /// </summary>
    public string ValueDescriptionTemplate { get; set; }
}
