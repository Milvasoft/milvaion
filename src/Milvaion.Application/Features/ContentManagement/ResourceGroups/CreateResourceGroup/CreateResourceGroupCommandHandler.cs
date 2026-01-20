using Mapster;
using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ContentManagement.ResourceGroups.CreateResourceGroup;

/// <summary>
/// Handles the creation of the resource group.
/// </summary>
/// <param name="ResourceGroupRepository"></param>
[Log]
[UserActivityTrack(UserActivity.CreateResourceGroup)]
public record CreateResourceGroupCommandHandler(IMilvaionRepositoryBase<ResourceGroup> ResourceGroupRepository) : IInterceptable, ICommandHandler<CreateResourceGroupCommand, int>
{
    private readonly IMilvaionRepositoryBase<ResourceGroup> _contentResourceGroupRepository = ResourceGroupRepository;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(CreateResourceGroupCommand request, CancellationToken cancellationToken)
    {
        var resourceGroup = request.Adapt<ResourceGroup>();

        resourceGroup.Slug = request.Name.ToLowerAndNonSpacingUnicode();

        await _contentResourceGroupRepository.AddAsync(resourceGroup, cancellationToken);

        return Response<int>.Success(resourceGroup.Id);
    }
}
