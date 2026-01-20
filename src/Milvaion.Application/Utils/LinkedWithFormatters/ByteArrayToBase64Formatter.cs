using Milvasoft.Attributes.Annotations;

namespace Milvaion.Application.Utils.LinkedWithFormatters;

/// <summary>
/// Converts byte array to base64 string.
/// </summary>
public class ByteArrayToBase64Formatter : ILinkedWithFormatter
{
    /// <inheritdoc/>
    public const string FormatterName = "ByteArrayToBase64";

    /// <inheritdoc/>
    public object Format(object value)
    {
        if (value == null)
            return string.Empty;

        var base64String = Convert.ToBase64String((byte[])value);

        return base64String;
    }
}
