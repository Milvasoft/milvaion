using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Utils.LinkedWithFormatters;

/// <summary>
/// Converts boolean value to Yes or No.
/// </summary>
/// <param name="milvaLocalizer"></param>
public class YesNoFormatter(IMilvaLocalizer milvaLocalizer) : ILinkedWithFormatter
{
    private readonly IMilvaLocalizer _milvaLocalizer = milvaLocalizer;

    /// <inheritdoc/>
    public const string FormatterName = "YesNo";

    /// <inheritdoc/>
    public object Format(object value)
    {
        if (value is bool boolValue)
            return (boolValue ? _milvaLocalizer["Yes"] : _milvaLocalizer["No"]).ToString();

        return value?.ToString();
    }
}
