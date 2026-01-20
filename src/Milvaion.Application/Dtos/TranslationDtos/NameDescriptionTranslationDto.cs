namespace Milvaion.Application.Dtos.TranslationDtos;

/// <summary>
/// Common translation dto.
/// </summary>
public class NameDescriptionTranslationDto
{
    /// <summary>
    /// Translation of name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Translation of description.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Id of language.
    /// </summary>
    public int LanguageId { get; set; }
}
