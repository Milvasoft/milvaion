using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ContentManagement.Contents.CreateContent;

/// <summary>
/// Data transfer object for content creation.
/// </summary>
public record CreateContentCommand : CreateContentDto, ICommand<int>
{
}