using Milvaion.Application.Dtos.ContentManagementDtos.ResourceGroupDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.ContentManagement.ResourceGroups.GetResourceGroupList;

/// <summary>
/// Data transfer object for resource group list.
/// </summary>
public record GetResourceGroupListQuery : ListRequest, IListRequestQuery<ResourceGroupListDto>
{
}