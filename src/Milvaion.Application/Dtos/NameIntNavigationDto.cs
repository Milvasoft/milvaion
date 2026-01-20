using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete;

namespace Milvaion.Application.Dtos;

/// <summary>
/// It can be used to obtain Id-Name pair information for any domain.
/// </summary>
[Translate]
public class NameIntNavigationDto : BaseDto<int>
{
    /// <summary>
    /// Name of related domain.
    /// </summary>
    public string Name { get; set; }
}
