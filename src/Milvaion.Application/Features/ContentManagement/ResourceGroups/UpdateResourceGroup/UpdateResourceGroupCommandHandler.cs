using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ContentManagement.ResourceGroups.UpdateResourceGroup;

/// <summary>
/// Handles the update of the resource group.
/// </summary>
/// <param name="ResourceGroupRepository"></param>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.UpdateResourceGroup)]
public record UpdateResourceGroupCommandHandler(IMilvaionRepositoryBase<ResourceGroup> ResourceGroupRepository) : IInterceptable, ICommandHandler<UpdateResourceGroupCommand, int>
{
    private readonly IMilvaionRepositoryBase<ResourceGroup> _resourceGroupRepository = ResourceGroupRepository;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(UpdateResourceGroupCommand request, CancellationToken cancellationToken)
    {
        var setPropertyBuilder = _resourceGroupRepository.GetUpdatablePropertiesBuilder(request);

        await _resourceGroupRepository.ExecuteUpdateAsync(request.Id, setPropertyBuilder, cancellationToken: cancellationToken);

        return Response<int>.Success(request.Id);
    }
}
