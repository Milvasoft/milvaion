using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Attributes;
using Milvasoft.Milvaion.Sdk.Worker.Utils;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "JobDataTypeHelper unit tests.")]
public class JobDataTypeHelperTests
{
    #region GetJobDataType

    [Fact]
    public void GetJobDataType_ShouldReturnNull_ForNonGenericJob()
    {
        // Act
        var result = JobDataTypeHelper.GetJobDataType(typeof(SimpleJob));

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetJobDataType_ShouldReturnType_ForGenericIJob()
    {
        // Act
        var result = JobDataTypeHelper.GetJobDataType(typeof(TypedJob));

        // Assert
        result.Should().Be<SampleJobData>();
    }

    [Fact]
    public void GetJobDataType_ShouldReturnType_ForGenericIAsyncJob()
    {
        // Act
        var result = JobDataTypeHelper.GetJobDataType(typeof(AsyncTypedJob));

        // Assert
        result.Should().Be<SampleJobData>();
    }

    #endregion

    #region GenerateSchema

    [Fact]
    public void GenerateSchema_ShouldReturnNull_WhenTypeIsNull()
    {
        // Act
        var result = JobDataTypeHelper.GenerateSchema(null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GenerateSchema_ShouldGenerateValidSchema()
    {
        // Act
        var schema = JobDataTypeHelper.GenerateSchema(typeof(SampleJobData));

        // Assert
        schema.Should().NotBeNull();
        schema.Should().ContainKey("$schema");
        schema.Should().ContainKey("type");
        schema["type"].Should().Be("object");
        schema.Should().ContainKey("title");
        schema["title"].Should().Be(nameof(SampleJobData));
        schema.Should().ContainKey("properties");
    }

    [Fact]
    public void GenerateSchema_ShouldIncludeStringProperty()
    {
        // Act
        var schema = JobDataTypeHelper.GenerateSchema(typeof(SampleJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        // Assert
        properties.Should().ContainKey("recipient");
        var recipientSchema = (Dictionary<string, object>)properties["recipient"];
        recipientSchema["type"].Should().Be("string");
    }

    [Fact]
    public void GenerateSchema_ShouldIncludeIntegerProperty()
    {
        // Act
        var schema = JobDataTypeHelper.GenerateSchema(typeof(SampleJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        // Assert
        properties.Should().ContainKey("retryCount");
        var retrySchema = (Dictionary<string, object>)properties["retryCount"];
        retrySchema["type"].Should().Be("integer");
    }

    [Fact]
    public void GenerateSchema_ShouldIncludeBooleanProperty()
    {
        // Act
        var schema = JobDataTypeHelper.GenerateSchema(typeof(SampleJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        // Assert
        properties.Should().ContainKey("sendNotification");
        var boolSchema = (Dictionary<string, object>)properties["sendNotification"];
        boolSchema["type"].Should().Be("boolean");
    }

    [Fact]
    public void GenerateSchema_ShouldIncludeRequiredProperties()
    {
        // Act
        var schema = JobDataTypeHelper.GenerateSchema(typeof(SampleJobData));
        var required = (List<string>)schema["required"];

        // Assert
        required.Should().Contain("recipient");
    }

    [Fact]
    public void GenerateSchema_ShouldIncludeDescription_WhenDescriptionAttributePresent()
    {
        // Act
        var schema = JobDataTypeHelper.GenerateSchema(typeof(SampleJobData));
        var properties = (Dictionary<string, object>)schema["properties"];
        var recipientSchema = (Dictionary<string, object>)properties["recipient"];

        // Assert
        recipientSchema.Should().ContainKey("description");
        recipientSchema["description"].Should().Be("Email recipient");
    }

    #endregion

    #region GenerateSchemaJson

    [Fact]
    public void GenerateSchemaJson_ShouldReturnNull_WhenTypeIsNull()
    {
        // Act
        var result = JobDataTypeHelper.GenerateSchemaJson(null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GenerateSchemaJson_ShouldReturnValidJsonString()
    {
        // Act
        var json = JobDataTypeHelper.GenerateSchemaJson(typeof(SampleJobData));

        // Assert
        json.Should().NotBeNullOrWhiteSpace();
        json.Should().Contain("\"type\":\"object\"");
        json.Should().Contain("\"recipient\"");
    }

    #endregion

    #region GetJobDataInfo

    [Fact]
    public void GetJobDataInfo_ShouldReturnNull_ForNonGenericJob()
    {
        // Act
        var info = JobDataTypeHelper.GetJobDataInfo(typeof(SimpleJob));

        // Assert
        info.Should().BeNull();
    }

    [Fact]
    public void GetJobDataInfo_ShouldReturnInfo_ForTypedJob()
    {
        // Act
        var info = JobDataTypeHelper.GetJobDataInfo(typeof(TypedJob));

        // Assert
        info.Should().NotBeNull();
        info.TypeShortName.Should().Be(nameof(SampleJobData));
        info.SchemaJson.Should().NotBeNullOrWhiteSpace();
        info.Schema.Should().NotBeNull();
    }

    #endregion

    #region DynamicEnum

    [Fact]
    public void RegisterDynamicEnumValues_ShouldStoreValues()
    {
        // Arrange
        var key = "TestConfig:DynamicValues";
        var values = new[] { "Value1", "Value2", "Value3" };

        // Act
        JobDataTypeHelper.RegisterDynamicEnumValues(key, values);
        var result = JobDataTypeHelper.GetDynamicEnumValues(key);

        // Assert
        result.Should().HaveCount(3);
        result.Should().Contain("Value1");
        result.Should().Contain("Value2");
        result.Should().Contain("Value3");
    }

    [Fact]
    public void GetDynamicEnumValues_ShouldReturnEmpty_WhenKeyNotRegistered()
    {
        // Act
        var result = JobDataTypeHelper.GetDynamicEnumValues("NonExistent:Key");

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region GetJobDataType - Additional Interfaces

    [Fact]
    public void GetJobDataType_ShouldReturnType_ForIJobWithResult()
    {
        JobDataTypeHelper.GetJobDataType(typeof(ResultOnlyJob)).Should().BeNull("the type argument of a result-only job is its result");
        JobDataTypeHelper.GenerateSchema(typeof(SelfReferencingData)).Should().ContainKey("properties", "a self-referencing type must end instead of overflowing the stack");

        var result = JobDataTypeHelper.GetJobDataType(typeof(TypedJobWithResult));
        result.Should().Be<SampleJobData>();
    }

    [Fact]
    public void GetJobDataType_ShouldReturnType_ForIAsyncJobWithResult()
    {
        var result = JobDataTypeHelper.GetJobDataType(typeof(AsyncTypedJobWithResult));
        result.Should().Be<SampleJobData>();
    }

    #endregion

    #region GenerateSchema - Type Mapping

    [Fact]
    public void GenerateSchema_ShouldMapEnumProperty_ToStringWithEnumValues()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("priority");
        var prioritySchema = (Dictionary<string, object>)properties["priority"];
        prioritySchema["type"].Should().Be("string");
        prioritySchema.Should().ContainKey("enum");
    }

    [Fact]
    public void GenerateSchema_ShouldMapDateTimeProperty_WithDateTimeFormat()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("scheduledAt");
        var dateSchema = (Dictionary<string, object>)properties["scheduledAt"];
        dateSchema["type"].Should().Be("string");
        dateSchema["format"].Should().Be("date-time");
    }

    [Fact]
    public void GenerateSchema_ShouldMapGuidProperty_WithUuidFormat()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("correlationId");
        var guidSchema = (Dictionary<string, object>)properties["correlationId"];
        guidSchema["type"].Should().Be("string");
        guidSchema["format"].Should().Be("uuid");
    }

    [Fact]
    public void GenerateSchema_ShouldMapDecimalProperty_ToNumber()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("amount");
        var decimalSchema = (Dictionary<string, object>)properties["amount"];
        decimalSchema["type"].Should().Be("number");
    }

    [Fact]
    public void GenerateSchema_ShouldMapListProperty_ToArrayWithItems()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("tags");
        var listSchema = (Dictionary<string, object>)properties["tags"];
        listSchema["type"].Should().Be("array");
        listSchema.Should().ContainKey("items");
    }

    [Fact]
    public void GenerateSchema_ShouldMapNestedObjectProperty()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("nested");
        var nestedSchema = (Dictionary<string, object>)properties["nested"];
        nestedSchema["type"].Should().Be("object");
        nestedSchema.Should().ContainKey("properties");
    }

    [Fact]
    public void GenerateSchema_ShouldMapDateOnlyProperty_WithDateFormat()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("birthDate");
        var dateSchema = (Dictionary<string, object>)properties["birthDate"];
        dateSchema["format"].Should().Be("date");
    }

    [Fact]
    public void GenerateSchema_ShouldMapTimeOnlyProperty_WithTimeFormat()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("startTime");
        var timeSchema = (Dictionary<string, object>)properties["startTime"];
        timeSchema["format"].Should().Be("time");
    }

    [Fact]
    public void GenerateSchema_ShouldMapTimeSpanProperty_WithTimeFormat()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("duration");
        var tsSchema = (Dictionary<string, object>)properties["duration"];
        tsSchema["format"].Should().Be("time");
    }

    [Fact]
    public void GenerateSchema_ShouldIncludeDefaultValue_WhenDefaultValueAttributePresent()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("maxRetries");
        var retrySchema = (Dictionary<string, object>)properties["maxRetries"];
        retrySchema.Should().ContainKey("default");
        retrySchema["default"].Should().Be(3);
    }

    [Fact]
    public void GenerateSchema_ShouldRespectJsonPropertyNameAttribute()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("custom_name");
    }

    [Fact]
    public void GenerateSchema_ShouldSkipReadOnlyProperties()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().NotContainKey("readOnlyField");
    }

