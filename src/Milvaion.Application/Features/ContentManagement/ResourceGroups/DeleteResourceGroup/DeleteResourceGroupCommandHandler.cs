using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ContentManagement.ResourceGroups.DeleteResourceGroup;

/// <summary>
/// Handles the deletion of the resource group.
/// </summary>
/// <param name="ResourceGroupRepository"></param>
[Log]
[UserActivityTrack(UserActivity.DeleteResourceGroup)]
public record DeleteResourceGroupCommandHandler(IMilvaionRepositoryBase<ResourceGroup> ResourceGroupRepository) : IInterceptable, ICommandHandler<DeleteResourceGroupCommand, int>
{
    private readonly IMilvaionRepositoryBase<ResourceGroup> _contentResourceGroupRepository = ResourceGroupRepository;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(DeleteResourceGroupCommand request, CancellationToken cancellationToken)
    {
        var contentResourceGroup = await _contentResourceGroupRepository.GetForDeleteAsync(request.ResourceGroupId, cancellationToken: cancellationToken);

        if (contentResourceGroup == null)
            return Response<int>.Error(0, MessageKey.ResourceGroupNotFound);

        await _contentResourceGroupRepository.DeleteAsync(contentResourceGroup, cancellationToken: cancellationToken);

        return Response<int>.Success(request.ResourceGroupId);
    }
}
