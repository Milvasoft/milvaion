using System.Text.Json;
using System.Text.Json.Serialization;

namespace Milvasoft.Milvaion.Sdk.Worker.Options;

public static class ConstantJsonOptions
{
    public static JsonSerializerOptions PropNameCaseInsensitive { get; } = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Write not intended and ignore nulls JSON serializer options.
    /// </summary>
    public static JsonSerializerOptions WriteNotIntendedAndIgnoreNulls { get; } = new JsonSerializerOptions
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
