using Milvaion.Application.Dtos.ContentManagementDtos.LanguageDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.Languages.GetLanguageList;

/// <summary>
/// Data transfer object for language list.
/// </summary>
public record GetLanguageListQuery : ListRequest, IListRequestQuery<LanguageDto>
{
}