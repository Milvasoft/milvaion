using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;

namespace Milvasoft.Milvaion.Sdk.Utils;

/// <summary>
/// Logger factory extensions for creating IMilvaLogger instances.
/// </summary>
public static class LoggerFactoryExtensions
{
    /// <summary>
    /// Creates an <see cref="IMilvaLogger"/> instance using the factory.
    /// </summary>
    /// <typeparam name="T">Type to create logger for</typeparam>
    /// <param name="factory">Logger factory</param>
    /// <returns>IMilvaLogger instance</returns>
    public static IMilvaLogger CreateMilvaLogger<T>(this ILoggerFactory factory) => new MilvaionLogger(factory);
}
