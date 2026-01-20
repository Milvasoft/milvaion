using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ContentManagement.ResourceGroups.DeleteResourceGroup;

/// <summary>
/// Data transfer object for resource group deletion.
/// </summary>
public record DeleteResourceGroupCommand : ICommand<int>
{
    /// <summary>
    /// Id of the resource group to be deleted.
    /// </summary>
    public int ResourceGroupId { get; set; }
}
