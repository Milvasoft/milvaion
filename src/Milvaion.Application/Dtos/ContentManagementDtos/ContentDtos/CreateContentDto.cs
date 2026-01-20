namespace Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;

/// <summary>
/// Create content model.
/// </summary>
public record CreateContentDto
{
    /// <summary>
    /// Key of content.
    /// </summary>
    public string Key { get; set; }

    /// <summary>
    /// Value of content.
    /// </summary>
    public string Value { get; set; }

    /// <summary>
    /// LanguageId of content.
    /// </summary>
    public int LanguageId { get; set; }

    /// <summary>
    /// Id of the namespace this content belongs to.
    /// </summary>
    public int ResourceGroupId { get; set; }

    /// <summary>
    /// Media files if exists.
    /// </summary>
    public List<UpsertMediaDto> Medias { get; set; }
}
