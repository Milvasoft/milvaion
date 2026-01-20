using Milvaion.Domain.UI;
using Milvasoft.Core.MultiLanguage.EntityBases.Abstract;
using System.Text.Json.Serialization;

namespace Milvaion.Domain.JsonModels;

/// <summary>
/// Json model for menu group translations.
/// </summary>
public class MenuGroupTranslation : ITranslationEntityWithIntKey<MenuGroup>
{
    /// <summary>
    /// Gets or sets the menu group name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Language id.
    /// </summary>
    public int LanguageId { get; set; }

    /// <summary>
    /// Related entity id.
    /// </summary>
    public int EntityId { get; set; }

    /// <summary>
    /// Unused property. Because of jsonb.
    /// </summary>
    [JsonIgnore]
    public MenuGroup Entity { get; set; }
}
