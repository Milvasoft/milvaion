using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ContentManagement.Namespaces.UpdateNamespace;

/// <summary>
/// Handles the update of the contentNamespace.
/// </summary>
/// <param name="NamespaceRepository"></param>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.UpdateNamespace)]
public record UpdateNamespaceCommandHandler(IMilvaionRepositoryBase<Namespace> NamespaceRepository) : IInterceptable, ICommandHandler<UpdateNamespaceCommand, int>
{
    private readonly IMilvaionRepositoryBase<Namespace> _contentNamespaceRepository = NamespaceRepository;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(UpdateNamespaceCommand request, CancellationToken cancellationToken)
    {
        var setPropertyBuilder = _contentNamespaceRepository.GetUpdatablePropertiesBuilder(request);

        await _contentNamespaceRepository.ExecuteUpdateAsync(request.Id, setPropertyBuilder, cancellationToken: cancellationToken);

        return Response<int>.Success(request.Id);
    }
}
