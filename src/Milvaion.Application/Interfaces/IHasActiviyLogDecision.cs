namespace Milvaion.Application.Interfaces;

/// <summary>
/// Indicates that the implementing class has a decision on whether to log the activity or not. Classes implementing this interface can control the logging behavior of their activities by setting the <see cref="ShouldLogActivity"/> property.
/// </summary>
public interface IHasActiviyLogDecision
{
    /// <summary>
    /// Indicates whether the activity should be logged or not. If true, the activity will be logged; if false, it will not be logged.
    /// </summary>
    public bool ShouldLogActivity { get; set; }
}
