using FluentValidation;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ExclusionCalendars.DeleteExclusionCalendar;

/// <summary>
/// Deletes an exclusion calendar.
/// </summary>
public record DeleteExclusionCalendarCommand : ICommand<Guid>
{
    /// <summary>Id of the calendar.</summary>
    public Guid CalendarId { get; set; }
}

/// <summary>
/// Exclusion calendar deletion validations.
/// </summary>
public sealed class DeleteExclusionCalendarCommandValidator : AbstractValidator<DeleteExclusionCalendarCommand>
{
    ///<inheritdoc cref="DeleteExclusionCalendarCommandValidator"/>
    public DeleteExclusionCalendarCommandValidator() => RuleFor(c => c.CalendarId).NotEmpty().WithMessage("Calendar id is required.");
}

/// <summary>
/// Handles exclusion calendar deletion.
/// </summary>
/// <remarks>
/// A calendar still referenced by a job is not deleted. Removing it would make those jobs run on days someone chose
/// to exclude - holidays, maintenance windows - without anyone deciding that. The caller removes it from the jobs
/// first, which makes that decision explicit.
/// </remarks>
/// <param name="CalendarRepository"></param>
/// <param name="JobRepository"></param>
[Log]
[UserActivityTrack(UserActivity.DeleteExclusionCalendar)]
public record DeleteExclusionCalendarCommandHandler(IMilvaionRepositoryBase<ExclusionCalendar> CalendarRepository,
                                                    IMilvaionRepositoryBase<ScheduledJob> JobRepository) : IInterceptable, ICommandHandler<DeleteExclusionCalendarCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<ExclusionCalendar> _calendarRepository = CalendarRepository;
    private readonly IMilvaionRepositoryBase<ScheduledJob> _jobRepository = JobRepository;

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(DeleteExclusionCalendarCommand request, CancellationToken cancellationToken)
    {
        var calendar = await _calendarRepository.GetByIdAsync(request.CalendarId, cancellationToken: cancellationToken);

        if (calendar is null)
            return Response<Guid>.Error(request.CalendarId, "Exclusion calendar not found.");

        var calendarId = request.CalendarId;

        var usedBy = await _jobRepository.GetAllAsync(j => j.ExclusionCalendarIds.Contains(calendarId),
                                                      projection: j => new ScheduledJob { Id = j.Id, DisplayName = j.DisplayName },
                                                      cancellationToken: cancellationToken);

        if (usedBy.Count > 0)
        {
            var names = string.Join(", ", usedBy.Take(5).Select(j => $"'{j.DisplayName}'"));
            var more = usedBy.Count > 5 ? $" and {usedBy.Count - 5} more" : string.Empty;

            return Response<Guid>.Error(request.CalendarId, $"Calendar '{calendar.Name}' is used by {usedBy.Count} job(s): {names}{more}. Remove it from those jobs first.");
        }

        await _calendarRepository.DeleteAsync(calendar, cancellationToken: cancellationToken);

        return Response<Guid>.Success(request.CalendarId);
    }
}
