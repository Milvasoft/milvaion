using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Utils.LinkedWithFormatters;

/// <summary>
/// Converts boolean value to Exists or NotExists.
/// </summary>
/// <param name="milvaLocalizer"></param>
public class ExistsNotFormatter(IMilvaLocalizer milvaLocalizer) : ILinkedWithFormatter
{
    private readonly IMilvaLocalizer _milvaLocalizer = milvaLocalizer;

    /// <inheritdoc/>
    public static string FormatterName => "ExistsNot";

    /// <inheritdoc/>
    public object Format(object value)
    {
        if (value is bool boolValue)
            return (boolValue ? _milvaLocalizer["Exists"] : _milvaLocalizer["NotExists"]).ToString();

        return value?.ToString();
    }
}
