using FluentValidation;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ExclusionCalendars.CreateExclusionCalendar;

/// <summary>
/// Creates an exclusion calendar.
/// </summary>
public record CreateExclusionCalendarCommand : ICommand<Guid>, IExclusionCalendarFields
{
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
/// Exclusion calendar creation validations.
/// </summary>
public sealed class CreateExclusionCalendarCommandValidator : AbstractValidator<CreateExclusionCalendarCommand>
{
    ///<inheritdoc cref="CreateExclusionCalendarCommandValidator"/>
    public CreateExclusionCalendarCommandValidator() => ExclusionCalendarRules.AddFieldRules(this);
}

/// <summary>
/// Handles exclusion calendar creation.
/// </summary>
/// <param name="CalendarRepository"></param>
[Log]
[UserActivityTrack(UserActivity.CreateExclusionCalendar)]
public record CreateExclusionCalendarCommandHandler(IMilvaionRepositoryBase<ExclusionCalendar> CalendarRepository) : IInterceptable, ICommandHandler<CreateExclusionCalendarCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<ExclusionCalendar> _calendarRepository = CalendarRepository;

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(CreateExclusionCalendarCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        var lowered = name.ToLower();

        var nameTaken = await _calendarRepository.GetFirstOrDefaultAsync(c => c.Name.ToLower() == lowered,
                                                                         projection: c => new ExclusionCalendar { Id = c.Id },
                                                                         cancellationToken: cancellationToken);

        if (nameTaken is not null)
            return Response<Guid>.Error(default, $"A calendar named '{name}' already exists.");

        var calendar = new ExclusionCalendar { Id = Guid.CreateVersion7() };

        ExclusionCalendarRules.Apply(request, calendar);

        await _calendarRepository.AddAsync(calendar, cancellationToken);

        return Response<Guid>.Success(calendar.Id);
    }
}
