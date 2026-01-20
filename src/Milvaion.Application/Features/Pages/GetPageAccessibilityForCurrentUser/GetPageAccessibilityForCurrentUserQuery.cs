using Milvaion.Application.Dtos.UIDtos.PageDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Pages.GetPageAccessibilityForCurrentUser;

/// <summary>
/// Data transfer object for page details.
/// </summary>
public record GetPageAccessibilityForCurrentUserQuery : IQuery<PageDto>
{
    /// <summary>
    /// Page name where you want to access the information.
    /// </summary>
    public string PageName { get; set; }
}
