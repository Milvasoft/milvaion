using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.MultiLanguage.EntityBases.Abstract;
using Milvasoft.Core.MultiLanguage.Manager;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.Languages.UpdateLanguage;

/// <summary>
/// Handles the update of the language.
/// </summary>
/// <param name="LanguageRepository"></param>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.UpdateRole)]
public record UpdateLanguageCommandHandler(IMilvaionRepositoryBase<Language> LanguageRepository) : IInterceptable, ICommandHandler<UpdateLanguageCommand, int>
{
    private readonly IMilvaionRepositoryBase<Language> _languageRepository = LanguageRepository;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(UpdateLanguageCommand request, CancellationToken cancellationToken)
    {
        if (request.IsDefault.IsUpdated)
        {
            if (request.IsDefault.Value)
            {
                var languages = await _languageRepository.GetAllAsync(l => l.IsDefault, cancellationToken: cancellationToken);

                foreach (var language in languages)
                {
                    language.IsDefault = false;
                }

                await _languageRepository.BulkUpdateAsync(languages, null, cancellationToken);
            }
            else
                request.IsDefault = new UpdateProperty<bool>();
        }

        var setPropertyBuilder = _languageRepository.GetUpdatablePropertiesBuilder(request);

        await _languageRepository.ExecuteUpdateAsync(request.Id, setPropertyBuilder, cancellationToken: cancellationToken);

        var latestLanguages = await _languageRepository.GetAllAsync(cancellationToken: cancellationToken);

        var languageSeed = latestLanguages.Cast<ILanguage>().ToList();

        MultiLanguageManager.UpdateLanguagesList(languageSeed);

        return Response<int>.Success(request.Id);
    }
}
