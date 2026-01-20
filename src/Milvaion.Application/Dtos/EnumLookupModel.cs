namespace Milvaion.Application.Dtos;

/// <summary>
/// Model for enum lookups.
/// </summary>
public class EnumLookupModel
{
    /// <summary>
    /// Enum value.
    /// </summary>
    public object Value { get; set; }

    /// <summary>
    /// Enum localized name.
    /// </summary>
    public string Name { get; set; }
}
