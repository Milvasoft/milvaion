using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.ContentManagement.Contents.GetGroupedContentList;

/// <summary>
/// Data transfer object for content list.
/// </summary>
public record GetGroupedContentListQuery : ListRequest, IListRequestQuery<GroupedContentListDto>
{
}