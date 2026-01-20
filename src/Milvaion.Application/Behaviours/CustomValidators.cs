using FluentValidation;
using FluentValidation.Validators;
using Milvaion.Application.Dtos.TranslationDtos;
using Milvasoft.Core.Abstractions.Localization;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Behaviours;

/// <summary>
/// Default data validator.
/// </summary>
public class DefaultDataValidator<T>(int rangeMin = 0, int rangeMax = 21) : PropertyValidator<T, int>
{
    /// <inheritdoc/>
    public override string Name => "DefaultDataValidator";

    /// <inheritdoc/>
    protected override string GetDefaultMessageTemplate(string errorCode) => "{PropertyName} should not less than 21.";

    /// <inheritdoc/>
    public override bool IsValid(ValidationContext<T> context, int value) => value <= rangeMin || value >= rangeMax;
}

/// <summary>
/// Default data validator.
/// </summary>
public class DefaultDataLongValidator<T>(long rangeMin = 0, long rangeMax = GlobalConstant.AutoIncrementStart) : PropertyValidator<T, long>
{
    /// <inheritdoc/>
    public override string Name => "DefaultDataValidator";

    /// <inheritdoc/>
    protected override string GetDefaultMessageTemplate(string errorCode) => "{PropertyName} should not less than " + GlobalConstant.AutoIncrementStart + ".";

    /// <inheritdoc/>
    public override bool IsValid(ValidationContext<T> context, long value) => value <= rangeMin || value >= rangeMax;
}

/// <summary>
/// Feature query validations. 
/// </summary>
public sealed class NameDescriptionTranslationDtoValidator : AbstractValidator<NameDescriptionTranslationDto>
{
    ///<inheritdoc cref="NameDescriptionTranslationDtoValidator"/>
    public NameDescriptionTranslationDtoValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.Name)
            .NotNullOrEmpty(localizer, MessageKey.GlobalName)
            .MaximumLength(100)
            .WithMessage(localizer[MessageKey.MaxLengthReached, localizer[MessageKey.GlobalName], 100]);

        RuleFor(query => query.Description)
            .MaximumLength(5000)
            .WithMessage(localizer[MessageKey.MaxLengthReached, localizer[MessageKey.GlobalDescription], 5000]);

        RuleFor(query => query.LanguageId)
            .NotBeDefaultData()
            .WithMessage(localizer[MessageKey.DefaultValueCannotModify])
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Language]]);
    }
}

/// <summary>
/// Feature query validations. 
/// </summary>
public sealed class UpdateNameDescriptionTranslationDtoValidator : AbstractValidator<UpdateProperty<List<NameDescriptionTranslationDto>>>
{
    ///<inheritdoc cref="UpdateNameDescriptionTranslationDtoValidator"/>
    public UpdateNameDescriptionTranslationDtoValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query)
            .NotNullOrEmpty(localizer, MessageKey.GlobalName)
            .When(q => q.IsUpdated);

        RuleForEach(query => query.Value)
            .SetValidator(new NameDescriptionTranslationDtoValidator(localizer))
            .When(query => query.IsUpdated);
    }
}