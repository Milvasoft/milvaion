using Microsoft.Extensions.DependencyInjection;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Components.Rest.OptionsDataFetcher;
using Milvasoft.Core.Helpers;
using Milvasoft.Core.Utils.Constants;
using Milvasoft.DataAccess.EfCore.Utils.LookupModels;

namespace Milvaion.Application.Utils.LinkedWithFormatters;

/// <summary>
/// Lookup options fetcher.
/// </summary>
/// <param name="serviceProvider"></param>
public class LookupFetcher(IServiceProvider serviceProvider) : IOptionsDataFetcher
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    /// <inheritdoc/>
    public const string FetcherName = "LookupFetcher";

    /// <inheritdoc/>
    public bool IsAsync { get; set; } = true;

    /// <inheritdoc/>
    public List<object> Fetch(object optionalData = null) => throw new NotImplementedException();

    /// <inheritdoc/>
    public async Task<List<object>> FetchAsync(object optionalData = null)
    {
        var entityName = optionalData as string;

        var lookupService = _serviceProvider.GetService<ILookupService>();

        var lookupRequest = new LookupRequest
        {
            Parameters =
            [
                new()
                {
                    EntityName = entityName,
                    RequestedPropertyNames = [EntityPropertyNames.Id, nameof(NameIntNavigationDto.Name)]
                }
            ]
        };

        var result = await lookupService.GetLookupsAsync(lookupRequest);

        if (!result.IsNullOrEmpty())
        {
            var resultAsLookupResult = result.Cast<LookupResult>().FirstOrDefault();

            var optionLookupModels = resultAsLookupResult?.Data.Select(i => new OptionLookupModel
            {
                Value = ((Dictionary<string, object>)i)[EntityPropertyNames.Id],
                Name = ((Dictionary<string, object>)i)[nameof(OptionLookupModel.Name)].ToString(),
            });

            return [.. optionLookupModels];
        }

        return [];
    }
}
