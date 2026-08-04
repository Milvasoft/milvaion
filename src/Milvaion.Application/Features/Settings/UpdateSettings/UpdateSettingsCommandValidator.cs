using FluentValidation;

namespace Milvaion.Application.Features.Settings.UpdateSettings;

/// <summary>
/// Validates the settings update - light bounds so the branding text stays sane.
/// </summary>
public sealed class UpdateSettingsCommandValidator : AbstractValidator<UpdateSettingsCommand>
{
    ///<inheritdoc cref="UpdateSettingsCommandValidator"/>
    public UpdateSettingsCommandValidator()
    {
        When(x => x.Branding is not null, () =>
        {
            RuleFor(x => x.Branding.Title).MaximumLength(100);
            RuleFor(x => x.Branding.Subtitle).MaximumLength(200);
        });
    }
}
