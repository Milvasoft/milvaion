using Milvasoft.Core.EntityBases.Abstract;

namespace Milvaion.Application.Dtos;

/// <summary>
/// Represents an audit data transfer object.
/// </summary>
public record AuditDto<TKey> where TKey : struct, IEquatable<TKey>
{
    /// <summary>
    /// Gets or sets the creation date.
    /// </summary>
    public DateTime? CreationDate { get; set; }

    /// <summary>
    /// Gets or sets the username of the creator.
    /// </summary>
    public string CreatorUserName { get; set; }

    /// <summary>
    /// Gets or sets the last modification date.
    /// </summary>
    public DateTime? LastModificationDate { get; set; }

    /// <summary>
    /// Gets or sets the username of the last modifier.
    /// </summary>
    public string LastModifierUserName { get; set; }

    /// <summary>
    /// Assigns the values of the entity to the properties of the data transfer object.
    /// </summary>
    public AuditDto()
    {

    }

    /// <summary>
    /// Assigns the values of the entity to the properties of the data transfer object.
    /// </summary>
    /// <param name="entity"></param>
    public AuditDto(IAuditable<TKey> entity)
    {
        CreatorUserName = entity.CreatorUserName;
        CreationDate = entity.CreationDate;
        LastModifierUserName = entity.LastModifierUserName;
        LastModificationDate = entity.LastModificationDate;
    }

    /// <summary>
    /// Assigns the values of the entity to the properties of the data transfer object.
    /// </summary>
    /// <param name="entity"></param>
    public AuditDto(ICreationAuditable<TKey> entity)
    {
        CreatorUserName = entity.CreatorUserName;
        CreationDate = entity.CreationDate;
    }
}
