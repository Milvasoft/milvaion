using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.MultiLanguage.EntityBases.Abstract;
using Milvasoft.Core.Utils.Constants;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ContentManagementDtos.LanguageDtos;

/// <summary>
/// Language model.
/// </summary>
[Translate]
public class LanguageDto : ILanguage
{
    /// <summary>
    /// Language id.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Name of the language.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Iso code of the language.
    /// </summary>
    public string Code { get; set; }

    /// <summary>
    /// Determines whether the language is supported or not.
    /// </summary>
    [DisplayFormat("{supportedDescription}")]
    [Filterable(FilterComponentType = UiInputConstant.SwitchInput)]
    public bool Supported { get; set; }

    /// <summary>
    /// Determines whether language is supported or not.
    /// </summary>
    [Filterable(false)]
    [Browsable(false)]
    [LinkedWith<YesNoFormatter>(nameof(Supported), YesNoFormatter.FormatterName)]
    public string SupportedDescription { get; set; }

    /// <summary>
    /// Determines whether the language is default or not.
    /// </summary>
    [DisplayFormat("{isDefaultDescription}")]
    [Filterable(FilterComponentType = UiInputConstant.SwitchInput)]
    public bool IsDefault { get; set; }

    /// <summary>
    /// Determines whether language is default or not.
    /// </summary>
    [Filterable(false)]
    [Browsable(false)]
    [LinkedWith<YesNoFormatter>(nameof(IsDefault), YesNoFormatter.FormatterName)]
    public string IsDefaultDescription { get; set; }

    /// <summary>
    /// Projection expression for mapping Language entity to LanguageDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<Language, LanguageDto>> Projection { get; } = n => new LanguageDto
    {
        Id = n.Id,
        Name = n.Name,
        Code = n.Code,
        Supported = n.Supported,
        IsDefault = n.IsDefault,
    };
}
