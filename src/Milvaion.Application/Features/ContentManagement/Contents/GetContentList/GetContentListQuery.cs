using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.ContentManagement.Contents.GetContentList;

/// <summary>
/// Data transfer object for content list.
/// </summary>
public record GetContentListQuery : ListRequest, IListRequestQuery<ContentListDto>
{
}