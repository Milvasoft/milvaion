using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvaion.Application.Utils.Scheduling;
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

        RuleFor(x => x.TimeZoneId.Value)
            .Must(ZonedCron.IsValidTimeZoneId)
            .WithMessage(x => SchedulingValidation.InvalidTimeZoneMessage(x.TimeZoneId.Value))
            .When(x => x.TimeZoneId.IsUpdated && !string.IsNullOrEmpty(x.TimeZoneId.Value));

        RuleFor(x => x.MissedRunPolicy.Value)
            .IsInEnum()
            .WithMessage(SchedulingValidation.InvalidMissedRunPolicyMessage)
            .When(x => x.MissedRunPolicy.IsUpdated);

        RuleFor(x => x.MaxCatchUpRuns.Value)
            .InclusiveBetween(1, SchedulingValidation.MaxCatchUpRunsLimit)
            .WithMessage(SchedulingValidation.InvalidMaxCatchUpRunsMessage)
            .When(x => x.MaxCatchUpRuns.IsUpdated);

        RuleFor(x => x.Priority.Value)
            .InclusiveBetween(ScheduledJob.MinPriority, ScheduledJob.MaxPriority)
            .WithMessage(SchedulingValidation.InvalidPriorityMessage)
            .When(x => x.Priority.IsUpdated);

        RuleFor(x => x.ExclusionCalendarIds.Value)
            .Must(ids => ids is null || ids.Count <= SchedulingValidation.MaxCalendarsPerJob)
            .WithMessage(SchedulingValidation.TooManyCalendarsMessage)
            .When(x => x.ExclusionCalendarIds.IsUpdated);

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