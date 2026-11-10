using Cronos;
using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;
using System.Text.Json;

namespace Milvaion.Application.Features.ScheduledJobs.CreateScheduledJob;

/// <summary>
/// Validator for CreateScheduledJobCommand.
/// </summary>
public sealed class CreateScheduledJobCommandValidator : AbstractValidator<CreateScheduledJobCommand>
{
    ///<inheritdoc cref="CreateScheduledJobCommandValidator"/>
    public CreateScheduledJobCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.DisplayName)
            .NotNullOrEmpty(localizer, MessageKey.GlobalName)
            .When(q => !q.IsExternal);

        // Validate: At least one of CronExpression or ExecuteAt must be provided
        RuleFor(query => query)
            .Must(cmd => !string.IsNullOrWhiteSpace(cmd.CronExpression) || cmd.ExecuteAt != default)
            .WithMessage(localizer[MessageKey.CronOrExecuteAtRequired])
            .When(q => !q.IsExternal);

        // Validate CronExpression format (if provided)
        RuleFor(query => query.CronExpression)
            .Must(BeValidCronExpression)
            .WithMessage(localizer[MessageKey.InvalidCronExpression])
            .When(q => !q.IsExternal && !string.IsNullOrWhiteSpace(q.CronExpression));

        // A valid expression can still have no future occurrence (e.g. a year that has passed).
        RuleFor(query => query.CronExpression)
            .Must(HaveFutureOccurrence)
            .WithMessage(localizer[MessageKey.InvalidCronExpression])
            .When(q => !q.IsExternal && !string.IsNullOrWhiteSpace(q.CronExpression) && BeValidCronExpression(q.CronExpression));

        // Validate ExecuteAt is in future (if provided and no cron)
        RuleFor(query => query.ExecuteAt)
            .Must(date => date > DateTime.UtcNow)
            .WithMessage(localizer[MessageKey.ExecuteAtMustBeInFuture])
            .When(q => !q.IsExternal && string.IsNullOrWhiteSpace(q.CronExpression) && q.ExecuteAt != default);

        // Validate JobData is valid JSON (if provided)
        RuleFor(query => query.JobData)
            .Must(BeValidJson)
            .WithMessage(localizer[MessageKey.InvalidJobData])
            .When(q => !q.IsExternal && !string.IsNullOrWhiteSpace(q.JobData));

        RuleFor(x => x.ExecuteAt)
            .Must(date => date < DateTime.UtcNow.AddYears(1))
            .WithMessage("Job execution time cannot be more than 1 year in the future")
            .When(q => !q.IsExternal);
    }
    private static bool BeValidCronExpression(string cronExpression) => CronExpression.TryParse(cronExpression, CronFormat.IncludeSeconds, out _);

    private static bool HaveFutureOccurrence(string cronExpression)
        => CronExpression.Parse(cronExpression, CronFormat.IncludeSeconds).GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc).HasValue;

    private static bool BeValidJson(string jobData)
    {
        if (string.IsNullOrWhiteSpace(jobData))
            return true;

        try
        {
            JsonDocument.Parse(jobData);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}