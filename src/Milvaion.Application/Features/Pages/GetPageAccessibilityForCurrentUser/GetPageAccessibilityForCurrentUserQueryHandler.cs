using Milvaion.Application.Dtos.UIDtos.PageDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Pages.GetPageAccessibilityForCurrentUser;

/// <summary>
/// Handles the page detail operation.
/// </summary>
/// <param name="uiService"></param>
public class GetPageAccessibilityForCurrentUserQueryHandler(IUIService uiService) : IInterceptable, IQueryHandler<GetPageAccessibilityForCurrentUserQuery, PageDto>
{
    private readonly IUIService _uiService = uiService;

    /// <inheritdoc/>
    public async Task<Response<PageDto>> Handle(GetPageAccessibilityForCurrentUserQuery request, CancellationToken cancellationToken)
    {
        var pageDto = await _uiService.GetCurrentUserPageAccessibilityAsync(request.PageName, cancellationToken);

        if (pageDto == null)
            return Response<PageDto>.Success(pageDto, MessageKey.PageNotFound, MessageType.Warning);

        return Response<PageDto>.Success(pageDto);
    }
}
