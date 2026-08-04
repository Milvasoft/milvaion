using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Settings.UpdateSettings;

/// <summary>
/// Handles the settings update. Persists via the provider, which also refreshes this instance's
/// cache and notifies the others.
/// </summary>
/// <param name="settingsProvider"></param>
[Log]
public class UpdateSettingsCommandHandler(ISettingsProvider settingsProvider) : IInterceptable, ICommandHandler<UpdateSettingsCommand, bool>
{
    private readonly ISettingsProvider _settingsProvider = settingsProvider;

    /// <inheritdoc/>
    public async Task<Response<bool>> Handle(UpdateSettingsCommand request, CancellationToken cancellationToken)
    {
        var current = await _settingsProvider.GetAsync(cancellationToken);

        var updated = new AppSettingsDocument
        {
            Branding = new BrandingSettings
            {
                Title = request.Branding?.Title ?? current.Branding?.Title,
                Subtitle = request.Branding?.Subtitle ?? current.Branding?.Subtitle
            },
            Notifications = request.Notifications ?? current.Notifications
        };

        await _settingsProvider.UpdateAsync(updated, cancellationToken);

        return Response<bool>.Success(true);
    }
}
