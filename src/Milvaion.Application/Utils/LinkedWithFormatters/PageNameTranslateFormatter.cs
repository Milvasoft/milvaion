using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Utils.LinkedWithFormatters;

/// <summary>
/// Converts boolean value to Yes or No.
/// </summary>
/// <param name="milvaLocalizer"></param>
public class PageNameTranslateFormatter(IMilvaLocalizer milvaLocalizer) : ILinkedWithFormatter
{
    private readonly IMilvaLocalizer _milvaLocalizer = milvaLocalizer;

    /// <inheritdoc/>
    public const string FormatterName = "PageNameTranslate";

    /// <inheritdoc/>
    public object Format(object value)
    {
        if (value is string stringValue)
            return _milvaLocalizer[$"UI.{stringValue}"].ToString();

        return value?.ToString();
    }
}
