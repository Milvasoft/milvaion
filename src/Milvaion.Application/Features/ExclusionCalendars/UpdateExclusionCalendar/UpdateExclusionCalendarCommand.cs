using FluentValidation;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ExclusionCalendars.UpdateExclusionCalendar;

/// <summary>
/// Replaces an exclusion calendar's name, description and rules. Every field is written, so send the full calendar.
/// </summary>
public record UpdateExclusionCalendarCommand : ICommand<Guid>, IExclusionCalendarFields
{
    /// <summary>Id of the calendar.</summary>
    public Guid Id { get; set; }

    /// <summary>Unique name of the calendar.</summary>
    public string Name { get; set; }

    /// <summary>What the calendar is for.</summary>
    public string Description { get; set; }

    /// <summary>Single excluded dates (yyyy-MM-dd).</summary>
    public List<DateOnly> Dates { get; set; } = [];

    /// <summary>Excluded date ranges, both ends inclusive.</summary>
    public List<ExclusionDateRange> DateRanges { get; set; } = [];

    /// <summary>Excluded days of the week (0 = Sunday ... 6 = Saturday).</summary>
    public List<int> Weekdays { get; set; } = [];
}

/// <summary>
/// Exclusion calendar update validations.
/// </summary>
public sealed class UpdateExclusionCalendarCommandValidator : AbstractValidator<UpdateExclusionCalendarCommand>
{
    ///<inheritdoc cref="UpdateExclusionCalendarCommandValidator"/>
    public UpdateExclusionCalendarCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty().WithMessage("Calendar id is required.");

        ExclusionCalendarRules.AddFieldRules(this);
    }
}

/// <summary>
/// Handles exclusion calendar updates.
/// </summary>
/// <remarks>
/// Nothing is cached: the dispatcher reads the calendars of due jobs from the database on every cycle, so a change
/// applies from the next run on.
/// </remarks>
/// <param name="CalendarRepository"></param>
[Log]
[UserActivityTrack(UserActivity.UpdateExclusionCalendar)]
public record UpdateExclusionCalendarCommandHandler(IMilvaionRepositoryBase<ExclusionCalendar> CalendarRepository) : IInterceptable, ICommandHandler<UpdateExclusionCalendarCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<ExclusionCalendar> _calendarRepository = CalendarRepository;

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(UpdateExclusionCalendarCommand request, CancellationToken cancellationToken)
    {
        var calendar = await _calendarRepository.GetByIdAsync(request.Id, cancellationToken: cancellationToken);

        if (calendar is null)
            return Response<Guid>.Error(request.Id, "Exclusion calendar not found.");

        var name = request.Name.Trim();
        var lowered = name.ToLower();
        var id = request.Id;

        var nameTaken = await _calendarRepository.GetFirstOrDefaultAsync(c => c.Id != id && c.Name.ToLower() == lowered,
                                                                         projection: c => new ExclusionCalendar { Id = c.Id },
                                                                         cancellationToken: cancellationToken);

        if (nameTaken is not null)
            return Response<Guid>.Error(request.Id, $"A calendar named '{name}' already exists.");

        ExclusionCalendarRules.Apply(request, calendar);

        await _calendarRepository.UpdateAsync(calendar, cancellationToken);

        return Response<Guid>.Success(calendar.Id);
    }
}
