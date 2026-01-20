using Milvaion.Application.Dtos.ContentManagementDtos.LanguageDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Languages.GetLanguageList;

/// <summary>
/// Handles the language list operation.
/// </summary>
/// <param name="languageRepository"></param>
public class GetLanguageListQueryHandler(IMilvaionRepositoryBase<Language> languageRepository) : IInterceptable, IListQueryHandler<GetLanguageListQuery, LanguageDto>
{
    private readonly IMilvaionRepositoryBase<Language> _languageRepository = languageRepository;

    /// <inheritdoc/>
    public async Task<ListResponse<LanguageDto>> Handle(GetLanguageListQuery request, CancellationToken cancellationToken)
    {
        var response = await _languageRepository.GetAllAsync(request, projection: LanguageDto.Projection, cancellationToken: cancellationToken);

        return response;
    }
}
