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
            .NotNullOrEmpty(localizer, MessageKey.GlobalName);

        // Validate: At least one of CronExpression or ExecuteAt must be provided
        RuleFor(query => query)
            .Must(cmd => !string.IsNullOrWhiteSpace(cmd.CronExpression) || cmd.ExecuteAt != default)
            .WithMessage(localizer[MessageKey.CronOrExecuteAtRequired]);

        // Validate CronExpression format (if provided)
        RuleFor(query => query.CronExpression)
            .Must(BeValidCronExpression)
            .WithMessage(localizer[MessageKey.InvalidCronExpression])
            .When(q => !string.IsNullOrWhiteSpace(q.CronExpression));

        // Validate ExecuteAt is in future (if provided and no cron)
        RuleFor(query => query.ExecuteAt)
            .Must(date => date > DateTime.UtcNow)
            .WithMessage(localizer[MessageKey.ExecuteAtMustBeInFuture])
            .When(q => string.IsNullOrWhiteSpace(q.CronExpression) && q.ExecuteAt != default);

        // Validate JobData is valid JSON (if provided)
        RuleFor(query => query.JobData)
            .Must(BeValidJson)
            .WithMessage(localizer[MessageKey.InvalidJobData])
            .When(q => !string.IsNullOrWhiteSpace(q.JobData));

        // Validate cron expression
        RuleFor(x => x.CronExpression)
            .Must(BeValidCronExpression)
            .When(x => !string.IsNullOrEmpty(x.CronExpression))
            .WithMessage("Invalid cron expression");

        //// Prevent too frequent recurring jobs
        //RuleFor(x => x.CronExpression)
        //    .Must(NotBeMoreFrequentThan1Minute)
        //    .When(x => !string.IsNullOrEmpty(x.CronExpression))
        //    .WithMessage("Recurring jobs cannot run more frequently than once per minute");

        RuleFor(x => x.ExecuteAt)
            .LessThan(DateTime.UtcNow.AddYears(1))
            .WithMessage("Job execution time cannot be more than 1 year in the future");
    }
    private bool BeValidCronExpression(string cronExpression)
    {
        try
        {
            CronExpression.Parse(cronExpression, CronFormat.IncludeSeconds);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool NotBeMoreFrequentThan1Minute(string cronExpression)
    {
        try
        {
            var cron = CronExpression.Parse(cronExpression, CronFormat.IncludeSeconds);
            var now = DateTime.UtcNow;
            var next1 = cron.GetNextOccurrence(now, TimeZoneInfo.Utc);
            var next2 = cron.GetNextOccurrence(next1.Value, TimeZoneInfo.Utc);

            if (!next1.HasValue || !next2.HasValue)
                return true;

            var interval = next2.Value - next1.Value;
            return interval >= TimeSpan.FromMinutes(1);
        }
        catch
        {
            return false;
        }
    }

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