using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Attributes;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Milvasoft.Milvaion.Sdk.Worker.Utils;

/// <summary>
/// Helper class to extract job data type information from job implementations.
/// Used during auto-discovery to send job data schema to scheduler.
/// </summary>
public static class JobDataTypeHelper
{
    private static readonly Type[] _genericJobInterfaces =
    [
        typeof(IJob<>),
        typeof(IJobWithResult<>),
        typeof(IAsyncJob<>),
        typeof(IAsyncJobWithResult<>)
    ];

    /// <summary>
    /// Dynamic enum values storage. Key is configuration key, value is list of allowed values.
    /// Populated at startup from worker configuration.
    /// </summary>
    private static readonly Dictionary<string, List<string>> _dynamicEnumValues = [];

    /// <summary>
    /// Registers dynamic enum values for a configuration key.
    /// These values will be injected into the JSON schema for properties marked with [DynamicEnum].
    /// </summary>
    /// <param name="configurationKey">Configuration key (e.g., "SqlExecutorConfig:Connections")</param>
    /// <param name="values">List of allowed values</param>
    public static void RegisterDynamicEnumValues(string configurationKey, IEnumerable<string> values) => _dynamicEnumValues[configurationKey] = [.. values];

    /// <summary>
    /// Gets registered dynamic enum values for a configuration key.
    /// </summary>
    public static IReadOnlyList<string> GetDynamicEnumValues(string configurationKey) => _dynamicEnumValues.TryGetValue(configurationKey, out var values) ? values : [];

    /// <summary>
    /// Gets the JobData type from a job type if it implements a generic job interface.
    /// </summary>
    /// <param name="jobType">The job implementation type</param>
    /// <returns>The JobData type, or null if the job doesn't use typed JobData</returns>
    public static Type GetJobDataType(Type jobType)
    {
        var interfaces = jobType.GetInterfaces();

        foreach (var iface in interfaces)
        {
            if (!iface.IsGenericType)
                continue;

            var genericDef = iface.GetGenericTypeDefinition();

            if (_genericJobInterfaces.Contains(genericDef))
            {
                return iface.GetGenericArguments()[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Generates a JSON schema representation of the JobData type.
    /// This schema can be sent to the scheduler and displayed in the UI.
    /// </summary>
    /// <param name="jobDataType">The JobData type to generate schema for</param>
    /// <returns>A dictionary representing the JSON schema</returns>
    public static Dictionary<string, object> GenerateSchema(Type jobDataType)
    {
        if (jobDataType == null)
            return null;

        var schema = new Dictionary<string, object>
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["type"] = "object",
            ["title"] = jobDataType.Name,
            ["properties"] = GenerateProperties(jobDataType),
            ["required"] = GetRequiredProperties(jobDataType)
        };

        return schema;
    }

    /// <summary>
    /// Generates a JSON schema string for the JobData type.
    /// </summary>
    /// <param name="jobDataType">The JobData type</param>
    /// <returns>JSON schema as string, or null if no JobData type</returns>
    public static string GenerateSchemaJson(Type jobDataType)
    {
        var schema = GenerateSchema(jobDataType);

        if (schema == null)
            return null;

        return JsonSerializer.Serialize(schema, ConstantJsonOptions.WriteNotIntendedAndIgnoreNulls);
    }

    /// <summary>
    /// Gets job data information for a job type including type name and schema.
    /// </summary>
    /// <param name="jobType">The job implementation type</param>
    /// <returns>Job data info or null if no typed JobData</returns>
    public static JobDataInfo GetJobDataInfo(Type jobType)
    {
        var jobDataType = GetJobDataType(jobType);

        if (jobDataType == null)
            return null;

        return new JobDataInfo
        {
            TypeName = jobDataType.FullName,
            TypeShortName = jobDataType.Name,
            Schema = GenerateSchema(jobDataType),
            SchemaJson = GenerateSchemaJson(jobDataType)
        };
    }

    private static Dictionary<string, object> GenerateProperties(Type type)
    {
        var properties = new Dictionary<string, object>();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite)
                continue;

            var propSchema = new Dictionary<string, object>();
            var propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            // Get JSON property name
            var jsonPropertyName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? ToCamelCase(prop.Name);

            // Set type
            propSchema["type"] = GetJsonType(propType);

            // Set format for special types
            var format = GetJsonFormat(propType);

            if (format != null)
                propSchema["format"] = format;

            // Handle dynamic enums (values from configuration)
            var dynamicEnumAttr = prop.GetCustomAttribute<DynamicEnumAttribute>();

            if (dynamicEnumAttr != null)
            {
                var dynamicValues = GetDynamicEnumValues(dynamicEnumAttr.ConfigurationKey);

                if (dynamicValues.Count > 0)
                {
                    propSchema["enum"] = dynamicValues;
                }
            }
            // Handle static enums
            else if (propType.IsEnum)
            {
                propSchema["enum"] = Enum.GetNames(propType);
            }

            // Handle arrays/lists
            if (IsArrayOrList(propType))
            {
                var elementType = GetElementType(propType);

                propSchema["items"] = new Dictionary<string, object>
                {
                    ["type"] = GetJsonType(elementType)
                };
            }

            // Handle nested objects
            if (IsComplexType(propType) && !propType.IsEnum)
            {
                propSchema["type"] = "object";
                propSchema["properties"] = GenerateProperties(propType);
            }

            // Get description from XML comments or attributes if available
            var descAttr = prop.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();

            if (descAttr != null)
                propSchema["description"] = descAttr.Description;

            // Check for required attribute
            var requiredAttr = prop.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>();

            if (requiredAttr != null)
                propSchema["required"] = true;

            // Default value
            var defaultValue = GetDefaultValue(prop);

            if (defaultValue != null)
                propSchema["default"] = defaultValue;

            properties[jsonPropertyName] = propSchema;
        }

        return properties;
    }

    private static List<string> GetRequiredProperties(Type type)
    {
        var required = new List<string>();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Check for required attribute
            if (prop.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>() != null)
            {
                var jsonPropertyName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? ToCamelCase(prop.Name);

                required.Add(jsonPropertyName);
            }

            // Check if it's a non-nullable reference type (C# 8+)
            var nullabilityContext = new NullabilityInfoContext();
            var nullabilityInfo = nullabilityContext.Create(prop);

            if (nullabilityInfo.WriteState == NullabilityState.NotNull && !prop.PropertyType.IsValueType)
            {
                var jsonPropertyName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? ToCamelCase(prop.Name);

                if (!required.Contains(jsonPropertyName))
                    required.Add(jsonPropertyName);
            }
        }

        return required;
    }

