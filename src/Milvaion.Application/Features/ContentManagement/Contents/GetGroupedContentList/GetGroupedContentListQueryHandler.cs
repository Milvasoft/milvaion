using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore.Bulk;
using Milvasoft.DataAccess.EfCore.Utils;

namespace Milvaion.Application.Features.ContentManagement.Contents.GetGroupedContentList;

/// <summary>
/// Handles the content list operation.
/// </summary>
/// <param name="dbContextAccessor"></param>
public class GetGroupedContentListQueryHandler(IMilvaionDbContextAccessor dbContextAccessor) : IInterceptable, IListQueryHandler<GetGroupedContentListQuery, GroupedContentListDto>
{
    private readonly IMilvaBulkDbContextBase _dbContext = dbContextAccessor.GetDbContext();

    /// <inheritdoc/>
    public async Task<ListResponse<GroupedContentListDto>> Handle(GetGroupedContentListQuery request, CancellationToken cancellationToken)
    {
        var query = _dbContext.Set<Content>()
                              .AsNoTrackingWithIdentityResolution()
                              .WithFiltering(request.Filtering)
                              .GroupBy(p => p.KeyAlias)
                              .Select(grouped => new GroupedContentListDto
                              {
                                  IdList = grouped.Select(p => p.Id).ToList(),
                                  Key = grouped.First().Key,
                                  NamespaceSlug = grouped.First().NamespaceSlug,
                                  ResourceGroupSlug = grouped.First().ResourceGroupSlug,
                                  KeyAlias = grouped.Key,
                                  Namespace = new NameIntNavigationDto
                                  {
                                      Id = grouped.First().NamespaceId,
                                      Name = grouped.First().Namespace.Name
                                  },
                                  ResourceGroup = new NameIntNavigationDto
                                  {
                                      Id = grouped.First().ResourceGroupId,
                                      Name = grouped.First().ResourceGroup.Name
                                  }
                              })
                              .WithSorting(request.Sorting);

        var response = ListResponse<GroupedContentListDto>.Success();

        if (request.PageNumber.HasValue && request.RowCount.HasValue)
        {
            response.TotalDataCount = await query.CountAsync(cancellationToken);
            response.TotalPageCount = request.CalculatePageCountAndCompareWithRequested(response.TotalDataCount);
            response.CurrentPageNumber = request.PageNumber.Value;

            query = query.Skip((request.PageNumber.Value - 1) * request.RowCount.Value).Take(request.RowCount.Value);
        }

        var result = await query.ToListAsync(cancellationToken);

        response.Data = result;

        return response;
    }
}
