using Serilog.Core;
using Serilog.Events;

namespace Milvaion.Infrastructure.Logging;

/// <summary>
/// Removes _typeTag from Serilog's output.
/// </summary>
public class RemoveTypeTagEnricher : ILogEventEnricher
{
    /// <summary>
    /// Removes _typeTag from Serilog's output.
    /// </summary>
    /// <param name="logEvent"></param>
    /// <param name="propertyFactory"></param>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var pair in logEvent.Properties)
            if (pair.Value is StructureValue structureValue)
                logEvent.AddOrUpdateProperty(new LogEventProperty(pair.Key, RemoveTypeTags(structureValue)));
    }

    /// <summary>
    /// Removes _typeTag from Serilog's output.
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public static LogEventPropertyValue RemoveTypeTags(LogEventPropertyValue value)
    {
        if (value is StructureValue sv)
            return new StructureValue(sv.Properties.Select(p => new LogEventProperty(p.Name, RemoveTypeTags(p.Value))));

        if (value is SequenceValue qv)
            return new SequenceValue(qv.Elements.Select(RemoveTypeTags));

        return value;
    }
}