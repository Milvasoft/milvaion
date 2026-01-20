using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ContentManagement.Contents.DeleteContents;

/// <summary>
/// Data transfer object for contents deletion.
/// </summary>
public record DeleteContentsCommand : ICommand<List<int>>
{
    /// <summary>
    /// Id list of the contents to be deleted.
    /// </summary>
    public List<int> ContentIdList { get; set; }
}
