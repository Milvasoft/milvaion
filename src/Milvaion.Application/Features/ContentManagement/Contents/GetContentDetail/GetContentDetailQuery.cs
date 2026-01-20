using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.ContentManagement.Contents.GetContentDetail;

/// <summary>
/// Data transfer object for content details.
/// </summary>
public record GetContentDetailQuery : IQuery<ContentDetailDto>
{
    /// <summary>
    /// Resource group id to access details.
    /// </summary>
    public int ContentId { get; set; }
}