    [Fact]
    public void GenerateSchema_ShouldMapDateTimeOffsetProperty()
    {
        var schema = JobDataTypeHelper.GenerateSchema(typeof(FullTypeJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        properties.Should().ContainKey("createdAt");
        var dtoSchema = (Dictionary<string, object>)properties["createdAt"];
        dtoSchema["format"].Should().Be("date-time");
    }

    #endregion

    #region DynamicEnum in Schema

    [Fact]
    public void GenerateSchema_ShouldIncludeDynamicEnumValues_WhenRegistered()
    {
        // Arrange
        var configKey = "TestConfig:Connections";
        JobDataTypeHelper.RegisterDynamicEnumValues(configKey, ["Conn1", "Conn2"]);

        // Act
        var schema = JobDataTypeHelper.GenerateSchema(typeof(DynamicEnumJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        // Assert
        properties.Should().ContainKey("connectionName");
        var connSchema = (Dictionary<string, object>)properties["connectionName"];
        connSchema.Should().ContainKey("enum");
    }

    [Fact]
    public void GenerateSchema_ShouldNotIncludeEnum_WhenDynamicEnumNotRegistered()
    {
        // Act
        var schema = JobDataTypeHelper.GenerateSchema(typeof(DynamicEnumUnregisteredJobData));
        var properties = (Dictionary<string, object>)schema["properties"];

        // Assert
        properties.Should().ContainKey("unknownKey");
        var connSchema = (Dictionary<string, object>)properties["unknownKey"];
        connSchema.Should().NotContainKey("enum");
    }

    #endregion

    #region GetJobDataInfo

    [Fact]
    public void GetJobDataInfo_ShouldReturnFullInfo_ForTypedJobWithResult()
    {
        var info = JobDataTypeHelper.GetJobDataInfo(typeof(TypedJobWithResult));

        info.Should().NotBeNull();
        info.TypeShortName.Should().Be(nameof(SampleJobData));
        info.TypeName.Should().Contain("SampleJobData");
        info.SchemaJson.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region Test Types

    private sealed class ResultOnlyJob : IAsyncJobWithResult<string>
    {
        public Task<string> ExecuteAsync(IJobContext context) => Task.FromResult("done");
    }

    private sealed class SelfReferencingData
    {
        public string Name { get; set; }
        public SelfReferencingData Parent { get; set; }
    }

    private sealed class SimpleJob : IAsyncJob
    {
        public Task ExecuteAsync(IJobContext context) => Task.CompletedTask;
    }

    private sealed class TypedJob : IJob<SampleJobData>
    {
        public void Execute(IJobContext context) { }
    }

    private sealed class AsyncTypedJob : IAsyncJob<SampleJobData>
    {
        public Task ExecuteAsync(IJobContext context) => Task.CompletedTask;
    }

    private sealed class TypedJobWithResult : IJobWithResult<SampleJobData, string>
    {
        public string Execute(IJobContext context) => "done";
    }

    private sealed class AsyncTypedJobWithResult : IAsyncJobWithResult<SampleJobData, string>
    {
        public Task<string> ExecuteAsync(IJobContext context) => Task.FromResult("done");
    }

    public class SampleJobData
    {
        [Required]
        [Description("Email recipient")]
        public string Recipient { get; set; }

        public int RetryCount { get; set; }

        public bool SendNotification { get; set; }
    }

    public class FullTypeJobData
    {
        public SamplePriority Priority { get; set; }
        public DateTime ScheduledAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public Guid CorrelationId { get; set; }
        public decimal Amount { get; set; }
        public List<string> Tags { get; set; }
        public NestedData Nested { get; set; }
        public DateOnly BirthDate { get; set; }
        public TimeOnly StartTime { get; set; }
        public TimeSpan Duration { get; set; }

        [DefaultValue(3)]
        public int MaxRetries { get; set; }

        [JsonPropertyName("custom_name")]
        public string CustomProp { get; set; }

        public string ReadOnlyField { get; }
    }

    public class NestedData
    {
        public string Value { get; set; }
    }

    public enum SamplePriority
    {
        Low,
        Medium,
        High
    }

    public class DynamicEnumJobData
    {
        [DynamicEnum("TestConfig:Connections")]
        public string ConnectionName { get; set; }
    }

    public class DynamicEnumUnregisteredJobData
    {
        [DynamicEnum("NotRegistered:Key")]
        public string UnknownKey { get; set; }
    }

    #endregion
}
