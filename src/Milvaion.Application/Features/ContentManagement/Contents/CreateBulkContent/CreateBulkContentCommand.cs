using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ContentManagement.Contents.CreateBulkContent;

/// <summary>
/// Data transfer object for contents creation.
/// </summary>
public record CreateBulkContentCommand : ICommand
{
    /// <summary>
    /// List of content creation objects.
    /// </summary>
    public List<CreateContentDto> Contents { get; init; }
}
