using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete;
using Milvasoft.Core.MultiLanguage.EntityBases.Concrete;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Language entity.
/// </summary>
[Table(TableNames.Languages)]
[DontIndexCreationDate]
public class Language : LanguageEntity
{
    /// <summary>
    /// Gets the unique identifier of the entity.
    /// </summary>
    /// <returns></returns>
    public override object GetUniqueIdentifier() => Id;

    /// <summary>
    /// Returns this instance of "<see cref="Type"/>.Name <see cref="BaseEntity{TKey}"/>.Id" as string.
    /// </summary>
    /// <returns></returns>
    public override string ToString() => $"[{GetType().Name} {Id}]";
}
