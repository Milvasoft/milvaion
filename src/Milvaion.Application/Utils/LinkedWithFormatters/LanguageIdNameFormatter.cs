using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.MultiLanguage.Manager;

namespace Milvaion.Application.Utils.LinkedWithFormatters;

/// <summary>
/// Gets language name by language id from static language list via multi language manager.
/// </summary>
public class LanguageIdNameFormatter : ILinkedWithFormatter
{
    /// <inheritdoc/>
    public const string FormatterName = "LanguageIdName";

    /// <inheritdoc/>
    public object Format(object value)
    {
        if (value is int intValue)
        {
            var language = MultiLanguageManager.Languages.FirstOrDefault(l => l.Id == intValue);

            return language?.Name ?? MessageConstant.QuestionMark;
        }

        return MessageConstant.QuestionMark;
    }
}
