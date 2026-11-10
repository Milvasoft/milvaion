using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.WorkflowDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using System.Linq.Expressions;

namespace Milvaion.Application.Features.Workflows.GetWorkflowList;

/// <summary>
/// Handles the workflow list query.
/// </summary>
public class GetWorkflowListQueryHandler(IMilvaionRepositoryBase<Workflow> workflowRepository) : IInterceptable, IListQueryHandler<GetWorkflowListQuery, WorkflowListDto>
{
    private readonly IMilvaionRepositoryBase<Workflow> _workflowRepository = workflowRepository;

    /// <inheritdoc/>
    public async Task<ListResponse<WorkflowListDto>> Handle(GetWorkflowListQuery request, CancellationToken cancellationToken)
    {
        Expression<Func<Workflow, bool>> predicate = null;

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            if (Guid.TryParse(request.SearchTerm, out var guid))
            {
                predicate = w => w.Id == guid;
            }
            else
            {
                string searchTerm = request.SearchTerm.ToContainsPattern();
                predicate = w => EF.Functions.ILike(w.Name, searchTerm, MilvaionExtensions.LikeEscapeCharacter) ||
                                 EF.Functions.ILike(w.Tags, searchTerm, MilvaionExtensions.LikeEscapeCharacter);
            }
        }

        var response = await _workflowRepository.GetAllAsync(request, condition: predicate, projection: WorkflowListDto.Projection, cancellationToken: cancellationToken);

        return response;
    }
}
