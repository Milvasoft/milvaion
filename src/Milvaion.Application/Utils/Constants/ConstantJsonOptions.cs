using System.Text.Json;

namespace Milvaion.Application.Utils.Constants;

/// <summary>
/// Static JSON serializer options.
/// </summary>
public static class ConstantJsonOptions
{
    /// <summary>
    /// Property name case insensitive JSON serializer options.
    /// </summary>
    public static JsonSerializerOptions PropNameCaseInsensitive { get; } = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };
}