    private static string GetJsonType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid))
            return "string";

        if (type == typeof(bool))
            return "boolean";

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
            type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
            return "integer";

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return "number";

        if (IsArrayOrList(type))
            return "array";

        if (type.IsEnum)
            return "string";

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly))
            return "string";

        if (type == typeof(TimeSpan) || type == typeof(TimeOnly))
            return "string";

        return "object";
    }

    private static string GetJsonFormat(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return "date-time";

        if (type == typeof(DateOnly))
            return "date";

        if (type == typeof(TimeOnly) || type == typeof(TimeSpan))
            return "time";

        if (type == typeof(Guid))
            return "uuid";

        if (type.Name.Contains("Email", StringComparison.OrdinalIgnoreCase))
            return "email";

        if (type.Name.Contains("Uri", StringComparison.OrdinalIgnoreCase) || type == typeof(Uri))
            return "uri";

        return null;
    }

    private static bool IsArrayOrList(Type type) => type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) ||
        (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IList<>)) ||
        (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)) ||
        (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ICollection<>));

    private static Type GetElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        if (type.IsGenericType)
            return type.GetGenericArguments()[0];

        return typeof(object);
    }

    private static bool IsComplexType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return !type.IsPrimitive && type != typeof(string) && type != typeof(decimal) &&
               type != typeof(DateTime) && type != typeof(DateTimeOffset) && type != typeof(Guid) &&
               type != typeof(TimeSpan) && type != typeof(DateOnly) && type != typeof(TimeOnly) &&
               !IsArrayOrList(type);
    }

    private static object GetDefaultValue(PropertyInfo prop)
    {
        var defaultAttr = prop.GetCustomAttribute<System.ComponentModel.DefaultValueAttribute>();

        return defaultAttr?.Value;
    }

    private static string ToCamelCase(string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;

        return char.ToLowerInvariant(str[0]) + str[1..];
    }
}

/// <summary>
/// Contains job data type information and schema.
/// </summary>
public class JobDataInfo
{
    /// <summary>
    /// Full type name of the JobData class.
    /// </summary>
    public string TypeName { get; set; }

    /// <summary>
    /// Short type name of the JobData class.
    /// </summary>
    public string TypeShortName { get; set; }

    /// <summary>
    /// JSON Schema as dictionary.
    /// </summary>
    public Dictionary<string, object> Schema { get; set; }

    /// <summary>
    /// JSON Schema as string.
    /// </summary>
    public string SchemaJson { get; set; }
}
