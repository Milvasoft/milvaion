namespace Milvaion.Domain.JsonModels;

/// <summary>
/// An inclusive range of calendar dates.
/// </summary>
public class ExclusionDateRange
{
    /// <summary>
    /// First excluded date.
    /// </summary>
    public DateOnly Start { get; set; }

    /// <summary>
    /// Last excluded date. Equal to <see cref="Start"/> for a single day.
    /// </summary>
    public DateOnly End { get; set; }

    /// <summary>
    /// Optional label, e.g. "Christmas shutdown".
    /// </summary>
    public string Label { get; set; }

    /// <summary>
    /// Returns true when <paramref name="date"/> falls inside the range.
    /// </summary>
    public bool Contains(DateOnly date) => date >= Start && date <= End;
}
