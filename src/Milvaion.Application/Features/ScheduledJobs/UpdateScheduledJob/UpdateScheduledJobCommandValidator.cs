using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;
using System.Text.Json;

namespace Milvaion.Application.Features.ScheduledJobs.UpdateScheduledJob;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class UpdateScheduledJobCommandValidator : AbstractValidator<UpdateScheduledJobCommand>
{
    ///<inheritdoc cref="UpdateScheduledJobCommandValidator"/>
    public UpdateScheduledJobCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.Id)
            .Must(id => Guid.Empty != id)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Job]]);

        RuleFor(query => query.DisplayName)
            .NotNullOrEmpty(localizer, MessageKey.GlobalName)
            .When(q => q.DisplayName.IsUpdated);

        //  Validate CronExpression format if updated
        RuleFor(query => query.CronExpression.Value)
            .Must(BeValidCronExpression)
            .WithMessage(localizer[MessageKey.InvalidCronExpression])
            .When(q => q.CronExpression.IsUpdated && !string.IsNullOrWhiteSpace(q.CronExpression.Value));

        //  Validate JobData is valid JSON if updated
        RuleFor(query => query.JobData.Value)
            .Must(BeValidJson)
            .WithMessage(localizer[MessageKey.InvalidJobData])
            .When(q => q.JobData.IsUpdated && !string.IsNullOrWhiteSpace(q.JobData.Value));
    }

    private static bool BeValidCronExpression(string cronExpression)
    {
        try
        {
            var parsed = Cronos.CronExpression.Parse(cronExpression, Cronos.CronFormat.IncludeSeconds);

            var nextRun = parsed.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);

            return nextRun.HasValue; // Must have at least one future occurrence
        }
        catch
        {
            return false;
        }
    }

    private static bool BeInFuture(DateTime dateTime) => dateTime > DateTime.UtcNow;

    private static bool BeValidJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return true;

        try
        {
            JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}